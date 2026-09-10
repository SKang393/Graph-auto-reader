# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import json
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import numpy as np
import pytest
import torch

from ml.markers.gate_seal import canonical_json_bytes
from ml.ocr.official_bakeoff import frozen_head_training, frozen_trunk_head, production_head_inputs
from ml.ocr.pretrained_db_head_v39 import train_p1 as subject


def _hash(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _write(path: Path, payload: bytes) -> str:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    return sha256(payload).hexdigest()


def _immutable(shape: tuple[int, ...], value: float = 0.0) -> np.ndarray:
    data = np.full(shape, value, dtype=np.float32)
    return np.frombuffer(data.tobytes(), dtype=np.float32).reshape(shape)


@dataclass
class Fixture:
    root: Path
    config_path: Path
    parity_path: Path
    capture_path: Path
    inputs: production_head_inputs.ProductionHeadInputs
    dependencies: subject.TrainingDependencies
    loaded: list[bool]
    acquired: list[bool]
    voided: list[BaseException]


def _fixture(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Fixture:
    root = tmp_path.resolve()
    parent_sha = _write(root / "models/parent.onnx", b"parent-model")
    monkeypatch.setattr(subject, "PARENT_MODEL_SHA256", parent_sha)
    monkeypatch.setattr(subject.production_head_inputs, "V3_BINDING_SHA256", "b" * 64)
    monkeypatch.setattr(subject, "source_bundle_sha256", lambda *_: "a" * 64)
    _write(root / "binding.json", b"binding")
    head_source_sha = _write(
        root / "ml/ocr/official_bakeoff/frozen_trunk_head.py", b"head-source"
    )
    parity_source_sha = _write(root / "artifacts/captured_parity_v2.py", b"parity-source")
    feature_model_sha = _write(root / "artifacts/features.onnx", b"feature-model")

    capture_rows: list[dict[str, Any]] = []
    parity_rows: list[dict[str, Any]] = []
    train_panels = []
    validation_panels = []
    for index in range(37):
        split = "train" if index < 28 else "validation"
        panel_id = f"panel-{index:02d}"
        tensor_sha = sha256(f"tensor-{index}".encode()).hexdigest()
        feature = _immutable((1, 96, 1, 1), index / 100.0)
        feature_name = f"{panel_id}.features.f32"
        feature_sha = _write(root / "artifacts/parity" / feature_name, feature.tobytes())
        capture_rows.append({
            "split": split,
            "panel_id": panel_id,
            "tensor": {"sha256": tensor_sha, "shape": [1, 3, 4, 4]},
        })
        parity_rows.append({
            "split": split,
            "panel_id": panel_id,
            "input_sha256": tensor_sha,
            "shape": [1, 3, 4, 4],
            "feature_file": feature_name,
            "feature_shape": [1, 96, 1, 1],
            "feature_sha256": feature_sha,
            "maximum_absolute_error": 0.0,
        })
        panel = production_head_inputs.ProductionHeadPanel(
            split,
            f"source-{index}",
            panel_id,
            f"panel-sha-{index}",
            4,
            4,
            (0, 0, 4, 4),
            (1, 3, 4, 4),
            _immutable((1, 3, 4, 4)),
            _immutable((1, 4, 4), 1.0 if index == 0 else 0.0),
            _immutable((1, 4, 4), 1.0),
            (),
            (),
            tensor_sha,
            "c" * 64,
        )
        (train_panels if split == "train" else validation_panels).append(panel)
    capture_path = root / "artifacts/capture/report.json"
    capture_sha = _write(capture_path, canonical_json_bytes({"panels": capture_rows}))
    parity = {
        "schema": subject.PARITY_SCHEMA,
        "source_model_sha256": parent_sha,
        "source_code_sha256": head_source_sha,
        "helper_sha256": parity_source_sha,
        "capture_report_sha256": capture_sha,
        "feature_model_sha256": feature_model_sha,
        "no_op_model_sha256": parent_sha,
        "provider": "CPUExecutionProvider",
        "cpu_threads": 1,
        "torch_mkldnn_enabled": False,
        "panels": parity_rows,
        "panel_count": 37,
        "maximum_absolute_error": 0.0,
        "tolerance": subject.PARITY_TOLERANCE,
        "passed": True,
        "optimizer_steps": 0,
        "private_reads": 0,
        "sealed_reads": 0,
    }
    parity_path = root / "artifacts/parity/report.json"
    parity_sha = _write(parity_path, canonical_json_bytes(parity))
    inputs = production_head_inputs.ProductionHeadInputs(
        "b" * 64,
        capture_sha,
        production_head_inputs.ProductionHeadSplit(
            "train", 20, 28, 709, 709, 0, 0, 0, 0, 0, (), tuple(train_panels)
        ),
        production_head_inputs.ProductionHeadSplit(
            "validation", 3, 9, 183, 183, 0, 0, 0, 0, 0, (), tuple(validation_panels)
        ),
    )
    config = {
        "schema": subject.CONFIG_SCHEMA,
        "task": subject.TASK,
        "revision": subject.REVISION,
        "candidate_id": subject.CANDIDATE_ID,
        "stage": "P1",
        "expected_runner_source_bundle_sha256": "a" * 64,
        "parent_model": {"path": "models/parent.onnx", "sha256": parent_sha},
        "v3_binding": {"path": "binding.json", "sha256": "b" * 64},
        "capture_report": {"path": "artifacts/capture/report.json", "sha256": capture_sha},
        "captured_parity_report": {"path": "artifacts/parity/report.json", "sha256": parity_sha},
        "captured_parity_source": {
            "path": "artifacts/captured_parity_v2.py", "sha256": parity_source_sha,
        },
        "head_source": {
            "path": "ml/ocr/official_bakeoff/frozen_trunk_head.py", "sha256": head_source_sha,
        },
        "feature_model": {"path": "artifacts/features.onnx", "sha256": feature_model_sha},
        "recipe": dict(subject._RECIPE),
        "expected_data": dict(subject._EXPECTED_DATA),
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "production_approval": False,
    }
    config_path = root / "config.json"
    _write(config_path, canonical_json_bytes(config))
    loaded: list[bool] = []
    acquired: list[bool] = []
    voided: list[BaseException] = []

    def load_inputs(*args, **kwargs):
        loaded.append(True)
        return inputs

    def acquire(*args, **kwargs):
        acquired.append(True)
        snapshot = root / "snapshot.json"
        snapshot.write_text("{}", encoding="utf-8")
        return SimpleNamespace(snapshot_path=snapshot, binding={"bound": True})

    def extract(*args, **kwargs):
        return SimpleNamespace(head=torch.nn.Linear(1, 1))

    def train(head, panels):
        epochs = tuple(
            frozen_head_training.EpochLoss(
                epoch=index,
                optimizer_steps=index * 28,
                training_step_mean_loss=0.5,
                full_train_loss=0.5,
                full_positive_dice_loss=0.5,
                full_empty_negative_loss=0.5,
                draw_order_sha256="f" * 64,
            )
            for index in range(1, 61)
        )
        return frozen_head_training.TrainingResult(
            optimizer_steps=1680,
            selected_epoch=1,
            epochs=60,
            epoch_losses=epochs,
            panel_count=28,
            positive_panel_count=1,
            empty_positive_panels=27,
            seed=20260939,
            batch_size=1,
            learning_rate=1e-4,
            weight_decay=1e-4,
            dice_epsilon=1e-6,
            torch_backend=frozen_head_training.TORCH_BACKEND,
            trainable_parameter_names=frozen_head_training.TRAINABLE_PARAMETER_NAMES,
            frozen_batch_norm_sha256_before="d" * 64,
            frozen_batch_norm_sha256_after="d" * 64,
            selected_trainable_state_sha256="e" * 64,
        )

    def patch(source, output, head, expected):
        output.write_bytes(b"patched-onnx")
        return frozen_trunk_head.PatchResult(
            parent_sha,
            _hash(output),
            tuple(frozen_trunk_head.TRAINABLE_CONSTANT_SHAPES),
            (),
        )

    dependencies = subject.TrainingDependencies(
        load_inputs,
        extract,
        train,
        patch,
        acquire,
        lambda *args: None,
        lambda authorization, error: voided.append(error) or root / "void.json",
    )
    return Fixture(root, config_path, parity_path, capture_path, inputs, dependencies,
                   loaded, acquired, voided)


def _rewrite_parity(fixture: Fixture, mutate) -> None:
    parity = json.loads(fixture.parity_path.read_text(encoding="utf-8"))
    mutate(parity)
    fixture.parity_path.write_bytes(canonical_json_bytes(parity))
    config = json.loads(fixture.config_path.read_text(encoding="utf-8"))
    config["captured_parity_report"]["sha256"] = _hash(fixture.parity_path)
    fixture.config_path.write_bytes(canonical_json_bytes(config))


def test_failed_captured_parity_is_rejected_before_truth_regeneration(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    fixture = _fixture(tmp_path, monkeypatch)
    _rewrite_parity(fixture, lambda value: (
        value["panels"][0].__setitem__("maximum_absolute_error", 2.1e-5),
        value.__setitem__("maximum_absolute_error", 2.1e-5),
        value.__setitem__("passed", True),
    ))

    with pytest.raises(subject.V39TrainingError, match="fails the fixed"):
        subject.prepare_training(
            fixture.config_path, repository_root=fixture.root, dependencies=fixture.dependencies
        )
    assert fixture.loaded == []


def test_runner_source_drift_fails_before_candidate_acquisition(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    fixture = _fixture(tmp_path, monkeypatch)
    monkeypatch.setattr(subject, "source_bundle_sha256", lambda *_: "e" * 64)
    with pytest.raises(subject.V39TrainingError, match="source bundle"):
        subject.train_candidate(
            fixture.root / "never-created",
            fixture.config_path,
            repository_root=fixture.root,
            dependencies=fixture.dependencies,
        )
    assert fixture.acquired == []
    assert not (fixture.root / "never-created").exists()


@pytest.mark.parametrize("field", ["torch_mkldnn_enabled", "feature_sha256", "input_sha256"])
def test_backend_and_cached_feature_identity_fail_before_truth(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, field: str
) -> None:
    fixture = _fixture(tmp_path, monkeypatch)

    def mutate(value):
        if field == "torch_mkldnn_enabled":
            value[field] = True
        else:
            value["panels"][0][field] = "f" * 64

    _rewrite_parity(fixture, mutate)
    with pytest.raises(subject.V39TrainingError):
        subject.prepare_training(
            fixture.config_path, repository_root=fixture.root, dependencies=fixture.dependencies
        )
    assert fixture.loaded == []


def test_preparation_retains_full_truth_but_creates_training_panels_only_for_train(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    fixture = _fixture(tmp_path, monkeypatch)
    prepared = subject.prepare_training(
        fixture.config_path, repository_root=fixture.root, dependencies=fixture.dependencies
    )
    assert len(prepared.training_panels) == 28
    assert {panel.split for panel in prepared.training_panels} == {"train"}
    assert prepared.train_truth_count == 709
    assert prepared.validation_truth_count == 183
    assert prepared.validation_panel_count == 9
    assert fixture.loaded == [True]


def test_train_stage_writes_state_only_exact_nine_patch_and_leaves_evaluation_pending(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    fixture = _fixture(tmp_path, monkeypatch)
    output = fixture.root / "run"
    report = subject.train_candidate(
        output,
        fixture.config_path,
        repository_root=fixture.root,
        dependencies=fixture.dependencies,
    )
    assert fixture.acquired == [True]
    assert fixture.voided == []
    assert report["status"] == "training_complete_pending_actual_csharp_dev_evaluation"
    assert report["actual_csharp_dev_evaluation"] == "pending"
    assert report["result_json_written"] is False
    assert report["canonical_ledger_updated"] is False
    assert report["training_result"]["torch_backend"] == "cpu-mkldnn-disabled-float64-conv0"
    assert report["onnx"]["changed_constants"] == list(frozen_trunk_head.TRAINABLE_CONSTANT_SHAPES)
    checkpoint = torch.load(output / subject.CHECKPOINT_NAME, weights_only=True)
    assert set(checkpoint) == {"weight", "bias"}
    assert (output / subject.STAGE_NAME).read_bytes() == canonical_json_bytes(report)


def test_presealed_failure_is_voided_and_unknown_steps_are_not_reported_as_zero(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    fixture = _fixture(tmp_path, monkeypatch)

    def fail_train(*args, **kwargs):
        raise RuntimeError("fixture optimizer failure")

    fixture.dependencies = subject.TrainingDependencies(
        fixture.dependencies.load_inputs,
        fixture.dependencies.extract_head,
        fail_train,
        fixture.dependencies.patch_head,
        fixture.dependencies.acquire,
        fixture.dependencies.verify_snapshot,
        fixture.dependencies.void,
    )
    output = fixture.root / "failed"
    with pytest.raises(RuntimeError, match="fixture optimizer failure"):
        subject.train_candidate(
            output,
            fixture.config_path,
            repository_root=fixture.root,
            dependencies=fixture.dependencies,
        )
    failure = json.loads((output / subject.STAGE_NAME).read_text())
    assert failure["optimizer_steps"] is None
    assert failure["optimizer_steps_known"] is False
    assert failure["phase"] == "optimization"
    assert len(fixture.voided) == 1
