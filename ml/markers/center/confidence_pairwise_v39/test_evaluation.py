# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from __future__ import annotations

import copy
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from . import evaluation, runner


REPO = Path(r"E:\Graph-auto-reader")
DEV_ARTIFACTS = (
    REPO / "artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/reflected-spatial-features-20261002-v1/report.json",
    REPO / "artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/center-shape-coverage-cache-v3/dev.pt",
    REPO / "artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/center-shape-coverage-cache-v3/manifest.json",
    REPO / "artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/center-shape-coverage-cache-v3/dev-inventory.json",
    REPO / "artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/center-shape-coverage-cache-v3/historical-preparation.json",
    REPO / "artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/center-confidence-reflection-v37/P1/report.json",
)
DEV_ARTIFACTS_AVAILABLE = all(path.is_file() for path in DEV_ARTIFACTS)


def _read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def test_fixed_config_and_preregistered_evaluator_source_identity() -> None:
    config = _read_json(REPO / runner.CONFIG_PATH)
    protocol = _read_json(REPO / runner.PROTOCOL_PATH)
    runner.validate_config(REPO, config)
    assert protocol["development"]["threshold"] == 0.1
    assert protocol["development"]["matching_tolerance_px"] == 5.0
    assert protocol["eval_only"]["source_paths"] == [path.as_posix() for path in evaluation.EVALUATOR_SOURCE_PATHS]
    assert protocol["eval_only"]["source_bundle_sha256"] == runner.source_bundle_sha256(REPO, evaluation.EVALUATOR_SOURCE_PATHS)
    assert protocol["eval_only"]["source_sha256"] == {
        path.as_posix(): runner.sha256_file(REPO / path) for path in evaluation.EVALUATOR_SOURCE_PATHS
    }
    assert protocol["eval_only"]["evaluation_thresholds"] == [0.1]


def test_explicit_fixed_threshold_suppresses_shared_helper_sweep_without_changing_default() -> None:
    from ml.markers.center import shape_coverage_evaluation

    assert shape_coverage_evaluation.evaluation_threshold_set(0.1, (0.1,)) == (0.1,)
    assert shape_coverage_evaluation.evaluation_threshold_set(0.1) == (0.1, 0.25, 0.4, 0.55, 0.7)
    with pytest.raises(ValueError, match="Explicit evaluation thresholds"):
        shape_coverage_evaluation.evaluation_threshold_set(0.1, (0.25,))


def test_auxiliary_025_is_baseline_integrity_only(monkeypatch: pytest.MonkeyPatch) -> None:
    from ml.markers.center import shape_coverage_evaluation

    baseline = object()
    calls = []

    def fake_predictions(scene, coordinates, output, domain, *, balanced, threshold):
        calls.append((output, balanced, threshold))
        return (object(), object())

    monkeypatch.setattr(shape_coverage_evaluation, "predictions", fake_predictions)
    monkeypatch.setattr(shape_coverage_evaluation, "match_predictions", lambda values, centers: (object(),))
    monkeypatch.setattr(shape_coverage_evaluation.bars, "center_metrics",
                        lambda values, centers, radius: SimpleNamespace(duplicate_count=1))
    monkeypatch.setattr(shape_coverage_evaluation.legacy, "prohibited_hits", lambda values, scene: {"x": 2})
    scene = SimpleNamespace(centers=((1.0, 2.0),))
    counts = shape_coverage_evaluation.historical_v27_integrity_counts(scene, (), baseline, None)

    assert calls == [(baseline, False, 0.25)]
    assert counts["true_positives"] == 1
    assert counts["false_positives"] == 1
    assert counts["false_negatives"] == 0
    assert counts["prohibited_structure_hits"] == 2


def test_missing_development_or_output_binding_is_rejected() -> None:
    config = _read_json(REPO / runner.CONFIG_PATH)
    protocol = _read_json(REPO / runner.PROTOCOL_PATH)
    malformed = copy.deepcopy(config)
    malformed.pop("development_file")
    with pytest.raises(ValueError, match="development evaluator input"):
        evaluation.validate_protocol(REPO, malformed, protocol)
    malformed = copy.deepcopy(config)
    malformed["training_output_path"] = "artifacts/other"
    with pytest.raises(ValueError, match="checkpoint output path"):
        evaluation.validate_protocol(REPO, malformed, protocol)


def test_actual_checkpoint_and_training_report_hashes_are_mandatory() -> None:
    with pytest.raises(ValueError, match="externally pinned"):
        evaluation._require_sha256(None, "V39 checkpoint")
    with pytest.raises(ValueError, match="lowercase SHA-256"):
        evaluation._require_sha256("A" * 64, "V39 checkpoint")


@pytest.mark.skipif(not DEV_ARTIFACTS_AVAILABLE, reason="fixed local synthetic train/dev artifacts are absent")
def test_actual_fixed_preflight_proves_manifest_split_labels_and_counts() -> None:
    result = evaluation.preflight(REPO)
    assert result["status"] == "eval_preflight_pass_no_candidate_scored"
    assert result["scope_counts"] == {"component": (167, 2004), "family": (9, 206)}
    assert (result["scenes"], result["truths"]) == (176, 2210)
    assert result["fixed_dev_sha256"] == "98662114b6e0b5e581b9ded142a51d304a65914881aeda0d75c74b84cc857e41"
    assert result["per_scene_overlap_verified"] is False
    assert result["private_reads"] == result["sealed_reads"] == result["optimizer_steps"] == 0


def test_summary_keeps_component_and_family_baseline_candidate_metrics_separate() -> None:
    metrics = {
        scope: {
            "v27_balanced": {"0.1": {
                "scene_count": scenes, "truth_count": truths, "true_positives": 10,
                "false_positives": 2, "false_negatives": truths - 10,
                "precision": 10 / 12, "recall": 10 / truths, "proposal_recall": 1.0,
                "prohibited_structure_hits": 1, "prohibited_structure_hit_rate": 1 / 12,
            }},
            "candidate_balanced": {"0.1": {
                "scene_count": scenes, "truth_count": truths, "true_positives": 11,
                "false_positives": 3, "false_negatives": truths - 11,
                "precision": 11 / 14, "recall": 11 / truths, "proposal_recall": 1.0,
                "prohibited_structure_hits": 2, "prohibited_structure_hit_rate": 2 / 14,
            }},
        }
        for scope, (scenes, truths) in evaluation.DEV_COUNTS.items()
    }
    result = evaluation.summarize_metrics({"results": metrics})
    assert result["component"]["candidate_balanced"]["precision"] == 11 / 14
    assert result["family"]["v27_balanced"]["prohibited_structure_hits"] == 1


def test_no_candidate_checkpoint_is_never_substituted_with_v37_baseline() -> None:
    result = evaluation.preflight(REPO)
    if not result["completed_checkpoint_present"]:
        assert not result["completed_training_report_present"]
        assert not (REPO / evaluation.OUTPUT_PATH).exists()
