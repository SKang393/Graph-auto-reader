# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from collections import Counter
from hashlib import sha256
from pathlib import Path

import numpy as np
import pytest
import torch

from ml.markers.center.component_diversity_v27 import diagnose_cohorts as diagnosis
from ml.markers.center.line_aware_v1.pipeline import ProposalBatch


def _rows() -> list[dict[str, object]]:
    rows = []
    for transition, count in diagnosis.EXPECTED_TRANSITIONS.items():
        for index in range(count):
            rows.append({
                "transition": transition,
                "morphology": diagnosis.MORPHOLOGIES[index % len(diagnosis.MORPHOLOGIES)],
                "diameter_px": "12",
                "radius_category": "within_2_5_to_8px",
                "layout_band": "center_middle",
                "anti_alias_radius_px": "0.25",
                "positive_bins": {index % 4},
                "changed_negative_bins": {0, 2},
            })
    return rows


def test_summary_conserves_all_fixed_truth_transitions() -> None:
    report = diagnosis._summarize(_rows())
    assert report["transition_counts"] == diagnosis.EXPECTED_TRANSITIONS
    assert sum(report["transition_counts"].values()) == 2004
    for values in report["cohorts"].values():
        aggregate = Counter()
        for row in values.values():
            aggregate.update(row)
        assert dict(aggregate) == diagnosis.EXPECTED_TRANSITIONS


def test_overlap_is_cohort_specific_and_conserved() -> None:
    report = diagnosis._summarize(_rows())
    for transition, expected in diagnosis.EXPECTED_TRANSITIONS.items():
        row = report["changed_negative_selection_overlap"][transition]
        assert row["truth_count"] == expected
        assert 0 <= row["truths_with_changed_negative_bin_overlap"] <= expected
        assert row["fraction"] == pytest.approx(
            row["truths_with_changed_negative_bin_overlap"] / expected
        )


def test_summary_rejects_nonconserving_transition_inventory() -> None:
    rows = _rows()
    rows.pop()
    with pytest.raises(diagnosis.CohortDiagnosisError, match="counts changed"):
        diagnosis._summarize(rows)


def test_changed_selection_bins_detects_only_count_changes() -> None:
    cells = {}
    for index in range(50):
        cells[f"negative_gt12|generic|within_2_5_to_8px-{index}"] = {
            "selected_bin_counts_before": {"1": 2, "2": 3},
            "selected_bin_counts_after": {"1": 2, "2": 4},
        }
    # Cell identities are exactly three fields; use distinct semantic strata.
    cells = {
        f"negative_gt12|generic{index}|within_2_5_to_8px": value
        for index, value in enumerate(cells.values())
    }
    changed = diagnosis._changed_selection_bins({"allocations": {"component_cells": cells}})
    assert changed == {"within_2_5_to_8px": {2}}


def test_changed_selection_bins_rejects_invalid_inventory_and_bin() -> None:
    with pytest.raises(diagnosis.CohortDiagnosisError, match="inventory"):
        diagnosis._changed_selection_bins({"allocations": {"component_cells": {}}})
    cells = {
        f"negative_gt12|generic{index}|above_8px": {
            "selected_bin_counts_before": {"64": 0},
            "selected_bin_counts_after": {"64": 1},
        }
        for index in range(50)
    }
    with pytest.raises(diagnosis.CohortDiagnosisError, match="out of range"):
        diagnosis._changed_selection_bins({"allocations": {"component_cells": cells}})


def test_generator_cohort_labels_are_fixed_and_bounded() -> None:
    class Scene:
        diameters = (12.0,)
        centers = ((112.0, 84.0),)

    row = diagnosis._cohort_row(0, 0, Scene())
    assert row == {
        "morphology": "filled_circle",
        "diameter_px": "12",
        "radius_category": "within_2_5_to_8px",
        "layout_band": "center_middle",
        "anti_alias_radius_px": "0",
    }


def test_one_pixel_truth_uses_generator_point_bypass() -> None:
    class Scene:
        diameters = (1.0,)
        centers = ((112.0, 84.0),)

    assert diagnosis._cohort_row(5, 0, Scene())["morphology"] == "point"


def test_cached_proposals_are_reordered_without_changing_patch_bytes() -> None:
    patches = torch.arange(3 * 3 * 33 * 33, dtype=torch.float32).reshape(3, 3, 33, 33)
    extracted = ProposalBatch(
        patches=patches,
        coordinates=torch.tensor(((0.0, 0.0), (4.0, 0.0), (8.0, 0.0))),
    )
    cached = np.asarray(((8.0, 0.0), (0.0, 0.0)), dtype=np.float32)
    aligned = diagnosis._align_cached_proposals(extracted, cached)
    assert np.array_equal(aligned.coordinates.numpy(), cached)
    assert torch.equal(aligned.patches, patches[torch.tensor((2, 0))])


def test_cached_proposal_alignment_rejects_unknown_coordinate() -> None:
    extracted = ProposalBatch(
        patches=torch.zeros((1, 3, 33, 33)),
        coordinates=torch.tensor(((0.0, 0.0),)),
    )
    with pytest.raises(diagnosis.CohortDiagnosisError, match="absent"):
        diagnosis._align_cached_proposals(
            extracted, np.asarray(((4.0, 0.0),), dtype=np.float32)
        )


def test_helper_source_authentication_rejects_byte_drift(tmp_path: Path) -> None:
    relative = Path("helper.py")
    payload = b"authenticated helper\n"
    (tmp_path / relative).write_bytes(payload)
    expected = {relative: sha256(payload).hexdigest()}
    diagnosis._verify_helper_sources(tmp_path, expected)
    (tmp_path / relative).write_bytes(payload + b"drift")
    with pytest.raises(diagnosis.CohortDiagnosisError, match="helper source changed"):
        diagnosis._verify_helper_sources(tmp_path, expected)
