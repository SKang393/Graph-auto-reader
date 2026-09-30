# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import numpy as np
import pytest

from ml.markers.center import crowded_coverage_data as data


def test_complete_population_has_unique_train_only_identities():
    cases = data.cases()
    assert len(cases) == 9*3*4*2*16
    assert len({c.sample_id for c in cases}) == len(cases)
    assert data.definition()["all_visible_neighbors_are_truth"]
    assert data.definition()["scope"] == "new_owned_train_only_supplement"


@pytest.mark.parametrize("shape", data.SHAPES)
@pytest.mark.parametrize("context,count", [("separated_pair", 2), ("overlapping_pair", 2), ("close_row", 3), ("three_neighbors", 4)])
@pytest.mark.parametrize("order", data.DRAW_ORDERS)
def test_all_neighbor_truths_participate_in_native_grid_labels(shape, context, count, order):
    rows = data.prepare_case(data.CrowdedCase(shape, "open", context, order, 1))
    record = rows.record
    assert record["split"] == "train" and record["truth_count"] == count
    assert len(record["visible_pixels_by_marker"]) == count and min(record["visible_pixels_by_marker"]) > 0
    centers = np.array([m["center"] for m in record["markers"]])
    coordinates = np.array(record["selected_proposal_coordinates"])
    distances = np.linalg.norm(coordinates[:, None, :]-centers[None, :, :], axis=2)
    positive = rows.labels == 1
    np.testing.assert_array_equal(positive, distances.min(axis=1) <= 3)
    assert positive.any() and (~positive).any()
    target = distances.argmin(axis=1)
    np.testing.assert_allclose(coordinates[positive]+4*rows.offsets[positive], centers[target[positive]], atol=1e-6)
    np.testing.assert_allclose(rows.radii[positive], np.array([m["radius"] for m in record["markers"]])[target[positive]], atol=1e-6)
    assert rows.patches.shape == (len(rows.labels), 3, 33, 33)
    assert np.isfinite(rows.patches).all() and rows.patches.min() >= 0 and rows.patches.max() <= 1
    assert not rows.patches[:, 1:].any()
    assert np.count_nonzero(rows.labels) == sum(record["positive_rows_by_marker"])


@pytest.mark.parametrize("fill", ["open", "filled", "degraded"])
def test_recipe_and_all_training_columns_repeat_exactly(fill):
    case = data.CrowdedCase("other", fill, "three_neighbors", "target_last", 15)
    first, second = data.prepare_case(case), data.prepare_case(case)
    assert first.record == second.record
    for name in ("patches", "labels", "offsets", "radii", "hard_negative"):
        np.testing.assert_array_equal(getattr(first, name), getattr(second, name))


def test_erased_markers_fail_instead_of_becoming_invented_positive_labels(monkeypatch):
    monkeypatch.setattr(data, "_draw_marker", lambda *args: None)
    with pytest.raises(ValueError, match="Invisible"):
        data.prepare_case(data.CrowdedCase("circle", "filled", "close_row", "target_last", 0))


@pytest.mark.parametrize("case", [
    data.CrowdedCase("invalid", "open", "close_row", "target_first", 0),
    data.CrowdedCase("circle", "open", "unknown", "target_first", 0),
    data.CrowdedCase("circle", "open", "close_row", "unknown", 0),
    data.CrowdedCase("circle", "open", "close_row", "target_first", 16),
])
def test_unregistered_recipe_is_rejected(case):
    with pytest.raises(ValueError, match="Unknown"):
        data.prepare_case(case)
