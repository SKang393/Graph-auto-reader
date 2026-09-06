# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fail-closed preregistration checks for dense marker contract V5."""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import pytest
import torch

import ml.markers.center.dense_contract_v5.public_gate as public_gate_module
import ml.markers.center.dense_contract_v5.train_p1 as train_p1_module
import ml.markers.center.dense_contract_v5.train_p2 as train_p2_module
import ml.markers.center.dense_contract_v5.train_p3 as train_p3_module

from ml.markers.center.dense_contract_v5.diagnose_feasibility import diagnose
from ml.markers.center.dense_contract_v5.dataset import (
    HEIGHT,
    PROHIBITED_KINDS,
    PUBLIC_SCENE_COUNT,
    TRAIN_SCENE_COUNT,
    VALIDATION_SCENE_COUNT,
    WIDTH,
    read_archive,
    render_split,
)
from ml.markers.center.dense_contract_v5.model import create_model
from ml.markers.center.dense_contract_v5.train_p1 import THRESHOLDS
from ml.markers.gate_seal import (
    GateSeal,
    canonical_json_bytes,
    sha256_file,
)
from ml.markers.training_budget import TrainingAuthorization


REPO_ROOT = Path(__file__).resolve().parents[5]
ROOT = REPO_ROOT / "ml/markers/center/dense_contract_v5"


def test_frozen_archives_and_split_metadata_are_exact() -> None:
    selection = json.loads((ROOT / "SELECTION_MANIFEST.json").read_text(encoding="utf-8"))
    seal = json.loads((ROOT / "SEALED_PUBLIC_TEST_SEAL.json").read_text(encoding="utf-8"))
    dataset = json.loads((ROOT / "PUBLIC_DATASET_MANIFEST.json").read_text(encoding="utf-8"))
    assert selection["train"]["count"] == TRAIN_SCENE_COUNT == 96
    assert selection["validation"]["count"] == VALIDATION_SCENE_COUNT == 24
    assert selection["sealed_public"]["count"] == PUBLIC_SCENE_COUNT == 32
    assert len({selection[name]["renderer_family"] for name in ("train", "validation", "sealed_public")}) == 3
    assert len({selection[name]["degradation_family"] for name in ("train", "validation", "sealed_public")}) == 3
    for name in ("train", "validation"):
        path = REPO_ROOT / selection[name]["archive_path"]
        assert sha256_file(path) == selection[name]["archive_sha256"]
    public_path = REPO_ROOT / seal["fixture_archive_path"]
    assert sha256_file(public_path) == seal["fixture_archive_sha256"]
    assert sha256_file(ROOT / "PUBLIC_DATASET_MANIFEST.json") == seal["dataset_manifest_sha256"]
    assert seal["public_gate_archive_opened"] is False
    assert seal["public_gate_evaluations"] == 0
    assert dataset["seed"] == 393
    assert len(dataset["fixtures"]) == 32
    assert len({fixture["fixture_id"] for fixture in dataset["fixtures"]}) == 32


def test_one_fresh_scene_has_dense_contract_targets_and_taxonomy() -> None:
    scene = render_split("validation")[0]
    assert scene.tensor.shape == (3, HEIGHT, WIDTH)
    assert scene.center_target.shape == (1, HEIGHT, WIDTH)
    assert scene.radius_target.shape == (1, HEIGHT, WIDTH)
    assert scene.artifact_target.shape == (1, HEIGHT, WIDTH)
    assert np.isfinite(scene.tensor).all()
    assert 8 <= len(scene.centers) <= 10
    assert set(kind for kind, _, _ in scene.hard_negatives) == set(PROHIBITED_KINDS)
    assert scene.scene_id.startswith("marker-dense-contract-v5-validation-")


def test_model_preserves_frozen_dense_three_head_contract() -> None:
    model = create_model().eval()
    value = torch.zeros((1, 3, HEIGHT, WIDTH), dtype=torch.float32)
    with torch.inference_mode():
        output = model(value)
    assert output.shape == value.shape
    assert torch.all((output[:, 0] >= 0) & (output[:, 0] <= 1))
    assert torch.all(output[:, 1] >= 1)
    assert torch.all((output[:, 2] >= 0) & (output[:, 2] <= 1))
    assert tuple(model.contract.input_channels) == (
        "ink_probability",
        "text_mask",
        "artifact_mask",
    )
    assert tuple(model.contract.output_channels) == (
        "center_probability",
        "radius_pixels",
        "artifact_probability",
    )


def test_p2_hard_negative_loss_penalizes_only_frozen_structure_points() -> None:
    archive = {
        "hard_counts": np.asarray([1], dtype=np.int32),
        "hard_points": np.asarray([[[2.0, 1.0]]], dtype=np.float32),
    }
    high = torch.full((1, 1, 4, 4), 0.01, dtype=torch.float32)
    high[0, 0, 1, 2] = 0.9
    low = high.clone()
    low[0, 0, 1, 2] = 0.1
    high_loss = train_p2_module._point_loss(high, archive, np.asarray([0]))
    low_loss = train_p2_module._point_loss(low, archive, np.asarray([0]))
    assert float(high_loss) > float(low_loss)


def test_p3_spatial_margin_covers_the_fixed_matching_and_exclusion_disks() -> None:
    archive = {
        "center_counts": np.asarray([1], dtype=np.int32),
        "centers": np.asarray([[[2.0, 2.0]]], dtype=np.float32),
        "hard_counts": np.asarray([1], dtype=np.int32),
        "hard_points": np.asarray([[[8.0, 8.0]]], dtype=np.float32),
    }
    favorable = torch.full((1, 1, 12, 12), 0.01, dtype=torch.float32)
    favorable[0, 0, 2, 5] = 0.9
    unfavorable = favorable.clone()
    unfavorable[0, 0, 2, 5] = 0.01
    unfavorable[0, 0, 8, 3] = 0.9
    favorable_positive, favorable_negative = train_p3_module._spatial_margin_losses(
        favorable,
        archive,
        np.asarray([0]),
    )
    unfavorable_positive, unfavorable_negative = train_p3_module._spatial_margin_losses(
        unfavorable,
        archive,
        np.asarray([0]),
    )
    assert float(favorable_positive) < float(unfavorable_positive)
    assert float(favorable_negative) < float(unfavorable_negative)


def test_p3_fused_inference_graph_preserves_activated_three_head_values() -> None:
    model = create_model().eval()
    fused = train_p3_module._fuse_inference_model(model)
    value = torch.linspace(0.0, 1.0, steps=3 * 32 * 32, dtype=torch.float32).reshape(1, 3, 32, 32)
    with torch.inference_mode():
        expected = model(value)
        actual = fused(value)
    assert not any(isinstance(module, torch.nn.BatchNorm2d) for module in fused.modules())
    assert float(torch.max(torch.abs(expected - actual))) <= train_p3_module.FUSION_SEMANTIC_TOLERANCE


def test_exhausted_v5_feasibility_diagnosis_is_aggregate_only_and_reproducible() -> None:
    report = json.loads((ROOT / "V5_FEASIBILITY_DIAGNOSIS.json").read_text(encoding="utf-8"))
    assert diagnose() == report
    assert report["exact_center_conflict_pair_count"] == 7
    assert report["acceptance_radius_overlap_pair_count"] == 18
    assert report["artifact_truth_cleared_hard_point_count"] == 8
    assert report["case_level_details_emitted"] is False
    assert report["fixture_pixels_emitted"] is False
    assert report["public_archive_opened"] is False
    assert report["public_gate_evaluations"] == 0
    assert "scene_ids" not in report


def test_historical_source_bindings_and_gate_configuration_are_preserved() -> None:
    protocol = json.loads((ROOT / "PROTOCOL.json").read_text(encoding="utf-8"))
    config = json.loads((ROOT / "training/p1.json").read_text(encoding="utf-8"))
    gate = json.loads((ROOT / "gates/sealed-public-v1.json").read_text(encoding="utf-8"))
    assert len(config["expected_runner_source_bundle_sha256"]) == 64
    assert len(gate["expected_evaluator_source_bundle_sha256"]) == 64
    assert len(gate["expected_gate_config_sha256"]) == 64
    assert sha256_file(ROOT / "training/p1.json") == protocol["candidate_config_sha256"]
    assert sha256_file(ROOT / "gates/sealed-public-v1.json") == protocol["public_gate_config_sha256"]
    assert config["selection_thresholds"] == list(THRESHOLDS)
    p2_protocol = json.loads((ROOT / "P2_PROTOCOL.json").read_text(encoding="utf-8"))
    p2_config = json.loads((ROOT / "training/p2.json").read_text(encoding="utf-8"))
    assert len(p2_config["expected_runner_source_bundle_sha256"]) == 64
    assert sha256_file(ROOT / "training/p2.json") == p2_protocol["candidate_config_sha256"]
    assert p2_protocol["public_gate_archive_opened"] is False
    assert p2_protocol["public_gate_evaluations"] == 0
    p3_protocol = json.loads((ROOT / "P3_PROTOCOL.json").read_text(encoding="utf-8"))
    p3_config = json.loads((ROOT / "training/p3.json").read_text(encoding="utf-8"))
    assert len(p3_config["expected_runner_source_bundle_sha256"]) == 64
    assert sha256_file(ROOT / "training/p3.json") == p3_protocol["candidate_config_sha256"]
    assert p3_protocol["p1_p2_aggregate_metrics_only_used_for_design"] is True
    assert p3_protocol["p1_p2_validation_case_detail_or_pixels_used_for_design"] is False
    assert p3_protocol["public_gate_archive_opened"] is False
    assert p3_protocol["public_gate_evaluations"] == 0


def test_canonical_budget_records_exhausted_p3_and_blocks_public_gate() -> None:
    ledger = json.loads(
        (REPO_ROOT / "ml/markers/training-budgets/production-repair-v1.json").read_text(encoding="utf-8")
    )
    entry = next(item for item in ledger["revisions"] if item["revision"] == "marker-center-dense-contract-v5")
    assert entry["status"] == "exhausted_failed_selection"
    assert entry["preregistered_candidate_ids"] == []
    assert entry["consumed_candidate_ids"] == ["P1", "P2", "P3"]
    assert entry["remaining_unregistered_candidate_ids"] == []
    assert entry["execution_authorized"] is False
    assert entry["authorized_candidate_id"] is None
    assert entry["public_gate_authorized"] is False
    assert entry["public_gate_evaluations"] == 0
    assert entry["production_approval"] is False
    assert entry["release_eligible"] is False
    result = json.loads((ROOT / "P1_RESULT.json").read_text(encoding="utf-8"))
    assert sha256_file(ROOT / "P1_RESULT.json") == entry["p1_result_sha256"]
    assert result["selection_gate_passed"] is False
    assert result["selection_false_negatives"] == 21
    assert sum(result["selection_prohibited_structure_hits"].values()) == 6
    assert result["onnx_parity_passed"] is False
    assert result["public_gate_evaluations"] == 0
    assert result["public_archive_opened_by_gate"] is False
    p2_result = json.loads((ROOT / "P2_RESULT.json").read_text(encoding="utf-8"))
    assert sha256_file(ROOT / "P2_RESULT.json") == entry["p2_result_sha256"]
    assert p2_result["selection_false_negatives"] == 16
    assert sum(p2_result["selection_prohibited_structure_hits"].values()) == 7
    assert p2_result["onnx_parity_passed"] is False
    assert p2_result["public_gate_evaluations"] == 0
    p3_result = json.loads((ROOT / "P3_RESULT.json").read_text(encoding="utf-8"))
    assert sha256_file(ROOT / "P3_RESULT.json") == entry["p3_result_sha256"]
    assert p3_result["selection_gate_passed"] is False
    assert p3_result["selection_false_negatives"] == 16
    assert sum(p3_result["selection_prohibited_structure_hits"].values()) == 7
    assert p3_result["onnx_parity_passed"] is True
    assert p3_result["checkpoint_to_inference_graph_passed"] is False
    assert p3_result["public_gate_evaluations"] == 0
    assert p3_result["public_archive_opened_by_gate"] is False


def test_training_runner_records_terminal_failure_after_authorization(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    seal_directory = tmp_path / "training-seal"
    seal_directory.mkdir()
    opened_path = seal_directory / "opened.json"
    opened_path.write_bytes(canonical_json_bytes({"status": "opened"}))
    authorization = TrainingAuthorization(
        directory=seal_directory,
        opened_path=opened_path,
        binding={"candidate_id": "P1", "committed_source_enforcement": True},
    )
    monkeypatch.setattr(
        train_p1_module,
        "acquire_training_candidate",
        lambda *args, **kwargs: authorization,
    )

    def fail_candidate(*args, **kwargs):
        raise RuntimeError("controlled training failure")

    monkeypatch.setattr(train_p1_module, "_execute_candidate", fail_candidate)
    output = tmp_path / "P1-run"
    with pytest.raises(RuntimeError, match="controlled training failure"):
        train_p1_module.run(output)
    report = json.loads((output / "candidate-report.json").read_text(encoding="utf-8"))
    result = json.loads((seal_directory / "result.json").read_text(encoding="utf-8"))
    assert report["status"] == "failed_runner"
    assert report["public_gate_archive_opened"] is False
    assert report["public_gate_evaluations"] == 0
    assert result["status"] == "failed_runner"
    assert result["report_sha256"] == sha256_file(output / "candidate-report.json")


def test_public_runner_records_terminal_failure_after_gate_open(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    seal_directory = tmp_path / "public-seal"
    seal_directory.mkdir()
    opened_path = seal_directory / "opened.json"
    opened_path.write_bytes(canonical_json_bytes({"status": "opened"}))
    seal = GateSeal(
        key="controlled-key",
        directory=seal_directory,
        opened_path=opened_path,
        binding={"evaluation_count": 1},
    )
    candidate_report_path = tmp_path / "candidate-report.json"
    onnx_path = tmp_path / "candidate.onnx"
    candidate_report_path.write_bytes(canonical_json_bytes({"selection_gate_passed": True}))
    onnx_path.write_bytes(b"controlled-onnx")

    def fail_gate(*args, **kwargs):
        raise RuntimeError("controlled public failure")

    monkeypatch.setattr(public_gate_module, "_evaluate_opened_gate", fail_gate)
    output = tmp_path / "public" / "report.json"
    with pytest.raises(RuntimeError, match="controlled public failure"):
        public_gate_module._run_opened_gate(
            candidate={"selection_gate_passed": True},
            candidate_report_path=candidate_report_path,
            onnx_path=onnx_path,
            archive_path=tmp_path / "sealed-public.npz",
            dataset_path=tmp_path / "dataset.json",
            split_seal_path=tmp_path / "split-seal.json",
            seal=seal,
            output_path=output,
        )
    report = json.loads(output.read_text(encoding="utf-8"))
    void = json.loads((seal_directory / "void.json").read_text(encoding="utf-8"))
    assert report["status"] == "failed_runner"
    assert report["evaluation_count"] == 1
    assert void["status"] == "void"
    assert void["sealed_split_read"] is False
    assert void["budget_consumed"] is False
    assert not (seal_directory / "result.json").exists()
