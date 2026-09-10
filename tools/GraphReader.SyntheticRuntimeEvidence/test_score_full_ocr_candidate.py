# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from dataclasses import replace
import importlib.util
from pathlib import Path
import sys
from types import SimpleNamespace

import pytest


MODULE_PATH = Path(__file__).with_name("score_full_ocr_candidate.py")
SPEC = importlib.util.spec_from_file_location("score_full_ocr_candidate", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
scorer = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = scorer
SPEC.loader.exec_module(scorer)


def _truth(identity: str, box, text: str, role: str = "annotation"):
    return scorer.FullTextTruth(identity * 64, "a" * 64, box, text, role,
                                scorer._canonical_role(role))


def _prediction(identity: str, box, text: str, role: str = "annotation"):
    return scorer.FullTextPrediction(identity, "a" * 64, box, text, role)


def test_ambiguous_pairing_matches_frozen_maximum_cardinality_and_ignores_text_role() -> None:
    truths = (
        _truth("1", scorer.Box(0, 0, 10, 10), "first"),
        _truth("2", scorer.Box(4, 0, 14, 10), "second"),
    )
    predictions = (
        _prediction("p0", scorer.Box(2, 0, 12, 10), "wrong", "other"),
        _prediction("p1", scorer.Box(0, 0, 10, 10), "first"),
    )
    pairs = scorer._maximum_cardinality_pairs(predictions, truths)
    assert pairs == ((1, 0), (0, 1))
    assert len(pairs) == scorer.geometry.maximum_cardinality_matches(
        predictions, tuple(truth.box for truth in truths)
    ) == 2
    changed = tuple(replace(item, text="changed", role="participant") for item in predictions)
    assert scorer._maximum_cardinality_pairs(changed, truths) == pairs


def test_full_denominator_cer_counts_deletions_insertions_and_wrong_roles() -> None:
    truths = (
        _truth("1", scorer.Box(0, 0, 10, 10), "AB", "annotation"),
        _truth("2", scorer.Box(20, 0, 30, 10), "cat", "participant"),
    )
    predictions = (
        _prediction("matched", scorer.Box(20, 0, 30, 10), "cut", "participant"),
        _prediction("extra", scorer.Box(40, 0, 50, 10), "xy", "other"),
    )
    metrics = scorer._score_split(truths, {"a" * 64: predictions})
    assert metrics["truth_region_count"] == 2
    assert metrics["geometry_matched_region_count"] == 1
    assert metrics["recognition_exact_accuracy"] == 0.0
    assert metrics["matched_pair_edit_count"] == 1
    assert metrics["unmatched_truth_deletion_edit_count"] == 2
    assert metrics["unmatched_prediction_insertion_edit_count"] == 2
    assert metrics["character_error_count"] == 5
    assert metrics["character_error_rate"] == 1.0
    assert metrics["role_accuracy"] == 0.5


def test_condition_label_maps_explicitly_to_other_and_unknown_roles_fail() -> None:
    assert scorer._canonical_role("condition_label") == "other"
    with pytest.raises(scorer.EvidenceError, match="no reviewed runtime mapping"):
        scorer._canonical_role("new_unreviewed_role")


def test_primary_text_is_compared_exactly_without_normalization() -> None:
    truth = _truth("1", scorer.Box(0, 0, 10, 10), "A B")
    prediction = _prediction("p", scorer.Box(0, 0, 10, 10), "ab")
    metrics = scorer._score_split((truth,), {"a" * 64: (prediction,)})
    assert metrics["recognition_exact_count"] == 0
    assert metrics["character_error_count"] == 3


def test_geometry_and_failure_metrics_match_frozen_scorer_fields() -> None:
    truth = _truth("1", scorer.Box(0, 0, 10, 10), "A")
    matched = SimpleNamespace(box=scorer.Box(0, 0, 10, 10))
    extra = SimpleNamespace(box=scorer.Box(20, 0, 30, 10))
    panel = SimpleNamespace(split="validation")
    evidence = SimpleNamespace(
        raw_by_source={"a" * 64: (matched, extra)},
        recognized_by_source={"a" * 64: (matched,)},
        explicit_region_failures={"train": 0, "validation": 1},
        failed_panel_raw_regions={"train": 0, "validation": 0},
        panels={"panel": panel},
        report={"panels": [{"panel_id": "panel", "status": "completed"}]},
    )
    raw, recognized, failures = scorer._geometry_and_failure_metrics(
        evidence, {"validation": (truth,)}
    )
    assert raw["validation"] == scorer.geometry._score_predictions(
        (truth,), {"a" * 64: (matched, extra)}
    )
    assert recognized["validation"] == scorer.geometry._score_predictions(
        (truth,), {"a" * 64: (matched,)}
    )
    assert failures["validation"] == {
        "failed_panel_count": 0,
        "explicit_region_failure_count": 1,
        "raw_regions_on_failed_panels": 0,
        "raw_regions_without_successful_recognition": 1,
    }
