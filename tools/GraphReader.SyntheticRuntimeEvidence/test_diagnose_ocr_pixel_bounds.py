# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import numpy as np
import pytest

from diagnose_ocr_pixel_bounds import foreground_threshold, tighten_box


def test_trims_empty_margins_and_preserves_all_foreground():
    pixels = np.full((12, 16), 255, dtype=np.uint8)
    pixels[4:7, 5:9] = 0
    assert tighten_box(pixels, (1.0, 1.0, 14.0, 11.0), 180) == (5.0, 4.0, 9.0, 7.0)
    pixels[2, 12] = 128  # A stray dark mark must not be silently filtered out.
    assert tighten_box(pixels, (1.0, 1.0, 14.0, 11.0), 180) == (5.0, 2.0, 13.0, 7.0)


def test_blank_proposal_keeps_its_geometry_and_denominator():
    pixels = np.full((6, 7), 255, dtype=np.uint8)
    box = (0.25, 1.5, 6.2, 5.25)
    assert tighten_box(pixels, box, 180) == box


def test_foreground_outside_box_does_not_change_the_result():
    pixels = np.full((8, 9), 255, dtype=np.uint8)
    pixels[0, 0] = 0
    pixels[4, 4] = 0
    assert tighten_box(pixels, (2.0, 2.0, 7.0, 7.0), 180) == (4.0, 4.0, 5.0, 5.0)


def test_subpixel_edges_and_bottom_right_ink_do_not_expand_or_disappear():
    pixels = np.zeros((4, 5), dtype=np.uint8)
    box = (0.25, 0.5, 4.75, 3.75)
    assert tighten_box(pixels, box, 180) == box
    pixels.fill(255)
    pixels[3, 4] = 0
    assert tighten_box(pixels, box, 180) == (4.0, 3.0, 4.75, 3.75)


def test_threshold_uses_existing_full_panel_mean_and_lower_clamp():
    pixels = np.full((4, 5), 200, dtype=np.uint8)
    assert foreground_threshold(pixels) == 160
    pixels.fill(0)
    assert foreground_threshold(pixels) == 32
    pixels.fill(255)
    assert foreground_threshold(pixels) == 204


@pytest.mark.parametrize("box", [(0, 0, 0, 1), (-1, 0, 1, 1), (0, 0, 6, 4), (0, 0, float('nan'), 1)])
def test_rejects_invalid_geometry(box):
    with pytest.raises(ValueError):
        tighten_box(np.zeros((4, 5), dtype=np.uint8), box, 180)


@pytest.mark.parametrize("pixels", [np.zeros((4, 5, 3), dtype=np.uint8), np.zeros((4, 5)), np.zeros((0, 5), dtype=np.uint8)])
def test_rejects_non_gray8_inputs(pixels):
    with pytest.raises(ValueError):
        foreground_threshold(pixels)


def test_pixel_at_threshold_is_retained_and_input_is_immutable():
    pixels = np.full((6, 7), 255, dtype=np.uint8)
    pixels[2, 3] = 180
    pixels.setflags(write=False)
    before = pixels.tobytes()
    assert tighten_box(pixels, (0, 0, 7, 6), 180) == (3.0, 2.0, 4.0, 3.0)
    assert pixels.tobytes() == before
