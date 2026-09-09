# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from copy import deepcopy
from pathlib import Path

import pytest

import build_frozen_workflow_truth as builder


SERIES_ID = "10000000-0000-4000-8000-000000000001"
POINT_ID = "20000000-0000-4000-8000-000000000001"
PHASE_ID = "30000000-0000-4000-8000-000000000001"
PANEL_ID = "40000000-0000-4000-8000-000000000001"
SOURCE_ID = "50000000-0000-4000-8000-000000000001"


def _scene():
    point = {
        "point_id": POINT_ID, "series_id": SERIES_ID, "phase_id": PHASE_ID,
        "observation_index": 1, "printed_x_value": 1, "estimated_x_value": None,
        "x_confidence": 1.0, "graph": [1.0, 12.5], "shape": "circle", "fill": "filled",
    }
    return {
        "scene_id": "60000000-0000-4000-8000-000000000001",
        "canvas": {"width": 100, "height": 80},
        "panels": [{
            "panel_id": PANEL_ID,
            "axes": {"x": {"session_count": 10}, "y": {}},
            "ticks": [
                {"axis": "x", "value": 1.0, "label": "1"},
                {"axis": "x", "value": 10.0, "label": "10"},
            ],
            "calibration_anchors": [
                {"kind": "session1_y0", "graph": [1.0, 0.0]},
                {"kind": "session1_ymax", "graph": [1.0, 100.0]},
                {"kind": "sessionmax_y0", "graph": [10.0, 0.0]},
            ],
            "phases": [{"phase_id": PHASE_ID, "code": "a"}],
            "series": [{
                "series_id": SERIES_ID, "semantic_role": "intervention",
                "shared_baseline_series_id": None, "applicable_probe_series_ids": [],
            }],
            "points": [point],
            "participant": "Participant 01",
        }],
    }


def _annotation():
    return {"markers": [{"point_id": POINT_ID, "center": [20.25, 30.75]}]}


def _source():
    return {"sha256": "a" * 64, "width": 100, "height": 80}


def test_truth_case_uses_degraded_pixels_graph_values_phase_and_printed_mode():
    case = builder._truth_case(_source(), SOURCE_ID, _scene(), _annotation())
    assert case["series"] == [{"series_key": SERIES_ID}]
    assert case["relations"] == [{
        "target_intervention_series_key": SERIES_ID,
        "shared_baseline_series_key": None,
        "applicable_probe_series_keys": [],
    }]
    assert case["points"] == [{
        "point_key": POINT_ID, "series_key": SERIES_ID,
        "source_pixel_x": 20.25, "source_pixel_y": 30.75,
        "graph_x": 1.0, "graph_y": 12.5,
        "expected_export_x": 1.0, "expected_export_mode": "printed_session",
        "authoritative_phase_code": "a",
    }]


def test_truth_case_rejects_missing_printed_session_endpoint():
    scene = _scene()
    scene["panels"][0]["ticks"].pop()
    with pytest.raises(builder.EvidenceError, match="first/final"):
        builder._truth_case(_source(), SOURCE_ID, scene, _annotation())


def test_build_validates_all_evidence_before_truth_regeneration(monkeypatch):
    calls = []
    evidence = {
        "dataset_seed": 393, "train_images": [], "dev_images": [],
        "sources": [], "report": {"cases": []}, "outputs": [],
    }
    monkeypatch.setattr(
        builder, "_validate_program_identity",
        lambda _expected: calls.append("program") or "1" * 64)
    monkeypatch.setattr(builder, "_validate_evidence", lambda: calls.append("evidence") or evidence)
    monkeypatch.setattr(
        builder.initial_scorer, "_validate_truth_environment",
        lambda: calls.append("truth-environment") or {})

    def regenerate(*_args):
        calls.append("regenerate")
        raise builder.EvidenceError("stop after ordering proof")

    monkeypatch.setattr(builder.family_ocr, "_regenerate", regenerate)
    with pytest.raises(builder.EvidenceError, match="ordering proof"):
        builder.build(builder.OUTPUT_ROOT / "unused.json", "1" * 64)
    assert calls == ["program", "evidence", "truth-environment", "regenerate"]


def test_malformed_evidence_never_reaches_truth_environment_or_regeneration(monkeypatch):
    calls = []

    def invalid():
        calls.append("evidence")
        raise builder.EvidenceError("bad report")

    monkeypatch.setattr(
        builder, "_validate_program_identity",
        lambda _expected: calls.append("program") or "1" * 64)
    monkeypatch.setattr(builder, "_validate_evidence", invalid)
    monkeypatch.setattr(
        builder.initial_scorer, "_validate_truth_environment",
        lambda: calls.append("truth-environment"))
    monkeypatch.setattr(
        builder.family_ocr, "_regenerate", lambda *_args: calls.append("regenerate"))
    with pytest.raises(builder.EvidenceError, match="bad report"):
        builder.build(builder.OUTPUT_ROOT / "unused.json", "1" * 64)
    assert calls == ["program", "evidence"]


def test_mutated_builder_is_rejected_before_evidence_or_truth(monkeypatch):
    calls = []
    monkeypatch.setattr(builder, "_validate_evidence", lambda: calls.append("evidence"))
    monkeypatch.setattr(
        builder.initial_scorer, "_validate_truth_environment",
        lambda: calls.append("truth-environment"))
    with pytest.raises(builder.EvidenceError, match="builder bytes differ"):
        builder.build(builder.OUTPUT_ROOT / "unused.json", "0" * 64)
    assert calls == []


def test_mutated_truth_guard_is_rejected_before_evidence_or_truth(monkeypatch, tmp_path):
    calls = []
    altered_guard = tmp_path / "score_initial_contour_output.py"
    altered_guard.write_text("# altered\n", encoding="utf-8")
    monkeypatch.setattr(builder, "INITIAL_SCORER_PATH", Path("altered-guard.py"))
    real_repository_path = builder._repository_path
    monkeypatch.setattr(
        builder, "_repository_path",
        lambda relative, label: altered_guard
        if relative == Path("altered-guard.py") else real_repository_path(relative, label))
    monkeypatch.setattr(builder, "_validate_evidence", lambda: calls.append("evidence"))
    monkeypatch.setattr(
        builder.initial_scorer, "_validate_truth_environment",
        lambda: calls.append("truth-environment"))
    actual_builder = builder._sha256_bytes(Path(builder.__file__).read_bytes())
    with pytest.raises(builder.EvidenceError, match="guard source bytes differ"):
        builder.build(builder.OUTPUT_ROOT / "unused.json", actual_builder)
    assert calls == []


def test_outputs_preserve_failed_case_and_reported_failure_type(monkeypatch, tmp_path):
    report_root = tmp_path / "run"
    report_root.mkdir()
    raw = {
        "source_id": SOURCE_ID, "image_sha256": "a" * 64,
        "width": 100, "height": 80, "status": "failed", "panel_count": 1,
        "correction_count": 0, "artifacts": [], "warnings": [],
        "error": "calibration failed", "failure_type": "ProductionWorkflowStageException",
    }
    assert raw["status"] == "failed"
    output = {
        "case_key": raw["source_id"], "source_sha256": raw["image_sha256"],
        "workflow_succeeded": raw["status"] == "completed",
        "failure_code": raw["failure_type"], "artifacts": [],
    }
    assert output == {
        "case_key": SOURCE_ID, "source_sha256": "a" * 64,
        "workflow_succeeded": False,
        "failure_code": "ProductionWorkflowStageException", "artifacts": [],
    }


def test_relations_preserve_shared_baseline_and_probe_ids():
    scene = _scene()
    baseline = "10000000-0000-4000-8000-000000000002"
    probe = "10000000-0000-4000-8000-000000000003"
    scene["panels"][0]["series"][0].update({
        "shared_baseline_series_id": baseline,
        "applicable_probe_series_ids": [probe],
    })
    for series_id, role in ((baseline, "baseline"), (probe, "maintenance")):
        # Use distinct canonical UUIDs while keeping the fixture small.
        point_id = ("20000000-0000-4000-8000-00000000000" +
                    str(len(scene["panels"][0]["points"]) + 1))
        phase_id = PHASE_ID
        scene["panels"][0]["series"].append({
            "series_id": series_id, "semantic_role": role,
            "shared_baseline_series_id": None, "applicable_probe_series_ids": [],
        })
        scene["panels"][0]["points"].append({
            "point_id": point_id, "series_id": series_id, "phase_id": phase_id,
            "observation_index": 1, "printed_x_value": 1, "estimated_x_value": None,
            "x_confidence": 1.0, "graph": [1.0, 10.0], "shape": "circle", "fill": "filled",
        })
    annotation = _annotation()
    annotation["markers"].extend([
        {"point_id": point["point_id"], "center": [25.0 + index, 35.0]}
        for index, point in enumerate(scene["panels"][0]["points"][1:])
    ])
    case = builder._truth_case(_source(), SOURCE_ID, scene, annotation)
    assert case["relations"][0]["shared_baseline_series_key"] == baseline
    assert case["relations"][0]["applicable_probe_series_keys"] == [probe]
