# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import math

import numpy as np
import pytest

from ml.ocr.official_bakeoff.db_inverse_targets import (
    InverseTargetError,
    build_inverse_targets,
)


def test_continuous_target_inverts_fixed_unclip() -> None:
    result = build_inverse_targets(256, 128, [(40.0, 30.0, 136.0, 42.0)])
    region = result.regions[0]
    left, top, right, bottom = region.continuous_contour_box
    width = right - left
    height = bottom - top
    unclip = 1.5 * width * height / (2.0 * (width + height))

    assert unclip == pytest.approx(region.inverse_offset)
    assert width + (2.0 * unclip) == pytest.approx(96.0)
    assert height + (2.0 * unclip) == pytest.approx(12.0)


def test_inclusive_raster_endpoints_preserve_declared_contour_extent() -> None:
    result = build_inverse_targets(256, 128, [(40.0, 30.0, 136.0, 42.0)])
    region = result.regions[0]
    left, top, right, bottom = region.raster_contour_box
    extent_x, extent_y = region.raster_extent

    assert (right - left, bottom - top) == (extent_x, extent_y)
    assert result.target.shape == (1, 128, 256)
    assert int(result.target.sum()) == (extent_x + 1) * (extent_y + 1)
    assert np.all(result.target[0, top : bottom + 1, left : right + 1] == 1)
    assert not result.target.flags.writeable


def test_tiny_boundary_truth_is_retained_with_minimum_extent_and_translation() -> None:
    result = build_inverse_targets(12, 10, [(0.0, 0.0, 1.0, 2.0)])
    region = result.regions[0]

    assert len(result.regions) == 1
    assert region.minimum_extent_clamped_x
    assert region.minimum_extent_clamped_y
    assert region.translated_to_fit_x
    assert region.raster_contour_box == (0, 0, 3, 3)
    assert result.target.sum() == 16


def test_overlap_and_eight_connected_touch_are_reported_without_dropping_truths() -> None:
    result = build_inverse_targets(
        64,
        32,
        [
            (2.0, 2.0, 10.0, 10.0),
            (4.0, 4.0, 12.0, 12.0),
            (10.5, 2.0, 18.5, 10.0),
            (40.0, 20.0, 48.0, 28.0),
        ],
    )

    assert len(result.regions) == 4
    assert [(item.first_index, item.second_index, item.kind) for item in result.contacts] == [
        (0, 1, "overlap"),
        (1, 2, "touch"),
    ]


@pytest.mark.parametrize(
    ("width", "height", "boxes"),
    [
        (3, 8, [(0.0, 0.0, 1.0, 1.0)]),
        (8, 3, [(0.0, 0.0, 1.0, 1.0)]),
        (8, 8, [(0.0, 0.0, 0.0, 1.0)]),
        (8, 8, [(-1.0, 0.0, 1.0, 1.0)]),
        (8, 8, [(0.0, 0.0, math.inf, 1.0)]),
    ],
)
def test_invalid_or_unrepresentable_input_fails_closed(
    width: int, height: int, boxes: list[tuple[float, float, float, float]]
) -> None:
    with pytest.raises(InverseTargetError):
        build_inverse_targets(width, height, boxes)
