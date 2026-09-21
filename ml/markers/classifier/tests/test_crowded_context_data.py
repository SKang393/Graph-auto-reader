# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import math

import numpy as np
import pytest

from ml.markers.classifier.crowded_context_data import (
    CONTEXTS, DRAW_ORDERS, CrowdedContextCase, cases, prepare_case)
from ml.markers.classifier.runtime_diagnostic import SHAPES


def test_complete_train_only_inventory():
    recipes = cases()
    assert len(recipes) == 9*3*4*2*32 == 6912
    assert len({r.sample_id for r in recipes}) == len(recipes)
    assert {r.shape for r in recipes} == set(SHAPES)
    assert {r.draw_order for r in recipes} == set(DRAW_ORDERS)


@pytest.mark.parametrize("shape", SHAPES)
@pytest.mark.parametrize("context", CONTEXTS)
@pytest.mark.parametrize("draw_order", DRAW_ORDERS)
def test_original_pixels_are_deterministic_and_target_is_visible(shape, context, draw_order):
    case = CrowdedContextCase(shape, "degraded", context, draw_order, 3)
    pixels, row = prepare_case(case)
    again, other = prepare_case(case)
    np.testing.assert_array_equal(pixels, again)
    assert row == other and row["split"] == "train"
    assert pixels.shape == (1, 32, 32) and pixels.dtype == np.float32
    assert np.isfinite(pixels).all() and pixels.min() >= 0 and pixels.max() <= 1
    assert row["target_visible_pixel_count"] > 0 and row["fill_index"] == 2
    assert row["original_raster_sha256"] != row["without_target_raster_sha256"]
    if context in ("overlapping_pair", "three_neighbors"):
        assert any(math.dist(row["center"], n["center"]) < row["radius"]+n["radius"] for n in row["neighbors"])
    if context == "three_neighbors":
        assert len(row["neighbors"]) == 3


@pytest.mark.parametrize("case", [
    CrowdedContextCase("cat", "filled", "close_row", "target_last", 0),
    CrowdedContextCase("circle", "unknown", "close_row", "target_last", 0),
    CrowdedContextCase("circle", "filled", "private", "target_last", 0),
    CrowdedContextCase("circle", "filled", "close_row", "target_last", 32),
    CrowdedContextCase("circle", "filled", "close_row", "target_last", True),
])
def test_unregistered_recipes_are_rejected(case):
    with pytest.raises(ValueError, match="recipe"):
        prepare_case(case)
