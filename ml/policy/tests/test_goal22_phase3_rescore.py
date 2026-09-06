# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import json

import pytest
import ml.policy.rescore_goal22_phase3 as rescore_module

from ml.policy.rescore_goal22_phase3 import (
    APPROVED_MARKER_CLASSIFIER,
    MARKER_CANDIDATES,
    OCR_CANDIDATES,
    POLICY_PATH,
    RESULT_PATH,
    _payloads,
    _score,
    _validate_optional_approved_classifier_evidence,
    rescore,
)


def test_recorded_rescore_matches_tracked_result() -> None:
    assert rescore() == json.loads(RESULT_PATH.read_text(encoding="utf-8"))


def test_policy_has_exact_tier1_and_tier2_contract() -> None:
    policy = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
    assert policy["tier1_reviewable_error"] == {
        "text_region_detection_recall_minimum": 0.95,
        "text_region_detection_precision_minimum": 0.95,
        "recognition_exact_match_minimum": 0.95,
        "character_error_rate_maximum": 0.05,
        "role_accuracy_minimum": 0.95,
        "prohibited_structure_hit_rate_maximum": 0.02,
        "marker_center_recall_minimum": 0.95,
        "marker_center_precision_minimum": 0.95,
        "marker_shape_accuracy_minimum": 0.95,
        "marker_fill_accuracy_minimum": 0.95,
    }
    assert policy["tier2_silent_corruption"]["zero_silently_exported_unvalidated_calibrations"] is True
    assert policy["compatibility_findings"]["marker_fill_accuracy_enforcement"] == (
        "resolved_approved_payload_compatibility"
    )
    assert policy["compatibility_findings"]["approved_payload_compatibility_proposal"] == {
        "existing_approved_fill_gate": 0.9,
        "future_candidate_fill_target": 0.95,
        "resolution_required_before_tier1_complete": False,
    }


def _ocr_candidate(revision: str) -> dict:
    return next(item for item in OCR_CANDIDATES if item["revision"] == revision)


def test_selection_prefers_v8_and_payload_available_marker_p2() -> None:
    result = rescore()
    assert result["selected_ocr"] == {
        "revision": "graphreader-v10-bounded-zero-consensus-ambiguity-alias-composition-v8",
        "candidate_id": "P1",
        "recorded_public_tier1_passed": True,
        "corrected_synthetic_tier1_passed": False,
    }
    assert result["selected_marker"] == {
        "revision": "marker-center-runtime-consistency-v2",
        "candidate_id": "P2",
        "recorded_public_tier1_passed": True,
        "real_dev_tier1_passed": False,
        "adapter_path": "src/GraphReader.App/Integration/Workflow/ProductionProposalMarkerCenterAdapter.cs",
        "adapter_sha256": rescore_module._sha256(rescore_module.MARKER_ADAPTER_PATH),
        "adapter_test_path": "tests/GraphReader.App.Tests/ProductionProposalMarkerCenterAdapterTests.cs",
        "adapter_test_sha256": rescore_module._sha256(rescore_module.MARKER_ADAPTER_TEST_PATH),
    }
    assert result["selected_adapter_compatibility"] == {"ocr": True, "marker": True}
    assert result["recorded_public_detection_candidates_clear_tier1"] is True
    assert result["selected_detection_candidates_clear_current_tier1"] is False
    assert result["tier1_automatic_pipeline_complete"] is False
    assert result["synthetic_candidate_approval"] is False
    assert "marker_fill_bar_compatibility_unresolved" not in result["promotion_blockers"]
    assert result["production_approval"] is False
    assert result["real_acceptance_corpus"] == {
        "study_count": 40,
        "dig_project_count": 171,
        "digitized_point_count": 3055,
        "split_status": "frozen_study_level_assignment",
        "assignment_sha256": "decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a",
        "real_dev_project_count": 120,
        "real_sealed_project_count": 51,
        "real_dev_ocr_axis_scored": True,
        "real_dev_ocr_axis_passed": False,
        "real_dev_marker_center_scored": True,
        "real_dev_marker_center_passed": False,
        "real_sealed_scored": False,
    }
    assert "selected_ocr_fails_corrected_synthetic_tier1" in result["promotion_blockers"]
    assert "selected_marker_fails_real_dev_tier1" in result["promotion_blockers"]
    assert "real_sealed_acceptance_not_scored" in result["promotion_blockers"]
    assert "selected_marker_adapter_not_implemented" not in result["promotion_blockers"]
    assert "private_acceptance_set_has_fewer_than_five_images" not in result["promotion_blockers"]
    assert result["model_inference_runs"] == 0
    assert result["sealed_split_reads"] == 0
    marker = result["marker_candidates"][0]
    assert set(marker["gates"]) == {
        "marker_center_precision",
        "marker_center_recall",
        "prohibited_structure_hit_rate",
    }
    assert "recognition_exact_match" not in marker["metrics"]
    assert "character_error_rate" not in marker["metrics"]
    assert "role_accuracy" not in marker["metrics"]


def test_selected_v8_has_all_composition_payload_identities() -> None:
    result = rescore()
    v8 = next(item for item in result["ocr_candidates"] if item["revision"].endswith("composition-v8"))
    assert v8["protocol_sha256"] == "32b4a7f74bfe93eba01ae59f0a6eb7cd575e019c605bb182119b67da2b7b25d0"
    assert v8["adapter_factory_path"] == "src/GraphReader.Ocr/OcrV8ProductionCompositionFactory.cs"
    assert v8["adapter_factory_sha256"] == "883db3c175362c265c92775475286a6263907f99e37ad11e62a4abd8f9230399"
    assert v8["operating_configuration"] == {
        "detector_threshold": 0.95,
        "official_rescue_threshold": 0.90,
        "consensus_rescue_threshold": 0.85,
        "zero_consensus_rescue_threshold": 0.82,
        "numeric_minimum_confidence": 0.65,
    }
    assert {item["kind"] for item in v8["payloads"]} == {
        "detector",
        "official_recognizer",
        "official_recognizer_inference_yaml",
        "numeric_recognizer",
        "ambiguity_recognizer",
    }
    assert v8["metrics"] == {
        "scene_count": 160,
        "exact_scene_count": 160,
        "true_positives": 800,
        "false_positives": 0,
        "false_negatives": 0,
        "prohibited_structure_hits": 0,
        "detected_region_count": 800,
        "detection_precision": 1.0,
        "detection_recall": 1.0,
        "prohibited_structure_hit_rate": 0.0,
        "recognition_exact_match": 0.99375,
        "character_error_rate": 0.0013979496738117428,
        "role_accuracy": 1.0,
    }


def test_approved_marker_classifier_compatibility_finding_is_preserved() -> None:
    result = rescore()["approved_marker_classifier"]
    assert result["shape_accuracy"] == 0.9907407407407407
    assert result["fill_accuracy"] == 0.9444444444444444
    assert result["gates"] == {
        "marker_shape_accuracy": True,
        "marker_fill_accuracy": False,
    }
    assert result["prior_production_approval"] is True
    assert result["approved_payload_compatible"] is True
    assert result["approved_payload_compatibility_gates"] == {
        "marker_shape_accuracy": True,
        "marker_fill_accuracy": True,
    }
    assert result["tier1_compatible"] is False


def test_detection_rates_are_derived_from_aggregate_counts() -> None:
    result = rescore()
    by_revision = {item["revision"]: item for item in result["ocr_candidates"]}
    v30 = by_revision["graph-text-unanimous-structure-veto-v30"]["metrics"]
    assert v30["detected_region_count"] == 2047
    assert v30["detection_precision"] == 1.0
    assert v30["detection_recall"] == 2047 / 2048
    assert v30["prohibited_structure_hit_rate"] == 0.0
    marker = next(item for item in rescore()["marker_candidates"] if item["revision"] == "marker-center-production-repair-v2")
    assert marker["metrics"]["true_positives"] == 18
    assert marker["metrics"]["marker_center_precision"] == 18 / 19
    payload_kinds = {
        item["kind"] for item in next(
            candidate for candidate in rescore()["ocr_candidates"]
            if candidate["revision"] == "graph-text-unanimous-structure-veto-v30"
        )["payloads"]
    }
    assert payload_kinds == {
        "v17_detector",
        "official_recognizer",
        "official_recognizer_inference_yaml",
        "onnx",
    }


def test_rescore_does_not_reference_fixture_archives() -> None:
    serialized = POLICY_PATH.read_text(encoding="utf-8").lower()
    assert ".zip" not in serialized
    assert "fixture_archive_path" not in serialized


def test_optional_payload_declarations_are_stable_when_bytes_are_absent() -> None:
    declaration = {
        "payloads": (("onnx", "artifacts/not-present.onnx", "a" * 64),),
    }
    assert _payloads(declaration) == [{
        "kind": "onnx",
        "path": "artifacts/not-present.onnx",
        "sha256": "a" * 64,
    }]


def test_classifier_identity_is_recorded_when_optional_artifacts_are_absent() -> None:
    original_evidence = APPROVED_MARKER_CLASSIFIER["evidence_path"]
    original_payload = APPROVED_MARKER_CLASSIFIER["optional_payload_path"]
    APPROVED_MARKER_CLASSIFIER["evidence_path"] = "artifacts/not-present-approval.json"
    APPROVED_MARKER_CLASSIFIER["optional_payload_path"] = "artifacts/not-present-classifier.onnx"
    original_report = APPROVED_MARKER_CLASSIFIER["public_v3_report_path"]
    APPROVED_MARKER_CLASSIFIER["public_v3_report_path"] = "artifacts/not-present-public-v3.json"
    try:
        result = rescore()["approved_marker_classifier"]
        assert result["evidence_file_sha256"] == "c4fb25e45e9c6d77100de8230a30443231445fa71751d685ba66c65da370e7a3"
        assert result["payload_sha256"] == "26f9304f1689053a0b94aa896a1e239f6ade1e5c1920736a3535c1b32f803b8a"
    finally:
        APPROVED_MARKER_CLASSIFIER["evidence_path"] = original_evidence
        APPROVED_MARKER_CLASSIFIER["optional_payload_path"] = original_payload
        APPROVED_MARKER_CLASSIFIER["public_v3_report_path"] = original_report


def test_missing_optional_report_does_not_block_aggregate_rescore() -> None:
    policy = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
    candidate = dict(_ocr_candidate("graph-text-unanimous-structure-veto-v30"))
    candidate["optional_evidence_path"] = "artifacts/not-present-report.json"
    scored = _score(candidate, policy["tier1_reviewable_error"])
    assert scored["tier1_passed"] is True


def test_selected_threshold_mismatch_fails_closed() -> None:
    policy = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
    candidate = dict(_ocr_candidate("graph-text-unanimous-structure-veto-v30"))
    candidate["selected_threshold"] = 0.54
    with pytest.raises(RuntimeError, match="Selected threshold mismatch"):
        _score(candidate, policy["tier1_reviewable_error"])


def test_marker_selected_threshold_source_mismatch_fails_closed() -> None:
    policy = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
    candidate = dict(MARKER_CANDIDATES[0])
    candidate["selected_threshold"] = 0.3
    with pytest.raises(RuntimeError, match="Selected threshold mismatch"):
        _score(candidate, policy["tier1_reviewable_error"])


def test_marker_aggregate_source_checksum_mismatch_fails_closed() -> None:
    policy = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
    candidate = dict(MARKER_CANDIDATES[0])
    candidate["ledger_entry_sha256"] = "0" * 64
    with pytest.raises(RuntimeError, match="Aggregate evidence checksum mismatch"):
        _score(candidate, policy["tier1_reviewable_error"])


def test_marker_true_positive_snapshot_is_ledger_bound_without_optional_report() -> None:
    policy = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
    candidate = dict(MARKER_CANDIDATES[0])
    candidate["optional_evidence_path"] = "artifacts/not-present-marker-report.json"
    candidate["aggregate"] = {**candidate["aggregate"], "true_positives": 99}
    with pytest.raises(RuntimeError, match="Aggregate snapshot value mismatch"):
        _score(candidate, policy["tier1_reviewable_error"])


def test_present_classifier_evidence_checksum_mismatch_fails_closed() -> None:
    original_path = APPROVED_MARKER_CLASSIFIER["evidence_path"]
    original = APPROVED_MARKER_CLASSIFIER["evidence_file_sha256"]
    APPROVED_MARKER_CLASSIFIER["evidence_path"] = "ml/policy/acceptance-bars.json"
    APPROVED_MARKER_CLASSIFIER["evidence_file_sha256"] = "0" * 64
    try:
        with pytest.raises(RuntimeError, match="Approved marker classifier evidence checksum mismatch"):
            _validate_optional_approved_classifier_evidence()
    finally:
        APPROVED_MARKER_CLASSIFIER["evidence_path"] = original_path
        APPROVED_MARKER_CLASSIFIER["evidence_file_sha256"] = original


def test_present_marker_report_metric_mismatch_fails_closed(monkeypatch) -> None:
    policy = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
    candidate = dict(MARKER_CANDIDATES[0])
    report_path = rescore_module.REPO_ROOT / candidate["optional_evidence_path"]
    if not report_path.is_file():
        pytest.skip("optional marker report is absent")
    original_read = rescore_module._read

    def tampered_read(path):
        value = original_read(path)
        if path == report_path:
            value["per_scene"][0]["metrics_5px"]["true_positives"] += 1
        return value

    monkeypatch.setattr(rescore_module, "_read", tampered_read)
    with pytest.raises(RuntimeError, match="Optional marker report aggregate mismatch"):
        _score(candidate, policy["tier1_reviewable_error"])


def test_present_classifier_report_accuracy_mismatch_fails_closed(monkeypatch) -> None:
    report_path = rescore_module.REPO_ROOT / APPROVED_MARKER_CLASSIFIER["public_v3_report_path"]
    if not report_path.is_file():
        pytest.skip("optional classifier report is absent")
    original_read = rescore_module._read

    def tampered_read(path):
        value = original_read(path)
        if path == report_path:
            value["metrics"]["shape"]["accuracy"] += 0.001
        return value

    monkeypatch.setattr(rescore_module, "_read", tampered_read)
    with pytest.raises(RuntimeError, match="shape accuracy mismatch"):
        _validate_optional_approved_classifier_evidence()


def test_present_classifier_payload_checksum_mismatch_fails_closed() -> None:
    original_evidence = APPROVED_MARKER_CLASSIFIER["evidence_path"]
    original_payload = APPROVED_MARKER_CLASSIFIER["optional_payload_path"]
    original_report = APPROVED_MARKER_CLASSIFIER["public_v3_report_path"]
    original_hash = APPROVED_MARKER_CLASSIFIER["payload_sha256"]
    APPROVED_MARKER_CLASSIFIER["evidence_path"] = "artifacts/not-present-approval.json"
    APPROVED_MARKER_CLASSIFIER["optional_payload_path"] = "ml/policy/acceptance-bars.json"
    APPROVED_MARKER_CLASSIFIER["public_v3_report_path"] = "artifacts/not-present-public-v3.json"
    APPROVED_MARKER_CLASSIFIER["payload_sha256"] = "0" * 64
    try:
        with pytest.raises(RuntimeError, match="Approved marker classifier payload checksum mismatch"):
            _validate_optional_approved_classifier_evidence()
    finally:
        APPROVED_MARKER_CLASSIFIER["evidence_path"] = original_evidence
        APPROVED_MARKER_CLASSIFIER["optional_payload_path"] = original_payload
        APPROVED_MARKER_CLASSIFIER["public_v3_report_path"] = original_report
        APPROVED_MARKER_CLASSIFIER["payload_sha256"] = original_hash
