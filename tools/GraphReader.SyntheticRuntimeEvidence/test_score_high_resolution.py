# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from copy import deepcopy
from hashlib import sha256
import json
from pathlib import Path

import pytest

import score_high_resolution as scorer


def _baseline_candidate():
    return {
        "schema": "graphreader.local-synthetic-ocr-candidate.v1",
        "production_approved": False,
        "native_sha256": "1" * 64,
        "native_scope": "reviewed-source-runtime-local-diagnostic",
        "detector": {"model_id": "det", "model_version": "1", "model_sha256": "2" * 64},
        "recognizer": {"model_id": "rec", "model_version": "1", "model_sha256": "3" * 64},
        "ocr_model_input": "original",
        "model_input_protocol": {
            "path": scorer.initial_scorer.ORIGINAL_PROTOCOL_PATH.as_posix(),
            "sha256": scorer.initial_scorer.EXPECTED_ORIGINAL_PROTOCOL_SHA256,
        },
        "ocr_output_geometry": "initial_db_contour",
        "initial_contour_protocol": {
            "path": scorer.initial_scorer.PROTOCOL_PATH.as_posix(),
            "sha256": scorer.initial_scorer.EXPECTED_PROTOCOL_SHA256,
        },
        "ocr_structure_admission": "advisory",
        "advisory_structure_protocol": {
            "path": scorer.advisory_scorer.PROTOCOL_PATH.as_posix(),
            "sha256": scorer.advisory_scorer.EXPECTED_PROTOCOL_SHA256,
        },
    }


def _candidate():
    value = _baseline_candidate()
    value["ocr_detector_maximum_side_length"] = 1920
    value["detector_resolution_protocol"] = {
        "path": scorer.PROTOCOL_PATH.as_posix(),
        "sha256": scorer.EXPECTED_PROTOCOL_SHA256,
    }
    return value


def _report(candidate=None):
    adapter_id = (
        scorer._candidate_adapter_id(candidate)
        if candidate is not None else
        "graphreader-ocr:" + scorer.ADVISORY_SUFFIX_TOKEN + scorer.ADAPTER_SUFFIX
        + ":d4aa24d408cd:7839f12b644f:c96f91b3ec18")
    return {
        "model_input": "original",
        "model_input_protocol_sha256": scorer.initial_scorer.EXPECTED_ORIGINAL_PROTOCOL_SHA256,
        "geometry_protocol_sha256": scorer.initial_scorer.EXPECTED_PROTOCOL_SHA256,
        "structure_admission": "advisory",
        "admission_protocol_sha256": scorer.advisory_scorer.EXPECTED_PROTOCOL_SHA256,
        "detector_maximum_side_length": 1920,
        "detector_resolution_protocol_sha256": scorer.EXPECTED_PROTOCOL_SHA256,
        "elapsed_milliseconds": 123.5,
        "ocr_adapter_id": adapter_id,
    }


def test_protocol_binds_frozen_baseline_and_full_denominator():
    protocol, digest, identities = scorer._load_protocol(
        scorer.REPOSITORY_ROOT / scorer.PROTOCOL_PATH)
    assert digest == scorer.EXPECTED_PROTOCOL_SHA256
    assert identities["baseline_score_sha256"] == scorer.EXPECTED_BASELINE_SCORE_SHA256
    assert identities["train_truth_count"] == 146
    assert identities["dev_truth_count"] == 183
    assert protocol["budget"] == {
        "synthetic_train_dev_runs": "unlimited", "optimizer_steps": 0,
        "private_reads": 0, "sealed_runs": 0, "production_approval": False,
        "release_eligible": False,
    }


def test_candidate_is_exact_resolution_only_override():
    scorer._validate_isolated_candidate(
        _baseline_candidate(), _candidate(), scorer.EXPECTED_PROTOCOL_SHA256)
    for key, value, error in (
        ("ocr_detector_maximum_side_length", 1919, "maximum side"),
        ("ocr_detector_maximum_side_length", 1920.0, "maximum side"),
        ("ocr_detector_maximum_side_length", True, "maximum side"),
        ("production_approved", True, "changes more than"),
    ):
        changed = _candidate()
        changed[key] = value
        with pytest.raises(scorer.EvidenceError, match=error):
            scorer._validate_isolated_candidate(
                _baseline_candidate(), changed, scorer.EXPECTED_PROTOCOL_SHA256)


def test_report_identity_requires_exact_resolution_and_adapter_suffix():
    scorer._validate_report_identity(_report())
    for field, value in (
        ("detector_maximum_side_length", 960),
        ("detector_maximum_side_length", 1920.0),
        ("detector_maximum_side_length", True),
        ("detector_resolution_protocol_sha256", "0" * 64),
        ("ocr_adapter_id", "graphreader-ocr:" + scorer.ADVISORY_SUFFIX_TOKEN + ":x:y:z"),
    ):
        changed = _report()
        changed[field] = value
        with pytest.raises(scorer.EvidenceError, match="identity is invalid"):
            scorer._validate_report_identity(changed)


def test_expected_adapter_suffix_preserves_exact_baseline_identity():
    baseline = "graphreader-ocr:" + scorer.ADVISORY_SUFFIX_TOKEN + ":a:b:c"
    assert scorer._expected_adapter_id(baseline) == (
        "graphreader-ocr:" + scorer.ADVISORY_SUFFIX_TOKEN + scorer.ADAPTER_SUFFIX + ":a:b:c")
    with pytest.raises(scorer.EvidenceError, match="unexpected composition"):
        scorer._expected_adapter_id("graphreader-ocr:other:a:b:c")


def test_tensor_dimensions_bind_the_1920_db_resize_and_reject_960_shape():
    assert scorer._expected_tensor_dimensions(1000, 500) == (1920, 1024)
    assert scorer._expected_tensor_dimensions(1536, 2048) == (1536, 1920)
    assert scorer._expected_tensor_dimensions(10, 10) == (1920, 1920)
    assert scorer._expected_tensor_dimensions(1000, 500) != (960, 512)


def test_baseline_score_is_authenticated_and_complete():
    score, payload = scorer._validate_baseline_score()
    assert sha256(payload).hexdigest() == scorer.EXPECTED_BASELINE_SCORE_SHA256
    assert score["integrity"]["full_source_truth_count"] == 329
    assert score["detection_metrics"]["advisory_initial"]["combined"]["truth_region_count"] == 329


def test_output_guard_accepts_only_new_repository_artifact(monkeypatch, tmp_path):
    monkeypatch.setattr(scorer, "REPOSITORY_ROOT", tmp_path)
    output = tmp_path / "artifacts" / "high-resolution" / "score.json"
    assert scorer._validate_new_output_path(output) == output.resolve()
    output.parent.mkdir(parents=True)
    output.write_text("existing", encoding="utf-8")
    with pytest.raises(scorer.EvidenceError, match="new destination"):
        scorer._validate_new_output_path(output)
    with pytest.raises(scorer.EvidenceError, match="repository artifacts"):
        scorer._validate_new_output_path(tmp_path / "outside.json")


def test_unreviewed_evaluator_fails_before_helpers_or_truth(monkeypatch, tmp_path):
    helper_called = False
    truth_called = False

    def forbidden_helper(*_):
        nonlocal helper_called
        helper_called = True
        raise AssertionError("helper called")

    def forbidden_truth(*_):
        nonlocal truth_called
        truth_called = True
        raise AssertionError("truth called")

    monkeypatch.setattr(scorer, "_load_protocol", forbidden_helper)
    monkeypatch.setattr(scorer.family_ocr, "_regenerate", forbidden_truth)
    with pytest.raises(scorer.EvidenceError, match="current high-resolution evaluator bytes"):
        scorer.score(
            tmp_path / "protocol", tmp_path / "candidate", "1" * 64,
            tmp_path / "execution", "2" * 64,
            tmp_path / "train", "3" * 64, tmp_path / "dev", "4" * 64,
            tmp_path / "output", evaluator_sha256="0" * 64)
    assert helper_called is False
    assert truth_called is False


def _mock_images(split):
    count = 4 if split == "train" else 3
    return [
        {"image_sha256": ("a" if split == "train" else "b") + f"{index:063x}"}
        for index in range(count)
    ]


def _mock_cases(split, images):
    result = {}
    panel_ordinal = 0
    for source_index, image in enumerate(images):
        panel_count = 1 if split == "train" else 3
        panels = []
        for local in range(panel_count):
            panel_ordinal += 1
            panels.append({
                "panel_id": f"00000000-0000-4000-8000-{(100 if split == 'train' else 200) + panel_ordinal:012d}",
                "status": "seed-completed",
                "crop": {"x": local * 10, "y": source_index * 10, "width": 10, "height": 10},
            })
        result[image["image_sha256"]] = {"status": "panels-completed", "panels": panels}
    return result


@pytest.mark.parametrize("failure_mode", ["none", "zero-panels", "partial-panels"])
def test_complete_mocked_score_writes_exclusive_output_after_all_evidence(
    monkeypatch, tmp_path, failure_mode,
):
    monkeypatch.setattr(scorer, "REPOSITORY_ROOT", tmp_path)
    output = tmp_path / "artifacts" / "score.json"
    baseline = _baseline_candidate()
    candidate = _candidate()
    candidate_summary = {
        "sha256": "4" * 64, "native_sha256": "1" * 64,
        "native_scope": "reviewed-source-runtime-local-diagnostic",
        "detector": baseline["detector"], "recognizer": baseline["recognizer"],
    }
    protocol = {
        "hypothesis": "h", "isolated_change": "i", "acceptance_bar": "bar",
    }
    identities = {
        "train_manifest_path": "artifacts/train.json", "train_manifest_sha256": "5" * 64,
        "dev_manifest_path": "artifacts/dev.json", "dev_manifest_sha256": "6" * 64,
    }
    baseline_score = {
        "detection_metrics": {"advisory_initial": {"combined": {"truth_region_count": 329}}},
    }
    evidence_before_truth = False

    monkeypatch.setattr(scorer, "_validate_evaluator_sources", lambda value: value)
    monkeypatch.setattr(scorer, "_load_protocol", lambda *_: (protocol, scorer.EXPECTED_PROTOCOL_SHA256, identities))
    monkeypatch.setattr(scorer, "_validate_baseline_score", lambda: (baseline_score, b"baseline-score"))

    def candidate_loader(path, _):
        return (baseline, {**candidate_summary, "sha256": scorer.EXPECTED_BASELINE_CANDIDATE_SHA256}) \
            if "baseline" in str(path) else (candidate, candidate_summary)

    monkeypatch.setattr(scorer.original_scorer, "_validate_complete_candidate", candidate_loader)
    monkeypatch.setattr(scorer, "_validate_isolated_candidate", lambda *_: None)
    monkeypatch.setattr(scorer.db_geometry, "_validate_execution_manifest", lambda *_: (b"execution", ()))

    def validate_report(split, *_args):
        nonlocal evidence_before_truth
        images = _mock_images(split)
        cases = _mock_cases(split, images)
        if failure_mode != "none" and split == "validation":
            failed_source = cases[images[0]["image_sha256"]]
            failed_source["status"] = "failed"
            if failure_mode == "zero-panels":
                failed_source["panels"] = []
            else:
                failed_source["panels"][0]["status"] = "failed"
        report = _report(candidate_summary)
        if split == "validation":
            evidence_before_truth = True
        return images, cases, report, (split + "-report").encode(), 393, ()

    monkeypatch.setattr(scorer.original_scorer, "_validate_report", validate_report)
    monkeypatch.setattr(scorer, "_validate_completed_panel", lambda *_: ((), (), 0, {
        "masked_tensor_width": 1920, "masked_tensor_height": 1024,
        "unmasked_tensor_width": 1920, "unmasked_tensor_height": 1024,
        "panel_width": 10, "panel_height": 10, "source_crop_x": 0, "source_crop_y": 0,
    }))
    monkeypatch.setattr(scorer.initial_scorer, "_validate_truth_environment", lambda: {"verified": True})

    def regenerate(split, _seed, images):
        assert evidence_before_truth is True
        count = scorer.EXPECTED_COUNTS[split]["truths"]
        return {
            image["image_sha256"]: (None, {"truth_count": count if index == 0 else 0})
            for index, image in enumerate(images)
        }

    monkeypatch.setattr(scorer.family_ocr, "_regenerate", regenerate)
    monkeypatch.setattr(
        scorer.family_ocr, "_truth_regions",
        lambda annotation: ([object()] * annotation["truth_count"], None))
    monkeypatch.setattr(
        scorer.original_scorer, "_metrics",
        lambda images, truths, predictions: {
            "truth_region_count": sum(len(truths[item["image_sha256"]]) for item in images),
            "predicted_region_count": sum(len(predictions[item["image_sha256"]]) for item in images),
        })

    result = scorer.score(
        tmp_path / "protocol", tmp_path / "candidate.json", "4" * 64,
        tmp_path / "execution", "7" * 64,
        tmp_path / "train-report.json", "8" * 64,
        tmp_path / "dev-report.json", "9" * 64,
        output, evaluator_sha256="e" * 64)
    assert output.is_file()
    assert json.loads(output.read_text(encoding="utf-8"))["schema"] == scorer.OUTPUT_SCHEMA
    assert result["integrity"]["full_source_truth_count"] == 329
    expected_completed = {
        "none": 13, "zero-panels": 10, "partial-panels": 12,
    }[failure_mode]
    assert result["integrity"]["completed_panels"] == expected_completed
    assert result["detector_tensor_allocation"]["observation_count"] == expected_completed
    assert result["integrity"]["failed_sources"] == int(failure_mode != "none")
    assert result["integrity"]["failed_panels"] == int(failure_mode == "partial-panels")
    assert result["detection_metrics"]["advisory_initial"]["combined"]["truth_region_count"] == 329
    if failure_mode != "none":
        assert result["integrity"]["complete_identity_verification"] is False


def test_report_mutation_fails_before_truth_regeneration(monkeypatch, tmp_path):
    truth_called = False
    monkeypatch.setattr(scorer, "REPOSITORY_ROOT", tmp_path)
    monkeypatch.setattr(scorer, "_validate_evaluator_sources", lambda value: value)
    monkeypatch.setattr(scorer, "_load_protocol", lambda *_: ({}, scorer.EXPECTED_PROTOCOL_SHA256, {
        "train_manifest_path": "artifacts/train.json", "train_manifest_sha256": "5" * 64,
        "dev_manifest_path": "artifacts/dev.json", "dev_manifest_sha256": "6" * 64,
    }))
    monkeypatch.setattr(scorer, "_validate_baseline_score", lambda: ({}, b"score"))
    candidate_summary = {
        "sha256": "4" * 64, "native_sha256": "1" * 64,
        "native_scope": "reviewed-source-runtime-local-diagnostic",
        "detector": _baseline_candidate()["detector"],
        "recognizer": _baseline_candidate()["recognizer"],
    }
    monkeypatch.setattr(
        scorer.original_scorer, "_validate_complete_candidate",
        lambda path, *_: (_baseline_candidate(), candidate_summary)
        if "baseline" in str(path) else (_candidate(), candidate_summary))
    monkeypatch.setattr(scorer, "_validate_isolated_candidate", lambda *_: None)
    monkeypatch.setattr(scorer.db_geometry, "_validate_execution_manifest", lambda *_: (b"execution", ()))
    changed = _report(candidate_summary)
    changed["detector_maximum_side_length"] = 960
    monkeypatch.setattr(
        scorer.original_scorer, "_validate_report",
        lambda split, *_: (_mock_images(split), _mock_cases(split, _mock_images(split)), changed, b"report", 393, ()))

    def forbidden_truth(*_):
        nonlocal truth_called
        truth_called = True
        raise AssertionError("truth regenerated")

    monkeypatch.setattr(scorer.family_ocr, "_regenerate", forbidden_truth)
    with pytest.raises(scorer.EvidenceError, match="runtime report identity"):
        scorer.score(
            tmp_path / "protocol", tmp_path / "candidate", "4" * 64,
            tmp_path / "execution", "7" * 64,
            tmp_path / "train", "8" * 64, tmp_path / "dev", "9" * 64,
            tmp_path / "artifacts" / "score.json", evaluator_sha256="e" * 64)
    assert truth_called is False
