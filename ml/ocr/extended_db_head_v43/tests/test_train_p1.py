# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path
from types import SimpleNamespace

import pytest
import torch

from ml.markers.gate_seal import canonical_json_bytes, sha256_file, source_bundle_sha256
from ml.ocr.inverse_db_target_v40 import train_p1 as v40
from ml.ocr.official_bakeoff import frozen_trunk_head, extended_head_training
from ml.ocr.extended_db_head_v43 import train_p1 as subject


def _training_result() -> extended_head_training.TrainingResult:
    epochs = tuple(
        extended_head_training.EpochLoss(
            epoch=index,
            optimizer_steps=index * 28,
            training_step_mean_loss=0.5,
            full_train_loss=0.5,
            full_positive_dice_loss=0.5,
            full_empty_negative_loss=None,
            draw_order_sha256="e" * 64,
        )
        for index in range(1, 241)
    )
    return extended_head_training.TrainingResult(
        optimizer_steps=6720,
        selected_epoch=1,
        epochs=240,
        epoch_losses=epochs,
        panel_count=28,
        positive_panel_count=28,
        empty_positive_panels=0,
        seed=20260939,
        batch_size=1,
        learning_rate=1e-4,
        weight_decay=1e-4,
        dice_epsilon=extended_head_training.DICE_EPSILON,
        torch_backend=extended_head_training.TORCH_BACKEND,
        trainable_parameter_names=extended_head_training.TRAINABLE_PARAMETER_NAMES,
        frozen_batch_norm_sha256_before="b" * 64,
        frozen_batch_norm_sha256_after="b" * 64,
        selected_trainable_state_sha256="c" * 64,
    )


def _prepared(config: dict, config_sha: str = "d" * 64) -> subject.PreparedTraining:
    panels = tuple(SimpleNamespace(panel_id=f"panel-{index}") for index in range(28))
    v39_base = SimpleNamespace(
        feature_inventory_sha256="1" * 64,
        parity_maximum_absolute_error=0.0,
        train_source_count=20,
        train_truth_count=709,
        validation_source_count=3,
        validation_panel_count=9,
        validation_truth_count=183,
    )
    base = SimpleNamespace(
        config=config["base_config"],
        config_sha256=subject.BASE_V40_CONFIG_SHA256,
        base=v39_base,
        training_panels=panels,
        target_inventory_sha256="2" * 64,
        rebuilt_mask_pixel_count=100,
        previously_masked_pixel_count=10,
        inverse_target_request_sha256=v40.INVERSE_TARGET_REQUEST_SHA256,
    )
    return subject.PreparedTraining(config, Path("config.json"), config_sha, base)


def test_actual_config_binds_bundle_and_preserves_v40_inputs() -> None:
    root = subject.REPO_ROOT
    config, config_sha = subject._load_config(root / subject.CONFIG_PATH, root)

    assert config_sha == sha256_file(root / subject.CONFIG_PATH)
    assert source_bundle_sha256(root, subject.RUNNER_SOURCE_PATHS) == config[
        "expected_runner_source_bundle_sha256"
    ]
    assert sha256_file(root / v40.CONFIG_PATH) == subject.BASE_V40_CONFIG_SHA256
    assert sha256_file(root / "ml/ocr/inverse_db_target_v40/P1_RESULT.json") == (
        subject.BASE_V40_RESULT_SHA256
    )
    assert sha256_file(root / "ml/ocr/official_bakeoff/extended_head_training.py") == (
        subject.EXTENDED_TRAINING_SOURCE_SHA256
    )
    assert config["expected_data"] == v40._EXPECTED_DATA


def test_recipe_changes_only_duration_from_v40() -> None:
    assert {**{k:v for k,v in subject._RECIPE.items() if k != "intraop_threads"}, "epochs": 60} == v40._RECIPE
    assert subject._RECIPE["intraop_threads"] == 16
    assert subject._RECIPE["epochs"] == 240
    assert subject._default_dependencies().train_head is extended_head_training.train_extended_head


def test_training_result_requires_duration_identity_and_fixed_recipe() -> None:
    result = _training_result()
    subject._validate_training_result(result, 28)

    changed = SimpleNamespace(**{**result.__dict__, "epochs": 60})
    with pytest.raises(subject.V43TrainingError, match="fixed extended Dice recipe"):
        subject._validate_training_result(changed, 28)  # type: ignore[arg-type]

    losses = list(result.epoch_losses)
    losses[1] = extended_head_training.EpochLoss(
        **{**losses[1].__dict__, "full_train_loss": 0.25}
    )
    wrong_selection = extended_head_training.TrainingResult(
        **{**result.__dict__, "epoch_losses": tuple(losses)}
    )
    with pytest.raises(subject.V43TrainingError, match="first minimum"):
        subject._validate_training_result(wrong_selection, 28)

    negative = list(result.epoch_losses)
    negative[0] = extended_head_training.EpochLoss(
        **{**negative[0].__dict__, "training_step_mean_loss": -0.1}
    )
    with pytest.raises(subject.V43TrainingError, match="epoch evidence"):
        subject._validate_training_result(
            extended_head_training.TrainingResult(
                **{**result.__dict__, "epoch_losses": tuple(negative)}
            ),
            28,
        )


def test_source_drift_rejects_before_authorization(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    acquired: list[bool] = []
    monkeypatch.setattr(subject, "_load_config", lambda *_: (
        {"expected_runner_source_bundle_sha256": "a" * 64}, "f" * 64
    ))
    monkeypatch.setattr(subject, "source_bundle_sha256", lambda *_: "b" * 64)
    dependencies = SimpleNamespace(acquire=lambda *args, **kwargs: acquired.append(True))

    with pytest.raises(subject.V43TrainingError, match="source bundle"):
        subject.train_candidate(
            tmp_path / "run", Path("config.json"), repository_root=tmp_path,
            dependencies=dependencies,
        )
    assert acquired == []
    assert not (tmp_path / "run").exists()


def test_authorization_config_mismatch_rejects_before_optimizer(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    trained: list[bool] = []
    config = {"expected_runner_source_bundle_sha256": "a" * 64}
    authorization = SimpleNamespace(
        snapshot_path=tmp_path / "snapshot.json",
        binding={
            "candidate_config_sha256": "0" * 64,
            "source_snapshot_sha256": "6" * 64,
        },
    )
    monkeypatch.setattr(subject, "_load_config", lambda *_: (config, "d" * 64))
    monkeypatch.setattr(subject, "source_bundle_sha256", lambda *_: "a" * 64)
    monkeypatch.setattr(
        subject,
        "prepare_training",
        lambda *args, **kwargs: SimpleNamespace(config_sha256="d" * 64),
    )
    deps = SimpleNamespace(
        acquire=lambda *args, **kwargs: authorization,
        train_head=lambda *args: trained.append(True),
        void=lambda *args: None,
    )

    with pytest.raises(subject.V43TrainingError, match="authorization configuration"):
        subject.train_candidate(
            tmp_path / "run", Path("config.json"), repository_root=tmp_path,
            dependencies=deps,
        )
    assert trained == []
    failure = json.loads((tmp_path / "run" / subject.STAGE_NAME).read_bytes())
    assert failure["phase"] == "preflight"
    assert failure["optimizer_steps"] == 0


def test_prepare_delegates_to_exact_v40_config_without_optimization(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    base_config = tmp_path / v40.CONFIG_PATH
    base_result = tmp_path / "ml/ocr/inverse_db_target_v40/P1_RESULT.json"
    objective = tmp_path / "ml/ocr/official_bakeoff/extended_head_training.py"
    base_config.parent.mkdir(parents=True)
    base_result.parent.mkdir(parents=True, exist_ok=True)
    objective.parent.mkdir(parents=True, exist_ok=True)
    base_config.write_bytes(b"base-config")
    base_result.write_bytes(b"base-result")
    objective.write_bytes(b"objective")
    monkeypatch.setattr(subject, "BASE_V40_CONFIG_SHA256", sha256(b"base-config").hexdigest())
    monkeypatch.setattr(subject, "BASE_V40_RESULT_SHA256", sha256(b"base-result").hexdigest())
    monkeypatch.setattr(subject, "EXTENDED_TRAINING_SOURCE_SHA256", sha256(b"objective").hexdigest())
    config = {
        "expected_runner_source_bundle_sha256": "a" * 64,
        "base_v40_config": {
            "path": v40.CONFIG_PATH.as_posix(),
            "sha256": subject.BASE_V40_CONFIG_SHA256,
        },
        "base_v40_result": {
            "path": "ml/ocr/inverse_db_target_v40/P1_RESULT.json",
            "sha256": subject.BASE_V40_RESULT_SHA256,
        },
        "extended_training_source": {
            "path": "ml/ocr/official_bakeoff/extended_head_training.py",
            "sha256": subject.EXTENDED_TRAINING_SOURCE_SHA256,
        },
    }
    observed: list[Path] = []
    base = SimpleNamespace(config_sha256=subject.BASE_V40_CONFIG_SHA256,
                           training_panels=tuple(range(28)))
    monkeypatch.setattr(subject, "_load_config", lambda *_: (config, "f" * 64))
    monkeypatch.setattr(subject, "source_bundle_sha256", lambda *_: "a" * 64)
    deps = SimpleNamespace(prepare_base=lambda path, **kwargs: (observed.append(path), base)[1])

    prepared = subject.prepare_training(
        Path("candidate.json"), repository_root=tmp_path, dependencies=deps)

    assert observed == [v40.CONFIG_PATH]
    assert prepared.base is base


def test_success_report_and_authorization_use_only_v43_identity(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    parent_path = tmp_path / "parent.onnx"
    parent_path.write_bytes(b"parent")
    parent_sha = sha256(b"parent").hexdigest()
    monkeypatch.setattr(v40, "PARENT_MODEL_SHA256", parent_sha)
    config = {
        "expected_runner_source_bundle_sha256": "a" * 64,
        "base_v40_config": {"path": v40.CONFIG_PATH.as_posix(),
                            "sha256": subject.BASE_V40_CONFIG_SHA256},
        "base_v40_result": {"path": "base-result.json",
                            "sha256": subject.BASE_V40_RESULT_SHA256},
        "extended_training_source": {"path": "extended.py",
                                 "sha256": subject.EXTENDED_TRAINING_SOURCE_SHA256},
        "base_config": {
            "parent_model": {"path": "parent.onnx", "sha256": parent_sha},
            "inverse_target_request": {"path": "request.json", "sha256": "3" * 64},
            "inverse_target_builder": {"path": "builder.py", "sha256": "4" * 64},
            "inverse_target_oracle_score": {"path": "score.json", "sha256": "5" * 64},
        },
    }
    prepared = _prepared(config)
    authorization_calls: list[dict] = []
    authorization = SimpleNamespace(
        snapshot_path=tmp_path / "snapshot.json",
        binding={
            "candidate_config_sha256": "d" * 64,
            "source_snapshot_sha256": "6" * 64,
        },
    )
    authorization.snapshot_path.write_text("{}", encoding="utf-8")
    monkeypatch.setattr(subject, "_load_config", lambda *_: (config, "d" * 64))
    monkeypatch.setattr(subject, "source_bundle_sha256", lambda *_: "a" * 64)
    monkeypatch.setattr(subject, "prepare_training", lambda *args, **kwargs: prepared)

    def patch(source, output, head, expected):
        output.write_bytes(b"patched")
        return frozen_trunk_head.PatchResult(
            parent_sha, sha256(b"patched").hexdigest(),
            tuple(frozen_trunk_head.TRAINABLE_CONSTANT_SHAPES), (),
        )

    def acquire(*args, **kwargs):
        authorization_calls.append(kwargs)
        return authorization

    deps = SimpleNamespace(
        acquire=acquire,
        void=lambda *args: None,
        verify_snapshot=lambda *args: None,
        extract_head=lambda *args: SimpleNamespace(head=torch.nn.Linear(1, 1)),
        train_head=lambda *args: _training_result(),
        patch_head=patch,
    )
    output = tmp_path / "run"

    report = subject.train_candidate(
        output, Path("config.json"), repository_root=tmp_path, dependencies=deps)

    assert authorization_calls == [{
        "task": subject.TASK,
        "revision": subject.REVISION,
        "candidate_id": subject.CANDIDATE_ID,
        "config_path": Path("config.json"),
        "runner_source_paths": subject.RUNNER_SOURCE_PATHS,
    }]
    assert report["schema"] == subject.STAGE_SCHEMA
    assert report["revision"] == subject.REVISION
    assert report["base_v40_config"] == config["base_v40_config"]
    assert report["base_v40_result"] == config["base_v40_result"]
    assert report["objective_source"] == config["extended_training_source"]
    assert report["training_result"]["epochs"] == 240
    assert report["training_result"]["dice_epsilon"] == extended_head_training.DICE_EPSILON
    assert report["actual_csharp_dev_evaluation"] == "pending"
    assert report["result_json_written"] is False
    assert report["canonical_ledger_updated"] is False
    assert report["private_data"] is False
    assert report["sealed_data"] is False
    assert report["production_approval"] is False
    assert (output / subject.STAGE_NAME).read_bytes() == canonical_json_bytes(report)


def test_protocol_keeps_current_bar_and_discloses_objective_difference() -> None:
    protocol = json.loads((subject.REPO_ROOT / subject.PROTOCOL_PATH).read_bytes())
    assert protocol["acceptance_bar"].startswith("ml/policy/acceptance-bars.json")
    assert protocol["budget"] == {
        "candidate_id": "P1",
        "experiment_budget": 1,
        "training_requires_canonical_authorization": True,
        "private_reads": 0,
        "sealed_runs_authorized": 0,
        "production_approval": False,
    }
