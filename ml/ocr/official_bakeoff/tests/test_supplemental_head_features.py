# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from copy import deepcopy
from hashlib import sha256
import json
from pathlib import Path

import numpy as np
import pytest

from ml.ocr.official_bakeoff import supplemental_head_features as subject


def _write(path: Path, value: dict) -> str:
    raw = json.dumps(value).encode("utf-8")
    path.write_bytes(raw)
    return sha256(raw).hexdigest()


def _capture(root: Path) -> tuple[Path, dict]:
    tensor_root = root / "train"
    tensor_root.mkdir(parents=True)
    raw = np.zeros((1, 3, 128, 128), dtype="<f4").tobytes()
    tensor = tensor_root / "shared.f32"
    tensor.write_bytes(raw)
    report = {
        "schema": "graphreader.supplemental-official-head-tensor-capture-report.v1",
        "scope": "project-owned-synthetic-train-only-supplemental-model-free",
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "truth_used_by_capture": False,
        "model_inference": False,
        "training_input_ready": False,
        "production_approved": False,
        "request": {"path": subject.REQUEST_PATH.as_posix(), "sha256": subject.REQUEST_SHA256},
        "detector_model_sha256": subject.head_api.REVIEWED_DETECTOR_SHA256,
        "capture_source_sha256": subject.CAPTURE_SOURCE_SHA256,
        "maximum_side_length": 960,
        "dimension_multiple": 128,
        "panel_count": 6,
        "failed_panel_count": 0,
        "assemblies": [
            {"name": name, "path": path.as_posix(), "sha256": digest}
            for name, path, digest in subject.ASSEMBLIES
        ],
        "panels": [
            {
                "split": "train",
                "panel_id": f"panel-{index}",
                "tensor": {
                    "file": "train/shared.f32",
                    "sha256": sha256(raw).hexdigest(),
                    "byte_count": len(raw),
                    "shape": [1, 3, 128, 128],
                    "dtype": "float32-le",
                    "input_name": subject.head_api.MODEL_INPUT_NAME,
                    "output_name": subject.head_api.MODEL_OUTPUT_NAME,
                },
            }
            for index in range(6)
        ],
    }
    return root / "report.json", report


def _composition() -> dict:
    return {
        "schema": "graphreader.supplemental-legend-head-input-verification.v1",
        "scope": "project-owned-synthetic-train-only-supplemental-model-free",
        "status": "complete_authenticated_composition",
        "acceptance_gate_changed": False,
        "historical_train_objects_unchanged": True,
        "supplemental_request_exactly_reconstructed": True,
        "model_inference_runs": 0,
        "optimizer_steps": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "production_approved": False,
        "training_authorized": False,
        "historical_train": {"panel_count": 28, "source_count": 20, "truth_count": 709},
        "fixed_development": {
            "panel_count": 9,
            "same_object": True,
            "source_count": 3,
            "tensor_hashes_unchanged": True,
            "truth_count": 183,
        },
        "combined_train": {"panel_count": 34, "source_count": 26, "truth_count": 862},
        "supplemental_train": {"panel_count": 6, "source_count": 6, "truth_count": 153},
        "artifact_bindings": [
            {
                "name": "historical_tensor_report",
                "path": "artifacts/goal22-runs/ocr-text-extent-tensors/report.json",
                "sha256": subject.HISTORICAL_CAPTURE_SHA256,
            },
            {"name": "supplemental_request", "path": subject.REQUEST_PATH.as_posix(), "sha256": subject.REQUEST_SHA256},
            {"name": "supplemental_capture_report", "path": subject.CAPTURE_PATH.as_posix(), "sha256": subject.CAPTURE_SHA256},
        ],
        "supplemental_capture_source": {
            "path": subject.CAPTURE_SOURCE_PATH.as_posix(),
            "sha256": subject.CAPTURE_SOURCE_SHA256,
        },
        "supplemental_capture_assemblies": [
            {"name": name, "path": path.as_posix(), "sha256": digest}
            for name, path, digest in subject.ASSEMBLIES
        ],
    }


def test_valid_six_train_panel_capture_is_loaded(tmp_path: Path) -> None:
    path, report = _capture(tmp_path)
    loaded, tensors = subject._load_capture_tensors(
        capture_path=path, expected_sha256=_write(path, report)
    )
    assert loaded == report
    assert len(tensors) == 6
    assert all(row["split"] == "train" for row, _ in tensors)
    assert all(values.shape == (1, 3, 128, 128) for _, values in tensors)
    assert all(values.dtype == np.float32 and np.isfinite(values).all() for _, values in tensors)


@pytest.mark.parametrize(
    "mutation",
    [
        lambda report: report.update(schema="changed"),
        lambda report: report.update(panel_count=5),
        lambda report: report["panels"].pop(),
        lambda report: report["panels"][0].update(split="validation"),
        lambda report: report["panels"][0].update(panel_id="panel-1"),
        lambda report: report["panels"][0].update(panel_id="../escape"),
        lambda report: report["panels"][0]["tensor"].update(file="../escape.f32"),
        lambda report: report["panels"][0]["tensor"].update(shape=[1, 1, 128, 128]),
        lambda report: report["panels"][0]["tensor"].update(byte_count=1),
        lambda report: report["panels"][0]["tensor"].update(dtype="float64-le"),
        lambda report: report["panels"][0]["tensor"].update(input_name="changed"),
        lambda report: report["panels"][0]["tensor"].update(output_name="changed"),
    ],
)
def test_capture_schema_split_path_shape_count_and_names_rejected(
    tmp_path: Path, mutation,
) -> None:
    path, report = _capture(tmp_path)
    mutation(report)
    with pytest.raises(subject.SupplementalFeatureError):
        subject._load_capture_tensors(capture_path=path, expected_sha256=_write(path, report))


def test_tensor_hash_tampering_and_nonfinite_values_rejected(tmp_path: Path) -> None:
    path, report = _capture(tmp_path)
    digest = _write(path, report)
    tensor = tmp_path / "train" / "shared.f32"
    values = np.zeros((1, 3, 128, 128), dtype="<f4")
    values.flat[0] = np.nan
    raw = values.tobytes()
    tensor.write_bytes(raw)
    with pytest.raises(subject.SupplementalFeatureError, match="bytes changed"):
        subject._load_capture_tensors(capture_path=path, expected_sha256=digest)
    for row in report["panels"]:
        row["tensor"]["sha256"] = sha256(raw).hexdigest()
    with pytest.raises(subject.SupplementalFeatureError, match="non-finite"):
        subject._load_capture_tensors(capture_path=path, expected_sha256=_write(path, report))


@pytest.mark.parametrize(
    "mutation",
    [
        lambda report: report.update(schema="changed"),
        lambda report: report.update(optimizer_steps=1),
        lambda report: report.update(private_reads=1),
        lambda report: report.update(sealed_reads=1),
        lambda report: report.update(training_authorized=True),
        lambda report: report["supplemental_train"].update(panel_count=5),
        lambda report: report["fixed_development"].update(panel_count=0),
        lambda report: report["artifact_bindings"][1].update(sha256="0" * 64),
        lambda report: report["supplemental_capture_source"].update(path="other.cs"),
        lambda report: report["supplemental_capture_assemblies"].pop(),
    ],
)
def test_composition_schema_counts_hashes_and_identities_rejected(mutation) -> None:
    report = _composition()
    mutation(report)
    with pytest.raises(subject.SupplementalFeatureError):
        subject._validate_composition(report)


def test_complete_composition_is_accepted() -> None:
    report = deepcopy(_composition())
    subject._validate_composition(report)


def _feature_report() -> dict:
    return {
        "schema": subject.SCHEMA,
        "scope": "project-owned-synthetic-train-only-supplemental-model-free",
        "composition_report_sha256": subject.COMPOSITION_SHA256,
        "capture_report_sha256": subject.CAPTURE_SHA256,
        "capture_request_sha256": subject.REQUEST_SHA256,
        "capture_source_sha256": subject.CAPTURE_SOURCE_SHA256,
        "historical_capture_sha256": subject.HISTORICAL_CAPTURE_SHA256,
        "capture_assemblies": [
            {"name": name, "path": path.as_posix(), "sha256": digest}
            for name, path, digest in subject.ASSEMBLIES
        ],
        "source_model_sha256": subject.head_api.REVIEWED_DETECTOR_SHA256,
        "source_code_sha256": subject.FROZEN_HEAD_SOURCE_SHA256,
        "helper_sha256": "2" * 64,
        "feature_model_sha256": subject.FEATURE_MODEL_SHA256,
        "no_op_model_sha256": subject.head_api.REVIEWED_DETECTOR_SHA256,
        "provider": "CPUExecutionProvider",
        "torch_head_arithmetic": "float64",
        "cpu_threads": 1,
        "torch_mkldnn_enabled": False,
        "graph_optimization": "ORT_ENABLE_ALL",
        "panel_count": 6,
        "train_panel_count": 6,
        "development_panel_count": 0,
        "maximum_absolute_error": 1e-6,
        "tolerance": subject.TOLERANCE,
        "passed": True,
        "optimizer_steps": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "production_approved": False,
    }


def test_feature_report_requires_pinned_model_and_zero_read_counts() -> None:
    subject._validate_feature_report(_feature_report())
    for key, value in (
        ("feature_model_sha256", "0" * 64),
        ("optimizer_steps", 1),
        ("private_reads", 1),
        ("sealed_reads", 1),
        ("development_panel_count", 1),
        ("maximum_absolute_error", subject.TOLERANCE * 2),
    ):
        report = _feature_report()
        report[key] = value
        with pytest.raises(subject.SupplementalFeatureError):
            subject._validate_feature_report(report)


def _feature_rows(capture: dict) -> list[dict]:
    rows = []
    for index, panel in enumerate(capture["panels"]):
        rows.append(
            {
                "split": "train",
                "panel_id": panel["panel_id"],
                "input_sha256": panel["tensor"]["sha256"],
                "input_shape": panel["tensor"]["shape"],
                "feature_file": panel["panel_id"] + ".features.f32",
                "feature_shape": [1, subject.head_api.FEATURE_CHANNELS, 32, 32],
                "feature_sha256": f"{index + 1:x}" * 64,
                "feature_byte_count": subject.head_api.FEATURE_CHANNELS * 32 * 32 * 4,
                "feature_dtype": "float32-le",
                "maximum_absolute_error": float(index) * 1e-7,
            }
        )
    return rows


def test_cached_inventory_is_exact_bijection_with_capture(tmp_path: Path) -> None:
    _, capture = _capture(tmp_path)
    report = _feature_report()
    report["panels"] = _feature_rows(capture)
    report["maximum_absolute_error"] = 5e-7
    validated = subject._validate_cached_inventory(report, capture)
    assert len(validated) == 6

    wrong_input = deepcopy(report)
    wrong_input["panels"][0]["input_sha256"] = "0" * 64
    with pytest.raises(subject.SupplementalFeatureError, match="authenticated capture"):
        subject._validate_cached_inventory(wrong_input, capture)

    wrong_shape = deepcopy(report)
    wrong_shape["panels"][0]["input_shape"] = [1, 3, 128, 256]
    wrong_shape["panels"][0]["feature_shape"] = [1, subject.head_api.FEATURE_CHANNELS, 32, 64]
    with pytest.raises(subject.SupplementalFeatureError, match="authenticated capture"):
        subject._validate_cached_inventory(wrong_shape, capture)

    wrong_maximum = deepcopy(report)
    wrong_maximum["maximum_absolute_error"] = 4e-7
    with pytest.raises(subject.SupplementalFeatureError, match="Global parity maximum"):
        subject._validate_cached_inventory(wrong_maximum, capture)


def test_output_must_be_fresh_before_authentication(tmp_path: Path) -> None:
    output = tmp_path / "artifacts" / "existing"
    output.mkdir(parents=True)
    with pytest.raises(subject.SupplementalFeatureError, match="new artifacts"):
        subject.capture_features(
            repository_root=tmp_path,
            parent_model=Path("model.onnx"),
            output_directory=Path("artifacts/existing"),
        )
