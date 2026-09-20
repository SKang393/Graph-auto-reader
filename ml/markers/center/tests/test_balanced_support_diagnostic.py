# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import numpy as np
import pytest

from ml.markers.center.balanced_support_diagnostic import balanced_center_support


@pytest.mark.parametrize("side", range(4))
def test_nearby_wall_does_not_locate_a_marker_center(side):
    ink = np.zeros((32, 32), dtype=np.float32)
    if side < 2:
        ink[:, 11 if side == 0 else 21] = 1
    else:
        ink[11 if side == 2 else 21, :] = 1
    assert not balanced_center_support(ink, 16, 16)


@pytest.mark.parametrize("kind", ("filled", "open", "cross", "thin_circle"))
def test_centered_marker_evidence_survives(kind):
    ink = np.zeros((32, 32), dtype=np.float32)
    if kind == "filled":
        ink[13:20, 13:20] = 1
    elif kind == "open":
        ink[11:22, 11] = ink[11:22, 21] = 1
        ink[11, 11:22] = ink[21, 11:22] = 1
    elif kind == "cross":
        ink[11:22, 16] = ink[16, 11:22] = 1
    else:
        outline = ("......####......", ".....#....#.....", "....#......#....",
                   "...#........#...", "...#........#...", "...#........#...",
                   "...#........#...", "....#......#....", ".....#....#.....", "......####......")
        for y, row in enumerate(outline, 11):
            for x, pixel in enumerate(row, 8):
                if pixel == "#":
                    ink[y, x] = 1
    assert balanced_center_support(ink, 16, 16)


def test_invalid_pixels_and_coordinates_do_not_become_support():
    ink = np.zeros((32, 32), dtype=np.float32)
    assert not balanced_center_support(ink, -1, 16)
    with pytest.raises(ValueError):
        balanced_center_support(ink, float("nan"), 16)
    ink[16, 16] = float("nan")
    with pytest.raises(ValueError):
        balanced_center_support(ink, 16, 16)
