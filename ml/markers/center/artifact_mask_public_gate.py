# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from __future__ import annotations

from collections.abc import Iterable, Mapping
from typing import Any

from ml.policy.evidence_policy import tier1_acceptance_bars


PROFILE = "marker-center-artifact-mask-public-gate-v1"
PROHIBITED_STRUCTURE_KINDS = (
    "text",
    "axis",
    "tick",
    "divider",
    "bracket",
    "arrow_shaft",
    "arrowhead",
    "legend",
    "line_intersection",
)


def _nonnegative_integer(value: object, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ValueError(f"{name} must be a nonnegative integer")
    return value


def _prohibited_hits(value: object) -> dict[str, int]:
    if not isinstance(value, Mapping) or set(value) != set(PROHIBITED_STRUCTURE_KINDS):
        raise ValueError("prohibited_structure_hits must use the frozen taxonomy")
    return {
        kind: _nonnegative_integer(value[kind], f"prohibited_structure_hits.{kind}")
        for kind in PROHIBITED_STRUCTURE_KINDS
    }


def evaluate_fixture(row: Mapping[str, Any]) -> dict[str, Any]:
    fixture_id = row.get("fixture_id")
    if not isinstance(fixture_id, str) or not fixture_id.strip():
        raise ValueError("fixture_id must be a nonempty string")
    expected_count = _nonnegative_integer(row.get("expected_count"), "expected_count")
    predicted_count = _nonnegative_integer(row.get("predicted_count"), "predicted_count")
    false_positive_count = _nonnegative_integer(
        row.get("false_positive_count"), "false_positive_count"
    )
    false_negative_count = _nonnegative_integer(
        row.get("false_negative_count"), "false_negative_count"
    )
    duplicate_count = _nonnegative_integer(row.get("duplicate_count"), "duplicate_count")
    hits = _prohibited_hits(row.get("prohibited_structure_hits"))
    exact_count = (
        predicted_count == expected_count
        and false_positive_count == 0
        and false_negative_count == 0
        and duplicate_count == 0
        and not any(hits.values())
    )
    return {
        "fixture_id": fixture_id,
        "expected_count": expected_count,
        "predicted_count": predicted_count,
        "exact_count": exact_count,
        "false_positive_count": false_positive_count,
        "false_negative_count": false_negative_count,
        "duplicate_count": duplicate_count,
        "prohibited_structure_hits": hits,
    }


def evaluate_public_gate(rows: Iterable[Mapping[str, Any]]) -> dict[str, Any]:
    fixtures = [evaluate_fixture(row) for row in rows]
    fixture_ids = [row["fixture_id"] for row in fixtures]
    if len(fixtures) < 3 or len(set(fixture_ids)) != len(fixture_ids):
        raise ValueError("the public gate requires at least three unique fixtures")
    aggregate_hits = {
        kind: sum(row["prohibited_structure_hits"][kind] for row in fixtures)
        for kind in PROHIBITED_STRUCTURE_KINDS
    }
    exact_fixture_count = sum(bool(row["exact_count"]) for row in fixtures)
    false_positive_count = sum(row["false_positive_count"] for row in fixtures)
    false_negative_count = sum(row["false_negative_count"] for row in fixtures)
    duplicate_count = sum(row["duplicate_count"] for row in fixtures)
    expected_count = sum(row["expected_count"] for row in fixtures)
    predicted_count = sum(row["predicted_count"] for row in fixtures)
    true_positive_count = expected_count - false_negative_count
    if true_positive_count < 0 or predicted_count != true_positive_count + false_positive_count:
        raise ValueError("aggregate TP, FP, FN, expected, and predicted counts are inconsistent")
    precision = true_positive_count / predicted_count if predicted_count else 0.0
    recall = true_positive_count / expected_count if expected_count else 0.0
    prohibited_hit_count = sum(aggregate_hits.values())
    prohibited_hit_rate = prohibited_hit_count / predicted_count if predicted_count else 0.0
    bars = tier1_acceptance_bars()
    passed = (
        precision >= bars["marker_center_precision_minimum"]
        and recall >= bars["marker_center_recall_minimum"]
        and prohibited_hit_rate <= bars["prohibited_structure_hit_rate_maximum"]
    )
    return {
        "profile": PROFILE,
        "status": "pass" if passed else "fail",
        "fixture_count": len(fixtures),
        "exact_fixture_count": exact_fixture_count,
        "downstream_false_positive_count": false_positive_count,
        "downstream_false_negative_count": false_negative_count,
        "downstream_duplicate_count": duplicate_count,
        "true_positive_count": true_positive_count,
        "false_positive_count": false_positive_count,
        "false_negative_count": false_negative_count,
        "artifact_precision": precision,
        "artifact_recall": recall,
        "prohibited_structure_hit_rate": prohibited_hit_rate,
        "prohibited_structure_hits": aggregate_hits,
        "fixture_results": fixtures,
    }


__all__ = [
    "PROFILE",
    "PROHIBITED_STRUCTURE_KINDS",
    "evaluate_fixture",
    "evaluate_public_gate",
]
