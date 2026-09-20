# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import numpy as np
import pytest

from ml.markers.center import shape_coverage_data as data
from ml.markers.classifier.graph_context_data import CoverageCase


def test_population_covers_every_supported_shape_without_dev_inputs():
    cases = data.cases()
    assert len(cases) == 9*3*5*16
    assert len({case.sample_id for case in cases}) == len(cases)
    assert {c.shape for c in cases} == set(data.SHAPES)
    assert data.definition()["scope"] == "new_owned_train_only_supplement"


@pytest.mark.parametrize("shape", data.SHAPES)
def test_every_shape_keeps_visible_positive_rows_and_consistent_offsets(shape):
    rows = data.prepare_case(CoverageCase(shape, "open", "isolated", 1))
    positive = rows.labels == 1
    coordinates = np.array(rows.record["selected_proposal_coordinates"])
    assert positive.any() and (~positive).any()
    np.testing.assert_allclose(coordinates[positive]+4*rows.offsets[positive],
        np.broadcast_to(rows.record["center"], (int(positive.sum()), 2)), atol=1e-6)
    assert rows.patches.shape == (len(rows.labels), 3, 33, 33)
    assert np.isfinite(rows.patches).all() and rows.patches.min() >= 0 and rows.patches.max() <= 1
    assert not rows.patches[:, 1:].any()
    assert rows.record["visible_local_pixels"] > 0


def test_repeated_recipe_is_byte_identical():
    case = CoverageCase("triangle_down", "filled", "bent_line", 15)
    first, second = data.prepare_case(case), data.prepare_case(case)
    assert first.record == second.record
    for name in ("patches", "labels", "offsets", "radii", "hard_negative"):
        np.testing.assert_array_equal(getattr(first, name), getattr(second, name))


def test_visible_hollow_marker_without_supported_center_remains_in_population():
    rows = data.prepare_case(CoverageCase("square", "open", "axis_contact", 10))
    assert rows.record["truth_count"] == 1
    assert rows.record["visible_local_pixels"] > 0
    assert not rows.record["truth_supported_by_positive_proposal"]
    assert len(rows.labels) >= 10 and not rows.labels.any()


@pytest.mark.parametrize("case", [
    CoverageCase("circle", "open", "neighbor", 0),
    CoverageCase(None, None, "text", 0),
    CoverageCase("circle", "open", "isolated", 16),
])
def test_unlabeled_neighbor_or_unbound_recipe_is_rejected(case):
    with pytest.raises(ValueError, match="Unknown"):
        data.prepare_case(case)
