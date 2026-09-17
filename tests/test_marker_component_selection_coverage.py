# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys

import numpy as np
import pytest
import torch

from ml.markers.center.mask_preserving_v24 import stratified_background


SCRIPT = (
    Path(__file__).resolve().parents[1]
    / "tools/GraphReader.SyntheticRuntimeEvidence/preflight_marker_component_selection_coverage.py"
)
SPEC = importlib.util.spec_from_file_location(
    "preflight_marker_component_selection_coverage", SCRIPT
)
assert SPEC is not None and SPEC.loader is not None
subject = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = subject
SPEC.loader.exec_module(subject)


def _patch(style: str, *, text: bool = False, artifact: bool = False) -> torch.Tensor:
    value = torch.zeros((3, 33, 33), dtype=torch.float32)
    if style == "horizontal":
        value[0, 0, :] = 1.0
    elif style == "vertical":
        value[0, :, 0] = 1.0
    elif style == "full":
        value[0] = 1.0
    elif style != "empty":
        raise ValueError(style)
    if text:
        value[1, 16, 16] = 1.0
    if artifact:
        value[2, 16, 16] = 1.0
    return value


@pytest.mark.parametrize("budget", (5, 11))
def test_streamed_seeded_selection_matches_multibin_frozen_selector(
    budget: int,
) -> None:
    patches = (
        torch.stack((
            _patch("empty"), _patch("empty"), _patch("empty"),
            _patch("horizontal"), _patch("horizontal"), _patch("vertical"),
            _patch("empty", text=True), _patch("empty", artifact=True),
            _patch("empty", text=True, artifact=True),
        )),
        torch.stack((
            _patch("empty"), _patch("empty"), _patch("horizontal"),
            _patch("full"), _patch("full"), _patch("empty", text=True),
            _patch("vertical", artifact=True),
        )),
    )
    eligible = (torch.arange(9, dtype=torch.int64), torch.arange(7, dtype=torch.int64))
    seed = 20260904
    frozen = stratified_background.select_stratified_background(
        patches, eligible, budget, seed
    )
    bin_ids = np.concatenate([
        stratified_background._bin_ids(value).numpy() for value in patches
    ])
    rows = np.arange(16, dtype=np.int64)
    unique_bins = len(np.unique(bin_ids))
    if budget == 5:
        assert unique_bins > budget
    else:
        assert unique_bins < budget

    selected, capacities, quotas = subject._select_seeded_rows(
        rows, bin_ids, budget, seed=seed
    )
    frozen_global = sorted(
        index if scene == 0 else 9 + index
        for scene, indices in enumerate(frozen.selections)
        for index in indices
    )

    assert selected.tolist() == frozen_global
    assert capacities == frozen.capacities
    assert quotas == frozen.quotas


def test_component_selection_is_deterministic_and_preserves_mandatory_cell_totals() -> None:
    source = np.asarray(
        (True, True, True, True, False, False, True, True, False, False, False, False),
        dtype=np.bool_,
    )
    positive = np.asarray(
        (True, False, False, False, False, False, False, False, False, False, False, False),
        dtype=np.bool_,
    )
    protected = np.asarray(
        (False, True, False, False, False, False, False, False, False, False, False, False),
        dtype=np.bool_,
    )
    reserved = np.asarray(
        (False, False, False, False, False, False, True, False, False, False, False, False),
        dtype=np.bool_,
    )
    cells = [None, "a", "a", "a", "a", "a", "b", "b", "b", "b", "b", "b"]
    bins = np.asarray((0, 0, 1, 2, 3, 4, 0, 1, 2, 3, 4, 5), dtype=np.uint8)

    first, report = subject._select_component_mask(
        source, positive, protected, reserved, cells, bins, seed=20260904
    )
    second, second_report = subject._select_component_mask(
        source, positive, protected, reserved, cells, bins, seed=20260904
    )

    assert np.array_equal(first, second)
    assert report == second_report
    assert first[positive].all()
    assert first[protected].all()
    assert first[reserved].all()
    assert int(first[np.asarray([value == "a" for value in cells])].sum()) == 3
    assert int(first[np.asarray([value == "b" for value in cells])].sum()) == 2
    assert report["a"]["source_selected_rows"] == report["a"]["mandatory_rows"] + report["a"]["fill_budget"]


def test_final_validation_rejects_family_change_and_component_cell_drift() -> None:
    source_component = np.asarray((True, True, False, True, False), dtype=np.bool_)
    proposed_component = np.asarray((True, False, True, True, False), dtype=np.bool_)
    positive = np.asarray((True, False, False, False, False), dtype=np.bool_)
    protected = np.asarray((False, False, False, True, False), dtype=np.bool_)
    reserved = np.zeros(5, dtype=np.bool_)
    cells = [None, "a", "a", "b", "b"]
    family = np.asarray((True, False, True), dtype=np.bool_)

    subject._validate_final_masks(
        source_component, proposed_component, positive, protected, reserved,
        cells, family, family.copy(),
    )
    changed_family = family.copy()
    changed_family[1] = True
    with pytest.raises(subject.PreflightError, match="family selection bytes changed"):
        subject._validate_final_masks(
            source_component, proposed_component, positive, protected, reserved,
            cells, family, changed_family,
        )
    drifted_component = proposed_component.copy()
    drifted_component[2] = False
    with pytest.raises(subject.PreflightError, match="selected-row total changed|cell quota changed"):
        subject._validate_final_masks(
            source_component, drifted_component, positive, protected, reserved,
            cells, family, family.copy(),
        )


def test_aggregate_counts_excludes_selected_rows_outside_eligible_mask() -> None:
    labels = ["positive", "negative", "negative", "other"]
    eligible = np.asarray((False, True, True, False), dtype=np.bool_)
    before = np.asarray((True, True, False, True), dtype=np.bool_)
    after = np.asarray((True, False, True, True), dtype=np.bool_)

    result = subject._aggregate_counts(labels, eligible, before, after)

    assert result["positive"] == {
        "eligible": 0,
        "selected_before": 0,
        "selected_after": 0,
    }
    assert result["negative"] == {
        "eligible": 2,
        "selected_before": 1,
        "selected_after": 1,
    }
    assert result["other"] == {
        "eligible": 0,
        "selected_before": 0,
        "selected_after": 0,
    }
