# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest
import torch

from ml.markers.gate_seal import canonical_json_bytes
from ml.ocr.inverse_db_target_v40 import train_p1 as subject
from ml.ocr.official_bakeoff import frozen_head_training, frozen_trunk_head


def _immutable(shape: tuple[int, ...], value: float = 0.0) -> np.ndarray:
    data = np.full(shape, value, dtype=np.float32)
    return np.frombuffer(data.tobytes(), dtype=np.float32).reshape(shape)


def _write(path: Path, payload: bytes) -> str:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    return sha256(payload).hexdigest()


def _target_fixture(
    root: Path,
) -> tuple[tuple[frozen_head_training.TrainingPanel, ...], dict, list[Path]]:
    base: list[frozen_head_training.TrainingPanel] = []
    records: list[dict] = []
    target_paths: list[Path] = []
    projection_serial = 0
    for index in range(37):
        split = "train" if index < 28 else "validation"
        panel_id = f"panel-{index:02d}"
        target = np.zeros((1, 2, 3), dtype="<f4")
        target[0, index % 2, index % 3] = 1
        target_path = root / "targets" / f"{panel_id}.f32"
        target_sha = _write(target_path, target.tobytes())
        target_paths.append(target_path)
        count = (709 // 28 + (index < (709 % 28))) if split == "train" else (
            183 // 9 + ((index - 28) < (183 % 9))
        )
        projections = []
        for _ in range(count):
            projections.append({
                "truth_id": sha256(f"truth-{projection_serial}".encode()).hexdigest(),
                "panel_box": [0.0, 0.0, 1.0, 1.0],
            })
            projection_serial += 1
        records.append({
            "panel_id": panel_id,
            "split": split,
            "width": 3,
            "height": 2,
            "tensor_width": 3,
            "tensor_height": 2,
            "target": {"path": target_path.relative_to(root).as_posix(), "sha256": target_sha},
            "source_sha256": "a" * 64,
            "panel_to_source_matrix": [1, 0, 0, 1, 0, 0],
            "projections": projections,
        })
        if split == "train":
            mask = np.ones((1, 2, 3), dtype=np.float32)
            if index == 0:
                mask[0, 0, 0] = 0
            base.append(frozen_head_training.TrainingPanel(
                panel_id, "train", _immutable((1, 96, 1, 1), index),
                _immutable((1, 2, 3)), _immutable((1, 2, 3), 1.0) if index else mask,
            ))
    return tuple(base), {"panels": records}, target_paths


def _training_result() -> frozen_head_training.TrainingResult:
    epochs = tuple(
        frozen_head_training.EpochLoss(
            epoch=index,
            optimizer_steps=index * 28,
            training_step_mean_loss=0.5,
            full_train_loss=0.5,
            full_positive_dice_loss=0.5,
            full_empty_negative_loss=0.5,
            draw_order_sha256="e" * 64,
        )
        for index in range(1, 61)
    )
    return frozen_head_training.TrainingResult(
        optimizer_steps=1680,
        selected_epoch=1,
        epochs=60,
        epoch_losses=epochs,
        panel_count=28,
        positive_panel_count=28,
        empty_positive_panels=0,
        seed=20260939,
        batch_size=1,
        learning_rate=1e-4,
        weight_decay=1e-4,
        dice_epsilon=1e-6,
        torch_backend=frozen_head_training.TORCH_BACKEND,
        trainable_parameter_names=frozen_head_training.TRAINABLE_PARAMETER_NAMES,
        frozen_batch_norm_sha256_before="b" * 64,
        frozen_batch_norm_sha256_after="b" * 64,
        selected_trainable_state_sha256="c" * 64,
    )


def test_inverse_targets_replace_only_train_labels_and_rebuild_all_one_masks(
    tmp_path: Path,
) -> None:
    base, request, _ = _target_fixture(tmp_path)

    panels, inventory, rebuilt_pixels, previously_masked = subject._replace_training_targets(
        tmp_path, base, request
    )

    assert tuple(panel.panel_id for panel in panels) == tuple(panel.panel_id for panel in base)
    assert all(panel.split == "train" for panel in panels)
    assert all(panel.features is original.features for panel, original in zip(panels, base))
    assert all(np.all(panel.mask == 1) for panel in panels)
    assert all(panel.mask.flags.writeable is False for panel in panels)
    assert panels[0].target[0, 0, 0] == 1
    assert rebuilt_pixels == 28 * 6
    assert previously_masked == 1
    assert len(inventory) == 64
    with pytest.raises(ValueError):
        panels[0].mask.setflags(write=True)


@pytest.mark.parametrize("mutation", ["missing", "reordered", "changed-bytes"])
def test_inverse_target_inventory_fails_closed(
    tmp_path: Path, mutation: str
) -> None:
    base, request, target_paths = _target_fixture(tmp_path)
    if mutation == "missing":
        request["panels"].pop()
    elif mutation == "reordered":
        request["panels"][0], request["panels"][1] = request["panels"][1], request["panels"][0]
    else:
        target_paths[0].write_bytes(b"changed")

    with pytest.raises(subject.V40TrainingError):
        subject._replace_training_targets(tmp_path, base, request)


def test_fixed_recipe_preserves_v39_optimizer_and_all_one_mask_contract() -> None:
    assert subject._RECIPE == {
        "seed": 20260939,
        "epochs": 60,
        "batch_size": 1,
        "learning_rate": 1e-4,
        "weight_decay": 1e-4,
        "dice_epsilon": 1e-6,
        "target_definition": "inverse_db_unclip_rectangle_v1",
        "supervision_mask": "all_ones_full_valid_runtime_crop",
        "empty_target_objective": "masked_mean_probability",
        "captured_parity_maximum_absolute_error": 1e-5,
    }
    subject._validate_training_result(_training_result(), 28)


def test_source_drift_rejects_before_training_candidate_acquisition(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    acquired: list[bool] = []
    monkeypatch.setattr(subject, "_load_config", lambda *_: (
        {"expected_runner_source_bundle_sha256": "a" * 64}, "f" * 64
    ))
    monkeypatch.setattr(subject, "source_bundle_sha256", lambda *_: "b" * 64)
    dependencies = SimpleNamespace(acquire=lambda *args, **kwargs: acquired.append(True))

    with pytest.raises(subject.V40TrainingError, match="source bundle"):
        subject.train_candidate(
            tmp_path / "run", Path("config.json"), repository_root=tmp_path,
            dependencies=dependencies,
        )
    assert acquired == []
    assert not (tmp_path / "run").exists()


def test_preflight_failure_after_acquisition_records_zero_steps_and_is_voided(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    voided: list[BaseException] = []
    authorization = SimpleNamespace(snapshot_path=tmp_path / "snapshot.json", binding={"bound": True})
    monkeypatch.setattr(subject, "_load_config", lambda *_: (
        {"expected_runner_source_bundle_sha256": "a" * 64}, "f" * 64
    ))
    monkeypatch.setattr(subject, "source_bundle_sha256", lambda *_: "a" * 64)
    monkeypatch.setattr(subject, "prepare_training", lambda *args, **kwargs: (_ for _ in ()).throw(
        RuntimeError("fixture preparation failure")
    ))
    dependencies = SimpleNamespace(
        acquire=lambda *args, **kwargs: authorization,
        void=lambda auth, error: voided.append(error),
    )
    output = tmp_path / "run"

    with pytest.raises(RuntimeError, match="fixture preparation failure"):
        subject.train_candidate(
            output, Path("config.json"), repository_root=tmp_path,
            dependencies=dependencies,
        )
    failure = json.loads((output / subject.STAGE_NAME).read_text(encoding="utf-8"))
    assert failure["status"] == "failed_presealed"
    assert failure["phase"] == "preflight"
    assert failure["optimizer_steps"] == 0
    assert failure["optimizer_steps_known"] is True
    assert failure["private_data"] is False
    assert failure["sealed_data"] is False
    assert len(voided) == 1


def test_failure_stage_write_still_attempts_canonical_void(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    voided: list[BaseException] = []
    authorization = SimpleNamespace(snapshot_path=tmp_path / "snapshot.json", binding={"bound": True})
    monkeypatch.setattr(subject, "_load_config", lambda *_: (
        {"expected_runner_source_bundle_sha256": "a" * 64}, "f" * 64
    ))
    monkeypatch.setattr(subject, "source_bundle_sha256", lambda *_: "a" * 64)
    monkeypatch.setattr(subject, "prepare_training", lambda *args, **kwargs: (_ for _ in ()).throw(
        RuntimeError("original preparation failure")
    ))
    monkeypatch.setattr(subject, "_write_json", lambda *args: (_ for _ in ()).throw(
        OSError("stage write failure")
    ))
    dependencies = SimpleNamespace(
        acquire=lambda *args, **kwargs: authorization,
        void=lambda auth, error: voided.append(error),
    )

    with pytest.raises(RuntimeError, match="original preparation failure"):
        subject.train_candidate(
            tmp_path / "run", Path("config.json"), repository_root=tmp_path,
            dependencies=dependencies,
        )
    assert len(voided) == 1
    assert str(voided[0]) == "original preparation failure"


def test_success_stage_retains_base_and_inverse_target_descriptors(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    parent_sha = _write(tmp_path / "parent.onnx", b"parent")
    monkeypatch.setattr(subject, "PARENT_MODEL_SHA256", parent_sha)
    config = {
        "expected_runner_source_bundle_sha256": "a" * 64,
        "parent_model": {"path": "parent.onnx", "sha256": parent_sha},
        "base_v39_config": {"path": "v39.json", "sha256": subject.BASE_V39_CONFIG_SHA256},
        "inverse_target_request": {"path": "request.json", "sha256": subject.INVERSE_TARGET_REQUEST_SHA256},
        "inverse_target_builder": {"path": "builder.py", "sha256": subject.INVERSE_TARGET_BUILDER_SHA256},
        "inverse_target_oracle_score": {"path": "score.json", "sha256": subject.INVERSE_TARGET_ORACLE_SCORE_SHA256},
    }
    base_panels, _, _ = _target_fixture(tmp_path)
    prepared = subject.PreparedTraining(
        config, Path("config.json"), "d" * 64,
        SimpleNamespace(
            config_sha256=subject.BASE_V39_CONFIG_SHA256,
            feature_inventory_sha256="1" * 64,
            parity_maximum_absolute_error=0.0,
            train_source_count=20,
            train_truth_count=709,
            validation_source_count=3,
            validation_panel_count=9,
            validation_truth_count=183,
        ),
        base_panels, "2" * 64, 168, 1, subject.INVERSE_TARGET_REQUEST_SHA256,
    )
    authorization = SimpleNamespace(snapshot_path=tmp_path / "snapshot.json", binding={"bound": True})
    (tmp_path / "snapshot.json").write_text("{}", encoding="utf-8")
    monkeypatch.setattr(subject, "_load_config", lambda *_: (config, "d" * 64))
    monkeypatch.setattr(subject, "source_bundle_sha256", lambda *_: "a" * 64)
    monkeypatch.setattr(subject, "prepare_training", lambda *args, **kwargs: prepared)

    def patch(source, output, head, expected):
        output.write_bytes(b"patched")
        return frozen_trunk_head.PatchResult(
            parent_sha, sha256(b"patched").hexdigest(),
            tuple(frozen_trunk_head.TRAINABLE_CONSTANT_SHAPES), (),
        )

    dependencies = SimpleNamespace(
        acquire=lambda *args, **kwargs: authorization,
        void=lambda *args: None,
        verify_snapshot=lambda *args: None,
        extract_head=lambda *args: SimpleNamespace(head=torch.nn.Linear(1, 1)),
        train_head=lambda *args: _training_result(),
        patch_head=patch,
    )
    output = tmp_path / "run"

    report = subject.train_candidate(
        output, Path("config.json"), repository_root=tmp_path, dependencies=dependencies
    )

    assert report["schema"] == subject.STAGE_SCHEMA
    assert report["base_v39_config"] == config["base_v39_config"]
    assert report["inverse_target_request"] == config["inverse_target_request"]
    assert report["inverse_target_builder"] == config["inverse_target_builder"]
    assert report["inverse_target_oracle_score"] == config["inverse_target_oracle_score"]
    assert report["supervision_mask"] == {
        "definition": "all_ones_full_valid_runtime_crop",
        "rebuilt_pixel_count": 168,
        "previously_masked_pixel_count": 1,
    }
    assert report["actual_csharp_dev_evaluation"] == "pending"
    assert (output / subject.CHECKPOINT_NAME).is_file()
    assert (output / subject.ONNX_NAME).is_file()
    assert (output / subject.STAGE_NAME).read_bytes() == canonical_json_bytes(report)
