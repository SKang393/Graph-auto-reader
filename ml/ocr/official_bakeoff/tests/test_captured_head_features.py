# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from hashlib import sha256
import json
from pathlib import Path

import numpy as np
import pytest

from ml.ocr.official_bakeoff import captured_head_features as subject


def _capture(root: Path) -> tuple[Path, dict]:
    root.mkdir(exist_ok=True)
    raw = np.zeros((1, 3, 128, 128), dtype="<f4").tobytes()
    (root / "input.f32").write_bytes(raw)
    report = {
        "schema": "graphreader.official-head-tensor-capture-report.v1",
        "scope": "project-owned-synthetic-train-dev-model-free",
        "synthetic_only": True, "private_data": False, "sealed_data": False,
        "truth_used_by_capture": False, "model_inference": False,
        "training_input_ready": False, "production_approved": False,
        "detector_model_sha256": subject.head_api.REVIEWED_DETECTOR_SHA256,
        "maximum_side_length": 960, "dimension_multiple": 128,
        "panel_count": 37, "failed_panel_count": 0,
        "panels": [{
            "panel_id": f"panel-{i}", "split": "train" if i < 28 else "validation",
            "tensor": {"shape": [1, 3, 128, 128], "file": "input.f32",
                       "sha256": sha256(raw).hexdigest(), "byte_count": len(raw),
                       "dtype": "float32-le", "input_name": "x", "output_name": "fetch_name_0"},
        } for i in range(37)],
    }
    return root / "capture.json", report


def _write(path: Path, report: dict) -> str:
    raw = json.dumps(report).encode("utf-8")
    path.write_bytes(raw)
    return sha256(raw).hexdigest()


def test_complete_handwritten_capture_retains_all_train_and_dev_tensors(tmp_path: Path) -> None:
    path, report = _capture(tmp_path)
    loaded, tensors = subject.load_capture_tensors(path, _write(path, report))
    assert loaded == report
    assert len(tensors) == 37
    assert sum(row["split"] == "train" for row, _ in tensors) == 28
    assert all(values.shape == (1, 3, 128, 128) for _, values in tensors)


@pytest.mark.parametrize("mutation", [
    lambda d: d.update(sealed_data=True),
    lambda d: d.update(private_data=True),
    lambda d: d.update(truth_used_by_capture=True),
    lambda d: d.update(model_inference=True),
    lambda d: d.update(panel_count=True),
    lambda d: d["panels"].pop(),
    lambda d: d["panels"][0].update(split="validation"),
    lambda d: d["panels"][0].update(panel_id="panel-1"),
    lambda d: d["panels"][0].update(panel_id="../escape"),
    lambda d: d["panels"][0]["tensor"].update(file="../escape.f32"),
    lambda d: d["panels"][0]["tensor"].update(shape=[1, 3, 128, 2048]),
    lambda d: d["panels"][0]["tensor"].update(byte_count=0),
    lambda d: d["panels"][0]["tensor"].update(dtype="float64-le"),
    lambda d: d["panels"][0]["tensor"].update(output_name="different"),
])
def test_invalid_capture_fails_before_model_loading(tmp_path: Path, mutation) -> None:
    path, report = _capture(tmp_path)
    mutation(report)
    with pytest.raises(subject.CapturedFeatureError):
        subject.load_capture_tensors(path, _write(path, report))


def test_nonfinite_and_tampered_tensor_bytes_rejected(tmp_path: Path) -> None:
    path, report = _capture(tmp_path)
    expected = _write(path, report)
    tensor = tmp_path / "input.f32"
    values = np.zeros((1, 3, 128, 128), dtype="<f4")
    values.flat[1] = np.nan
    raw = values.tobytes()
    tensor.write_bytes(raw)
    with pytest.raises(subject.CapturedFeatureError, match="bytes changed"):
        subject.load_capture_tensors(path, expected)
    for row in report["panels"]:
        row["tensor"]["sha256"] = sha256(raw).hexdigest()
    with pytest.raises(subject.CapturedFeatureError, match="non-finite"):
        subject.load_capture_tensors(path, _write(path, report))


def test_duplicate_json_and_report_tampering_rejected(tmp_path: Path) -> None:
    path, report = _capture(tmp_path)
    expected = _write(path, report)
    path.write_bytes(path.read_bytes() + b" ")
    with pytest.raises(subject.CapturedFeatureError, match="bytes changed"):
        subject.load_capture_tensors(path, expected)
    raw = b'{"schema":"one","schema":"two"}'
    path.write_bytes(raw)
    with pytest.raises(subject.CapturedFeatureError, match="Duplicate"):
        subject.load_capture_tensors(path, sha256(raw).hexdigest())


def test_model_failure_restores_training_process_settings(tmp_path: Path, monkeypatch) -> None:
    import torch

    artifacts = tmp_path / "artifacts"
    artifacts.mkdir()
    model = artifacts / "parent.onnx"
    model.write_bytes(b"handwritten non-model fixture")
    monkeypatch.setattr(subject.head_api, "REVIEWED_DETECTOR_SHA256", sha256(model.read_bytes()).hexdigest())
    path, report = _capture(artifacts / "capture")
    digest = _write(path, report)
    before = (torch.get_num_threads(), torch.backends.mkldnn.enabled,
              torch.are_deterministic_algorithms_enabled(),
              torch.is_deterministic_algorithms_warn_only_enabled())

    def fail_before_model_read(*args, **kwargs):
        assert torch.get_num_threads() == 1
        assert torch.backends.mkldnn.enabled is False
        assert torch.are_deterministic_algorithms_enabled()
        raise RuntimeError("handwritten initialization failure")

    monkeypatch.setattr(subject.head_api, "extract_frozen_trunk_head", fail_before_model_read)
    with pytest.raises(RuntimeError, match="handwritten initialization failure"):
        subject.capture_features(
            repository_root=tmp_path, parent_model=model, capture_report=path,
            capture_sha256=digest, output_directory=artifacts / "features",
        )
    assert before == (torch.get_num_threads(), torch.backends.mkldnn.enabled,
                      torch.are_deterministic_algorithms_enabled(),
                      torch.is_deterministic_algorithms_warn_only_enabled())
    assert not (artifacts / "features" / "report.json").exists()
