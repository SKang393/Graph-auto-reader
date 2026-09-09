# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from copy import deepcopy
from hashlib import sha256
from pathlib import Path
from types import SimpleNamespace

import pytest

import score_initial_contour_output as scorer


MODEL_ID = "11111111-2222-4333-8444-555555555555"
COMPONENT_ID = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"


def _polygon(left=1.25, top=2.5, right=5.0, bottom=7.75):
    return {
        "points": [
            {"x": left, "y": top, "is_finite": True},
            {"x": right, "y": top, "is_finite": True},
            {"x": right, "y": bottom, "is_finite": True},
            {"x": left, "y": bottom, "is_finite": True},
        ],
        "bounds": {
            "x": left, "y": top, "width": right - left, "height": bottom - top,
            "left": left, "top": top, "right": right, "bottom": bottom,
            "is_valid": True,
        },
    }


def _raw_region():
    return {
        "region_id": MODEL_ID,
        "polygon": _polygon(1, 1, 9, 9),
        "detection_confidence": 0.9,
        "coordinate_space": "original_pixels",
    }


def _component():
    return {
        "region_id": COMPONENT_ID,
        "polygon": _polygon(2, 2, 8, 8),
        "evidence": {"text_likelihood": 0.8, "likely_graph_structure": False},
    }


def _panel(final_region, raw=None, component=None):
    raw = raw or _raw_region()
    component = component or _component()
    return {
        "ocr_proposal_diagnostic": {
            "model_regions": [],
            "unmasked_model_regions": [deepcopy(raw)],
            "component_regions": [deepcopy(component)],
            "detector_input_sha256": "1" * 64,
            "unmasked_input_sha256": "2" * 64,
        },
        "ocr": {
            "regions": [deepcopy(final_region)],
            "region_failures": [],
            "cache": {"crop_count": 1},
        },
    }


def _sidecar(initial, raw=None):
    raw = raw or _raw_region()
    return {
        "invocations": [
            {"kind": "axis-masked"},
            {
                "kind": "unmasked",
                "observation": {
                    "accepted_contours": [{
                        "returned_region_id": raw["region_id"],
                        "initial_polygon": deepcopy(initial),
                        "expanded_polygon": deepcopy(raw["polygon"]),
                        "detection_confidence": raw["detection_confidence"],
                    }],
                },
            },
        ],
    }


def test_candidate_clone_allows_only_reviewed_geometry_fields():
    baseline = {
        "schema": "candidate", "ocr_model_input": "original",
        "model_input_protocol": {"path": "p", "sha256": "1" * 64},
        "production_approved": False,
    }
    candidate = deepcopy(baseline)
    candidate.update({
        "ocr_output_geometry": "initial_db_contour",
        "initial_contour_protocol": {
            "path": scorer.PROTOCOL_PATH.as_posix(),
            "sha256": scorer.EXPECTED_PROTOCOL_SHA256,
        },
    })
    scorer._validate_isolated_candidate(
        baseline, candidate, scorer.EXPECTED_PROTOCOL_SHA256)
    candidate["production_approved"] = True
    with pytest.raises(scorer.EvidenceError, match="changes more than"):
        scorer._validate_isolated_candidate(
            baseline, candidate, scorer.EXPECTED_PROTOCOL_SHA256)


def test_reviewed_protocol_binds_full_fixed_denominator():
    _, payload, identities = scorer._load_protocol(
        scorer.REPOSITORY_ROOT / scorer.PROTOCOL_PATH)
    assert scorer.family_ocr._sha256_bytes(payload) == scorer.EXPECTED_PROTOCOL_SHA256
    assert identities["train_source_count"] == 4
    assert identities["train_panel_count"] == 4
    assert identities["train_truth_count"] == 146
    assert identities["dev_source_count"] == 3
    assert identities["dev_panel_count"] == 9
    assert identities["dev_truth_count"] == 183


def test_initial_output_region_id_matches_binarywriter_guid_contract():
    polygon = _polygon()
    assert scorer._initial_output_region_id(MODEL_ID, COMPONENT_ID, polygon) == (
        "273b81b3-3a33-1d24-5db8-03a3468aa7d8")
    changed = deepcopy(polygon)
    changed["points"][0]["x"] += 0.25
    assert scorer._initial_output_region_id(MODEL_ID, COMPONENT_ID, changed) != (
        "273b81b3-3a33-1d24-5db8-03a3468aa7d8")


def test_completed_panel_requires_atomic_contour_polygon_and_id(monkeypatch):
    raw = _raw_region()
    component = _component()
    initial = _polygon()
    output_id = scorer._initial_output_region_id(MODEL_ID, COMPONENT_ID, initial)
    original_region = {
        **deepcopy(raw), "text": "old", "alternatives": ["old"],
        "role": "unknown", "confidence": 0.4,
    }
    final_region = {
        "region_id": output_id, "polygon": deepcopy(initial),
        "coordinate_space": "original_pixels", "text": "new",
        "alternatives": ["new"], "role": "annotation", "confidence": 0.7,
    }
    original = _panel(original_region, raw, component)
    current = _panel(final_region, raw, component)
    monkeypatch.setattr(scorer, "_sidecar_value", lambda *_: _sidecar(initial, raw))
    monkeypatch.setattr(scorer.original_scorer, "_runtime_models", lambda *_: None)
    monkeypatch.setattr(scorer.original_scorer, "_region_components", lambda *_: ("raw",))
    monkeypatch.setattr(scorer.original_scorer, "_final_components", lambda *_: ("final",))

    raw_components, final_components, stats = scorer._validate_completed_panel(
        original, current, {}, {}, Path("db-report.json"), {})
    assert raw_components == ("raw",)
    assert final_components == ("final",)
    assert stats["selected"] == 1
    assert stats["text_changes"] == 1
    assert stats["role_changes"] == 1

    current["ocr"]["regions"][0]["polygon"] = _polygon(3, 3, 6, 6)
    with pytest.raises(scorer.EvidenceError, match="does not match"):
        scorer._validate_completed_panel(
            original, current, {}, {}, Path("db-report.json"), {})


def test_completed_panel_rejects_old_contour_confidence_drift(monkeypatch):
    raw = _raw_region()
    component = _component()
    initial = _polygon()
    output_id = scorer._initial_output_region_id(MODEL_ID, COMPONENT_ID, initial)
    original = _panel(raw, raw, component)
    current = _panel({
        "region_id": output_id, "polygon": initial,
        "coordinate_space": "original_pixels",
    }, raw, component)
    sidecar = _sidecar(initial, raw)
    sidecar["invocations"][1]["observation"]["accepted_contours"][0]["detection_confidence"] = 0.8
    monkeypatch.setattr(scorer, "_sidecar_value", lambda *_: sidecar)
    with pytest.raises(scorer.EvidenceError, match="geometry or confidence"):
        scorer._validate_completed_panel(
            original, current, {}, {}, Path("db-report.json"), {})


def test_score_rejects_geometry_evidence_before_truth_regeneration(tmp_path, monkeypatch):
    monkeypatch.setattr(scorer, "REPOSITORY_ROOT", tmp_path)
    monkeypatch.setattr(scorer.family_ocr, "_require_artifact_path", lambda path, *_: path)
    identities = {
        "train_manifest_path": "manifest.json", "train_manifest_sha256": "1" * 64,
        "dev_manifest_path": "manifest.json", "dev_manifest_sha256": "1" * 64,
    }
    monkeypatch.setattr(scorer, "_load_protocol", lambda *_: (
        {"hypothesis": "h", "isolated_change": "i", "acceptance_bar": "a"},
        b"protocol", identities))
    monkeypatch.setattr(scorer, "_exact_file", lambda path, sha, label: (tmp_path / path, b"x"))
    monkeypatch.setattr(scorer, "_validate_truth_environment", lambda: {})
    monkeypatch.setattr(scorer, "_load_exact_object", lambda path, *_: (
        {"integrity": {"full_source_truth_denominator": 329, "selected_region_count": 205}}
        if path == scorer.COUNTERFACTUAL_REPORT_PATH else {}))
    monkeypatch.setattr(scorer, "_validate_reference_score", lambda *_: None)
    bound = {
        "baseline_candidate": (tmp_path / "axis.json", "1" * 64),
        "train_manifest": (tmp_path / "manifest.json", "1" * 64),
        "dev_manifest": (tmp_path / "manifest.json", "1" * 64),
        "baseline_train_report": (tmp_path / "axis-train.json", "1" * 64),
        "baseline_dev_report": (tmp_path / "axis-dev.json", "1" * 64),
    }
    monkeypatch.setattr(scorer.original_scorer, "_load_protocol", lambda *_: ({}, b"original", bound))
    monkeypatch.setattr(scorer, "EXPECTED_ORIGINAL_PROTOCOL_SHA256", sha256(b"original").hexdigest())
    raw = {"schema": "candidate", "production_approved": False}
    candidate = {
        "sha256": "2" * 64, "native_sha256": "3" * 64, "native_scope": "diagnostic",
    }
    monkeypatch.setattr(scorer.original_scorer, "_validate_complete_candidate", lambda *_: (raw, candidate))
    monkeypatch.setattr(scorer.original_scorer, "_validate_isolated_candidate", lambda *_: None)
    monkeypatch.setattr(scorer, "_validate_isolated_candidate", lambda *_: None)
    monkeypatch.setattr(scorer.db_geometry, "_validate_execution_manifest", lambda *_: (b"execution", ()))
    monkeypatch.setattr(scorer.db_geometry, "_load_protocol", lambda *_: ({}, b"db", {"runtime_candidate": (Path("c"), "4" * 64)}))
    monkeypatch.setattr(scorer.db_geometry, "_validate_candidate", lambda *_: candidate)
    monkeypatch.setattr(scorer, "_protocol_file", lambda *_: tmp_path / "manifest.json")
    images = [{"image_sha256": "5" * 64}]
    cases = {"5" * 64: {"status": "panels-completed", "panels": []}}
    calls = 0

    def validate_report(*args):
        nonlocal calls
        calls += 1
        report = {
            "ocr_adapter_id": "graphreader-ocr:graph-structure-consensus-original-model-input-v1:x",
            "geometry_protocol_sha256": None if calls == 1 else "0" * 64,
        }
        return images, cases, report, b"report", 393, ()

    monkeypatch.setattr(scorer.original_scorer, "_validate_report", validate_report)
    regenerated = False

    def forbidden_regenerate(*_):
        nonlocal regenerated
        regenerated = True
        raise AssertionError("truth regeneration preceded complete evidence validation")

    monkeypatch.setattr(scorer.family_ocr, "_regenerate", forbidden_regenerate)
    with pytest.raises(scorer.EvidenceError, match="geometry protocol"):
        scorer.score(
            tmp_path / scorer.PROTOCOL_PATH, tmp_path / "candidate.json", "6" * 64,
            tmp_path / "execution.json", "7" * 64,
            tmp_path / "train.json", "8" * 64,
            tmp_path / "dev.json", "9" * 64, tmp_path / "output.json",
            evaluator_sha256=sha256(Path(scorer.__file__).read_bytes()).hexdigest())
    assert regenerated is False


def _assert_guard_precedes_truth(tmp_path, monkeypatch, error, evaluator_sha256=None):
    monkeypatch.setattr(scorer, "REPOSITORY_ROOT", tmp_path)
    monkeypatch.setattr(scorer.family_ocr, "_require_artifact_path", lambda path, *_: path)
    monkeypatch.setattr(scorer, "_load_protocol", lambda *_: ({}, b"protocol", {}))
    regenerated = False

    def forbidden_regenerate(*_):
        nonlocal regenerated
        regenerated = True
        raise AssertionError("truth regeneration preceded dependency authentication")

    monkeypatch.setattr(scorer.family_ocr, "_regenerate", forbidden_regenerate)
    with pytest.raises(scorer.EvidenceError, match=error):
        scorer.score(
            tmp_path / scorer.PROTOCOL_PATH, tmp_path / "candidate.json", "6" * 64,
            tmp_path / "execution.json", "7" * 64,
            tmp_path / "train.json", "8" * 64,
            tmp_path / "dev.json", "9" * 64, tmp_path / "output.json",
            evaluator_sha256=evaluator_sha256 or sha256(Path(scorer.__file__).read_bytes()).hexdigest())
    assert regenerated is False
    assert not (tmp_path / "output.json").exists()


def test_unreviewed_evaluator_rejected_before_truth(tmp_path, monkeypatch):
    _assert_guard_precedes_truth(tmp_path, monkeypatch, "current evaluator bytes", "0" * 64)


@pytest.mark.parametrize("relative", sorted(scorer.SOURCE_BINDINGS))
def test_changed_transitive_source_rejected_before_truth(relative, tmp_path, monkeypatch):
    expected = scorer.SOURCE_BINDINGS[relative]
    source = tmp_path / relative
    source.parent.mkdir(parents=True)
    source.write_bytes((scorer.REPOSITORY_ROOT / relative).read_bytes() + b"\nchanged truth source\n")
    monkeypatch.setattr(scorer, "SOURCE_BINDINGS", {relative: expected})
    _assert_guard_precedes_truth(tmp_path, monkeypatch, "evaluator source .* fixed identity")


@pytest.mark.parametrize("name", sorted(scorer.PACKAGE_BINDINGS))
def test_changed_rendering_package_rejected_before_truth(name, tmp_path, monkeypatch):
    monkeypatch.setattr(scorer, "SOURCE_BINDINGS", {})
    monkeypatch.setattr(scorer, "PACKAGE_BINDINGS", {name: scorer.PACKAGE_BINDINGS[name]})
    monkeypatch.setattr(scorer, "package_version", lambda _: "0.0.0")
    _assert_guard_precedes_truth(tmp_path, monkeypatch, "truth package .* fixed version")


@pytest.mark.parametrize("alias", sorted(scorer.FONT_BINDINGS))
def test_changed_font_bytes_rejected_before_truth(alias, tmp_path, monkeypatch):
    filename, expected = scorer.FONT_BINDINGS[alias]
    path = tmp_path / filename
    path.write_bytes(b"different font bytes")
    monkeypatch.setattr(scorer, "SOURCE_BINDINGS", {})
    monkeypatch.setattr(scorer, "PACKAGE_BINDINGS", {})
    monkeypatch.setattr(scorer, "FONT_BINDINGS", {alias: (filename, expected)})
    monkeypatch.setattr(scorer, "FontResolver", lambda: SimpleNamespace(
        resolve=lambda *_args, **_kwargs: SimpleNamespace(path=path)))
    _assert_guard_precedes_truth(tmp_path, monkeypatch, "truth font .* fixed identity")
