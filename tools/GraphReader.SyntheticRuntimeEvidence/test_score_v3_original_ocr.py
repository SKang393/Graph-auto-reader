# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path

import pytest

import score_v3_original_ocr as scorer
from ml.ocr.component_region_detector_v6.dataset import Box, Component


def _polygon(left: float, top: float, right: float, bottom: float):
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


def _panel(regions, *, unmasked_sha="a" * 64):
    return {
        "status": "seed-completed",
        "width": 20,
        "height": 10,
        "source_gray": {"sha256": unmasked_sha},
        "panel_to_source_matrix": [1, 0, 30, 0, 1, 5, 0, 0, 1],
        "ocr_proposal_diagnostic": {
            "coordinate_space": "original_pixels",
            "used_as_accepted_evidence": False,
            "unmasked_input_sha256": unmasked_sha,
            "unmasked_model_regions": regions,
        },
    }


def _region(identifier, left, top, right, bottom):
    return {
        "region_id": identifier,
        "coordinate_space": "original_pixels",
        "polygon": _polygon(left, top, right, bottom),
    }


def test_unmasked_regions_bind_original_gray_and_map_once_to_source():
    predictions = scorer._regions_from_panel(_panel([
        _region("one", 2, 1, 8, 5),
        _region("two", 10, 2, 18, 8),
    ]))
    assert [item.box for item in predictions] == [
        Box(32, 6, 38, 10),
        Box(40, 7, 48, 13),
    ]

    bad = _panel([_region("one", 2, 1, 8, 5)])
    bad["ocr_proposal_diagnostic"]["unmasked_input_sha256"] = "b" * 64
    with pytest.raises(scorer.EvidenceError, match="original Gray8"):
        scorer._regions_from_panel(bad)


def test_duplicate_panel_regions_remain_false_positives_under_maximum_matching():
    truth = Box(10, 10, 20, 20)
    same = Component(10, 10, 19, 19, 100, 1)
    metrics = scorer._metrics(
        ["source"], {"source": (truth,)}, {"source": (same, same)})
    assert metrics == {
        "source_count": 1,
        "truth_region_count": 1,
        "predicted_region_count": 2,
        "true_positives": 1,
        "false_positives": 1,
        "false_negatives": 0,
        "precision": 0.5,
        "recall": 1.0,
    }


def test_full_source_denominator_retains_truth_outside_all_crops():
    truths = {
        "source": (
            Box(2, 2, 6, 6),
            Box(40, 40, 45, 45),
        )
    }
    prediction = Component(2, 2, 5, 5, 16, 1)
    metrics = scorer._metrics(["source"], truths, {"source": (prediction,)})
    assert metrics["truth_region_count"] == 2
    assert metrics["true_positives"] == 1
    assert metrics["false_negatives"] == 1
    assert scorer._truth_fully_covered(truths["source"][0], [
        {"x": 0, "y": 0, "width": 10, "height": 10}
    ])
    assert not scorer._truth_fully_covered(truths["source"][1], [
        {"x": 0, "y": 0, "width": 10, "height": 10}
    ])


def test_bound_json_rejects_tampered_report_bytes(tmp_path: Path):
    path = tmp_path / "report.json"
    original = (json.dumps({"status": "complete"}) + "\n").encode("utf-8")
    path.write_bytes(original)
    expected = sha256(original).hexdigest()
    value, payload = scorer._read_bound_json(path, expected, "runtime report")
    assert value["status"] == "complete"
    assert payload == original

    path.write_text('{"status":"changed"}\n', encoding="utf-8")
    with pytest.raises(scorer.EvidenceError, match="differ from the binding"):
        scorer._read_bound_json(path, expected, "runtime report")


def test_all_exchange_files_are_authenticated_before_loader_truth_can_run(tmp_path: Path):
    artifact_root = tmp_path / "artifacts" / "exchange"
    records = []
    for name in ("train", "dev"):
        manifest = artifact_root / f"{name}-manifest.json"
        report = artifact_root / f"{name}-report.json"
        manifest.parent.mkdir(parents=True, exist_ok=True)
        manifest.write_text('{"images":[]}\n', encoding="utf-8")
        report.write_text('{"cases":[]}\n', encoding="utf-8")
        records.append({
            "manifest_path": manifest.relative_to(tmp_path).as_posix(),
            "manifest_sha256": sha256(manifest.read_bytes()).hexdigest(),
            "report_path": report.relative_to(tmp_path).as_posix(),
            "report_sha256": sha256(report.read_bytes()).hexdigest(),
        })
    document = {"train": [records[0]], "dev": records[1]}
    scorer._preauthenticate_exchange_files(document, tmp_path)

    (artifact_root / "dev-report.json").write_text('{"cases":[1]}\n', encoding="utf-8")
    with pytest.raises(scorer.EvidenceError, match="development exchange report bytes"):
        scorer._preauthenticate_exchange_files(document, tmp_path)


def test_region_validation_rejects_out_of_bounds_and_duplicate_ids():
    with pytest.raises(scorer.EvidenceError, match="inside the panel"):
        scorer._regions_from_panel(_panel([_region("outside", 1, 1, 21, 5)]))
    with pytest.raises(scorer.EvidenceError, match="panel-unique"):
        scorer._regions_from_panel(_panel([
            _region("same", 1, 1, 4, 4),
            _region("same", 5, 1, 8, 4),
        ]))
