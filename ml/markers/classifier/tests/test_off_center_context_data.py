# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import hashlib
import math

import numpy as np
import pytest

from ml.markers.classifier.crowded_context_data import (
    CONTEXTS, DRAW_ORDERS, CrowdedContextCase, render_original)
from ml.markers.classifier.off_center_context_data import cases, prepare_case
from ml.markers.classifier.runtime_diagnostic import SHAPES


def test_all_previous_owned_recipes_are_retained():
    recipes = cases()
    assert len(recipes) == 6912
    assert len({r.sample_id for r in recipes}) == len(recipes)
    assert {r.variant for r in recipes} == set(range(32))


@pytest.mark.parametrize("shape", SHAPES)
@pytest.mark.parametrize("context", CONTEXTS)
@pytest.mark.parametrize("draw_order", DRAW_ORDERS)
def test_negative_has_no_authored_center_within_native_tolerance(shape, context, draw_order):
    case = CrowdedContextCase(shape, "degraded", context, draw_order, 3)
    pixels, row = prepare_case(case)
    repeated, repeat_row = prepare_case(case)
    np.testing.assert_array_equal(pixels, repeated)
    assert row == repeat_row and row["split"] == "train"
    assert row["artifact"] and row["shape_index"] == row["fill_index"] == -1
    assert row["shape"] is row["fill"] is None
    assert row["sample_id"] != case.sample_id and row["parent_sample_id"] == case.sample_id
    assert pixels.shape == (1, 32, 32) and pixels.dtype == np.float32
    assert np.isfinite(pixels).all() and pixels.min() >= 0 and pixels.max() <= 1
    points = [row["center"]] + [item["center"] for item in row["neighbors"]]
    assert all(math.dist(row["crop_center"], point) > 5 for point in points)
    assert math.dist(row["crop_center"], row["center"]) == pytest.approx(6)
    image, positive = render_original(case)
    try:
        assert row["original_raster_sha256"] == positive["original_raster_sha256"]
        assert hashlib.sha256(image.tobytes()).hexdigest() == row["original_raster_sha256"]
        assert row["crop_radius"] == positive["crop_radius"]
        assert row["authored_target_shape"] == shape
    finally:
        image.close()


def test_invalid_recipe_cannot_create_a_negative():
    with pytest.raises(ValueError, match="recipe"):
        prepare_case(CrowdedContextCase("circle", "filled", "private", "target_last", 0))
