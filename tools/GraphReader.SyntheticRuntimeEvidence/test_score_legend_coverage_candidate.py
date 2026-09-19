# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from copy import deepcopy
from hashlib import sha256
from pathlib import Path
from types import SimpleNamespace

import pytest

import score_legend_coverage_candidate as scorer


def test_scoring_source_substitution_is_limited_to_current_evaluator(monkeypatch):
    observed = {}
    reviewed_evaluator_sha256 = scorer.CURRENT_EVALUATOR_SOURCE_SHA256

    def read_exact(_root, relative, digest, _label, *_args):
        observed[relative] = digest
        if (relative == scorer.CURRENT_EVALUATOR_SOURCE
                and digest != reviewed_evaluator_sha256):
            raise scorer.metric.EvidenceError("wrong evaluator")
        return Path(relative), b""

    monkeypatch.setattr(scorer.metric.geometry, "_read_exact", read_exact)
    monkeypatch.setattr(
        scorer.metric.geometry, "_validate_source_bindings",
        lambda _root: (_ for _ in ()).throw(AssertionError("old validator called")),
    )
    result = scorer._validate_scoring_sources(Path("root"))
    assert result[scorer.CURRENT_EVALUATOR_SOURCE] == (
        scorer.CURRENT_EVALUATOR_SOURCE_SHA256
    )
    expected = dict(scorer.metric.geometry.SOURCE_BINDINGS)
    expected.update(scorer.metric.SOURCE_BINDINGS)
    assert {
        key: value for key, value in result.items()
        if key != scorer.CURRENT_EVALUATOR_SOURCE
    } == {
        key: value for key, value in expected.items()
        if key != scorer.CURRENT_EVALUATOR_SOURCE
    }

    monkeypatch.setattr(scorer, "CURRENT_EVALUATOR_SOURCE_SHA256", "0" * 64)
    with pytest.raises(scorer.metric.EvidenceError, match="wrong evaluator"):
        scorer._validate_scoring_sources(Path("root"))


def test_candidate_template_binding_is_exact():
    root = Path("repository").resolve()
    expected = root / scorer.CANDIDATE_TEMPLATE_PATH
    scorer._require_candidate_template_binding(
        root, expected, scorer.CANDIDATE_TEMPLATE_SHA256
    )
    with pytest.raises(scorer.metric.EvidenceError):
        scorer._require_candidate_template_binding(
            root, expected, "0" * 64
        )
    with pytest.raises(scorer.metric.EvidenceError):
        scorer._require_candidate_template_binding(
            root, root / "other-template.json", scorer.CANDIDATE_TEMPLATE_SHA256
        )


def _inventory_fixture():
    historical = {}
    for index in range(28):
        historical[f"historical-train-{index}"] = SimpleNamespace(
            split="train", source_sha256=f"historical-train-source-{index % 20}"
        )
    for index in range(9):
        historical[f"historical-validation-{index}"] = SimpleNamespace(
            split="validation",
            source_sha256=f"historical-validation-source-{index % 3}",
        )
    supplemental = {
        f"supplemental-{index}": SimpleNamespace(
            split="train", source_sha256=f"supplemental-source-{index}"
        )
        for index in range(6)
    }
    return historical, supplemental


def test_combined_inventory_rejects_panel_and_source_collisions_before_truth():
    historical, supplemental = _inventory_fixture()
    scorer._validate_combined_inventory(historical, supplemental)

    panel_collision = dict(supplemental)
    panel_collision[next(iter(historical))] = panel_collision.pop(next(iter(panel_collision)))
    with pytest.raises(scorer.metric.EvidenceError):
        scorer._validate_combined_inventory(historical, panel_collision)

    source_collision = dict(supplemental)
    first = next(iter(source_collision))
    source_collision[first] = SimpleNamespace(
        split="train", source_sha256=next(iter(historical.values())).source_sha256
    )
    with pytest.raises(scorer.metric.EvidenceError):
        scorer._validate_combined_inventory(historical, source_collision)


def test_historical_evaluation_request_allows_only_the_source_path_rebinding():
    original = {
        "capture_source": {
            "path": "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadTensorCapture.cs",
            "sha256": scorer.HISTORICAL_SOURCE_SHA256,
        },
        "assemblies": [{"name": "GraphReader.Ocr", "sha256": "a" * 64}],
        "panels": [{"panel_id": "panel"}],
    }
    derived = deepcopy(original)
    derived["capture_source"]["path"] = scorer.HISTORICAL_SOURCE.as_posix()
    scorer._validate_historical_request_delta(original, derived)

    for field, value in (("sha256", "b" * 64), ("path", "unbound.cs")):
        changed = deepcopy(derived)
        changed["capture_source"][field] = value
        with pytest.raises(scorer.metric.EvidenceError):
            scorer._validate_historical_request_delta(original, changed)
    changed = deepcopy(derived)
    changed["assemblies"][0]["sha256"] = "c" * 64
    with pytest.raises(scorer.metric.EvidenceError):
        scorer._validate_historical_request_delta(original, changed)


def _candidate_fixture():
    template = {
        "schema": "candidate",
        "composition_version": "original-db-head-candidate-v1",
        "recognizer": {"sha256": "r"},
        "native_sha256": "n" * 64,
        "license_inputs": [
            {"path": "LICENSES/original.txt", "sha256": "1" * 64}
        ],
        "execution_assemblies": [{"sha256": "e"}],
        "detector": {
            "model_path": "control.onnx",
            "model_id": "control",
            "model_version": scorer.PARENT_MODEL_VERSION,
            "model_sha256": "a" * 64,
            "manifest_path": "control.json",
            "manifest_sha256": "b" * 64,
        },
    }
    candidate = deepcopy(template)
    candidate["detector"].update({
        "model_path": "candidate.onnx",
        "model_id": "candidate",
        "model_version": scorer.DERIVED_MODEL_VERSION,
        "model_sha256": "c" * 64,
        "manifest_path": "candidate.json",
        "manifest_sha256": "d" * 64,
    })
    candidate["license_inputs"].append({
        "path": "candidate/MODIFICATION-NOTICE.txt", "sha256": "2" * 64,
    })
    manifest = {
        "model_id": "control",
        "sha256": "a" * 64,
        "files": ["control.onnx"],
        "benchmarks": [{"identity": "control"}],
        "model_version": scorer.PARENT_MODEL_VERSION,
        "preprocessing": {"maximum_side_length": 960},
        "postprocessing": {"probability_threshold": 0.3},
        "providers": ["cpu"],
        "task": "ocr_detection",
    }
    candidate_manifest = deepcopy(manifest)
    candidate_manifest.update({
        "model_id": "candidate",
        "model_version": scorer.DERIVED_MODEL_VERSION,
        "sha256": "c" * 64,
        "files": ["candidate.onnx"],
        "benchmarks": [{"identity": "candidate"}],
    })
    return candidate, template, candidate_manifest, manifest


def test_candidate_delta_allows_packager_version_and_appended_notice_only():
    fixture = _candidate_fixture()
    scorer._validate_candidate_recipe_delta(*fixture)

    for defect in (
        "recognizer", "version", "preprocessing", "same_model", "original_notice",
    ):
        candidate, template, candidate_manifest, control_manifest = _candidate_fixture()
        if defect == "recognizer":
            candidate["recognizer"] = {"sha256": "changed"}
        elif defect == "version":
            candidate["detector"]["model_version"] = "0.0.2"
            candidate_manifest["model_version"] = "0.0.2"
        elif defect == "preprocessing":
            candidate_manifest["preprocessing"]["maximum_side_length"] = 961
        elif defect == "original_notice":
            candidate["license_inputs"][0]["sha256"] = "3" * 64
        else:
            candidate["detector"]["model_sha256"] = template["detector"]["model_sha256"]
        with pytest.raises(scorer.metric.EvidenceError):
            scorer._validate_candidate_recipe_delta(
                candidate, template, candidate_manifest, control_manifest
            )


def test_modification_notice_requires_packager_content_and_candidate_directory(tmp_path):
    candidate_path = tmp_path / "candidate" / "candidate.json"
    candidate_path.parent.mkdir()
    notice_path = candidate_path.parent / scorer.MODIFICATION_NOTICE_NAME
    payload = scorer.MODIFICATION_NOTICE_TEXT.encode("utf-8")
    notice_path.write_bytes(payload)
    descriptor = {
        "path": notice_path.relative_to(tmp_path).as_posix(),
        "sha256": sha256(payload).hexdigest(),
    }
    scorer._validate_modification_notice(tmp_path, candidate_path, descriptor)

    changed = b"Unreviewed notice\n"
    notice_path.write_bytes(changed)
    descriptor["sha256"] = sha256(changed).hexdigest()
    with pytest.raises(scorer.metric.EvidenceError, match="content changed"):
        scorer._validate_modification_notice(tmp_path, candidate_path, descriptor)


def test_failed_panel_truth_remains_in_full_source_denominator():
    truths = (
        scorer.metric.FullTextTruth(
            "truth-completed", "source-completed", scorer.metric.Box(0, 0, 10, 10),
            "A", "legend_text", "legendtext",
        ),
        scorer.metric.FullTextTruth(
            "truth-failed", "source-failed", scorer.metric.Box(0, 0, 10, 10),
            "B", "legend_text", "legendtext",
        ),
    )
    recognized = scorer.metric.FullTextPrediction(
        "prediction", "source-completed", scorer.metric.Box(0, 0, 10, 10),
        "A", "legendtext",
    )
    score = scorer.metric._score_split(
        truths, {"source-completed": (recognized,)}
    )
    assert score["truth_region_count"] == 2
    assert score["geometry_false_negative_count"] == 1
    assert score["recognition_exact_accuracy"] == 0.5

    polygon = ((0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0))
    geometry_prediction = scorer.metric.geometry._prediction(polygon)
    evidence = SimpleNamespace(
        panels={
            "completed": SimpleNamespace(split="train"),
            "failed": SimpleNamespace(split="train"),
        },
        report={"panels": [
            {"panel_id": "completed", "status": "completed"},
            {"panel_id": "failed", "status": "failed"},
        ]},
        raw_by_source={"source-completed": (geometry_prediction,)},
        recognized_by_source={"source-completed": (geometry_prediction,)},
        explicit_region_failures={"train": 0},
        failed_panel_raw_regions={"train": 0},
    )
    raw, recognized_geometry, failures = scorer.metric._geometry_and_failure_metrics(
        evidence, {"train": truths}
    )
    assert raw["train"]["truth_region_count"] == 2
    assert recognized_geometry["train"]["false_negatives"] == 1
    assert failures["train"]["failed_panel_count"] == 1
