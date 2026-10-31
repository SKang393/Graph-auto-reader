# SPDX-License-Identifier: Apache-2.0
import importlib.util
from pathlib import Path
import uuid

ROOT = Path(__file__).resolve().parents[2]

def load(name, file):
    spec = importlib.util.spec_from_file_location(name, file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

a = load("association_under_test", Path(__file__).with_name("association.py"))


def test_affine_panel_to_source_transform_and_bounds():
    assert a.transform_panel_to_source([4, 7], [1, 0, 80, 0, 1, 20, 0, 0, 1], 1200, 350) == (84, 27)
    import pytest
    with pytest.raises(ValueError, match="outside"):
        a.transform_panel_to_source([4, 7], [1, 0, 2000, 0, 1, 0, 0, 0, 1], 1200, 350)
    with pytest.raises(ValueError, match="affine"):
        a.transform_panel_to_source([4, 7], [1, 0, 0, 0, 1, 0, 0.01, 0, 1], 1200, 350)


def test_two_pixel_rule_is_inclusive_and_all_labels_remain_review_only():
    row = a.associate_source("s0", "completed", {"completed": 1, "failed": 0, "not_processed": 0},
        [{"candidate_id": "c", "center": [12, 10], "symbol_input": False, "detector_candidate": True}],
        [{"truth_id": "m", "center": [10, 10], "connection": "connected"}], [])
    assert row["candidate_status_counts"] == {"review_connected_diagnostic": 1}
    assert row["truth_status_counts"] == {"matched_connected_review_only": 1}
    assert row["trainable_rows"] == row["negative_training_rows"] == 0


def test_overlay_symbol_duplicate_ambiguous_unmatched_and_isolated_are_review_only():
    row = a.associate_source("s0", "completed", {"completed": 1, "failed": 0, "not_processed": 0}, [
        {"candidate_id": "symbol", "center": [50, 50], "symbol_input": True, "detector_candidate": False},
        {"candidate_id": "dup-a", "center": [10, 10], "symbol_input": False, "detector_candidate": True},
        {"candidate_id": "dup-b", "center": [10, 10], "symbol_input": False, "detector_candidate": True},
        {"candidate_id": "amb", "center": [30, 30], "symbol_input": False, "detector_candidate": True},
        {"candidate_id": "unmatched", "center": [90, 90], "symbol_input": False, "detector_candidate": True},
    ], [
        {"truth_id": "m1", "center": [10, 10], "connection": "connected"},
        {"truth_id": "m2", "center": [31, 30], "connection": "connected"},
        {"truth_id": "m3", "center": [29, 30], "connection": "isolated"},
        {"truth_id": "m4", "center": [70, 70], "connection": "isolated"},
    ], [{"overlay_id": "o", "center": [50, 50]}])
    counts = row["candidate_status_counts"]
    assert counts["review_overlay"] == 1
    assert counts["review_duplicate"] == 2
    assert counts["review_ambiguous"] == 1
    assert counts["review_unmatched"] == 1
    assert row["truth_status_counts"]["review_ambiguous"] == 2
    assert row["truth_status_counts"]["unmatched_review_only"] == 2
    assert row["truth_status_counts_by_connection"]["isolated"]["unmatched_review_only"] == 1
    assert row["trainable_rows"] == row["negative_training_rows"] == 0


def test_partial_failure_does_not_turn_unobserved_truth_into_false_negative():
    row = a.associate_source("failed-source", "failed", {"completed": 1, "failed": 1, "not_processed": 0},
        [{"candidate_id": "partial", "center": [10, 10], "symbol_input": False, "detector_candidate": True}],
        [{"truth_id": "seen", "center": [10, 10], "connection": "connected"},
         {"truth_id": "unseen", "center": [80, 80], "connection": "isolated"}], [])
    assert row["truth_status_counts"] == {"matched_connected_review_only": 1, "not_observed_capture_incomplete": 1}


def test_full_64_source_summary_preserves_source_and_panel_failures():
    sources, truths = [], {}
    for i in range(64):
        source_id = str(uuid.UUID(int=i + 1))
        failed = i in (3, 8)
        panels = {"completed": 0 if failed else 1, "failed": 1 if failed else 0, "not_processed": 0}
        if i == 8:
            panels = {"completed": 0, "failed": 0, "not_processed": 0}
        sources.append({"source_id": source_id, "capture_status": "failed" if failed else "completed",
                        "panel_status_counts": panels,
                        "failure_type_counts": {"SyntheticFailure": 1} if failed else {}, "candidates": []})
        truths[source_id] = {"markers": [{"truth_id": f"m{i}", "center": [5, 5], "connection": "isolated"}], "overlays": []}
    result = a.associate_full(sources, truths)
    assert result["source_count"] == 64
    assert result["completed_source_count"] == 62
    assert result["failed_source_count"] == 2
    assert result["panel_status_counts"] == {"completed": 62, "failed": 1, "not_processed": 0}
    assert result["failure_type_counts"] == {"SyntheticFailure": 2}
    assert result["truth_status_counts"]["not_observed_capture_incomplete"] == 2
    assert result["truth_status_counts_by_connection"]["isolated"]["not_observed_capture_incomplete"] == 2
    assert result["negative_training_rows"] == result["trainable_rows"] == 0
    assert result["training_approved"] is False and result["optimizer_steps"] == 0


def test_identity_inventory_must_be_exactly_64_and_unique():
    import pytest
    with pytest.raises(ValueError, match="64 full-train"):
        a.associate_full([], {})
    s = {"source_id": "a", "capture_status": "completed", "panel_status_counts": {"completed": 1, "failed": 0, "not_processed": 0}, "candidates": []}
    truth = {str(i): {"markers": [], "overlays": []} for i in range(64)}
    with pytest.raises(ValueError, match="identities differ"):
        a.associate_full([s] * 64, truth)
