# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from __future__ import annotations

import numpy as np
import pytest
from PIL import Image

from ml.markers.classifier.runtime_patches import extract_original_patch, original_luminance


def test_integer_color_conversion_and_transparency_match_white_paper() -> None:
    image = Image.fromarray(np.array([[[255, 0, 0, 255], [0, 255, 0, 255],
                                      [0, 0, 255, 255], [0, 0, 0, 0], [0, 0, 0, 128]]], dtype=np.uint8))
    before = image.tobytes()
    expected = np.array([[76, 150, 29, 255, 127]], dtype=np.float32) / np.float32(255)
    np.testing.assert_array_equal(original_luminance(image), expected)
    assert image.tobytes() == before


def test_analytic_gradient_retains_original_pixel_sampling_positions() -> None:
    y, x = np.mgrid[:9, :11]
    pixels = ((x + 2 * y) / 100).astype(np.float32)
    before = pixels.copy()
    actual = extract_original_patch(pixels, (5, 4), 1, width=2, height=2)
    np.testing.assert_allclose(actual, [[[0.93, 0.89], [0.85, 0.81]]], rtol=0, atol=1e-7)
    assert actual.dtype == np.float32
    np.testing.assert_array_equal(pixels, before)


def test_large_marker_uses_radius_scaled_extent() -> None:
    pixels = np.broadcast_to(np.arange(41, dtype=np.float32) / 40, (41, 41))
    actual = extract_original_patch(pixels, (20, 20), 8, width=2, height=1)
    # Half extent is 18, so the two samples are original x=11 and x=29.
    np.testing.assert_allclose(actual, [[[0.725, 0.275]]], rtol=0, atol=1e-7)


def test_border_and_far_outside_samples_use_ink_padding() -> None:
    pixels = np.full((9, 9), 0.25, dtype=np.float32)
    actual = extract_original_patch(pixels, (0, 0), 1, width=2, height=2, padding_ink=0.4)
    np.testing.assert_array_equal(actual, np.array([[[0.4, 0.4], [0.4, 0.75]]], dtype=np.float32))
    outside = extract_original_patch(pixels, (1e30, -1e30), 1, padding_ink=0.2)
    assert np.all(outside == np.float32(0.2))


@pytest.mark.parametrize("radius", [0, -1, float("nan"), float("inf")])
def test_invalid_radius_is_rejected(radius: float) -> None:
    with pytest.raises(ValueError, match="Radius"):
        extract_original_patch(np.ones((8, 8), dtype=np.float32), (4, 4), radius)


@pytest.mark.parametrize("pixels", [np.array([]), np.ones((2, 2, 2)),
                                   np.array([[float("nan")]]), np.array([[1.1]]), np.array([[-0.1]])])
def test_invalid_raster_is_rejected(pixels: np.ndarray) -> None:
    with pytest.raises(ValueError, match="raster"):
        extract_original_patch(pixels, (0, 0), 1)


@pytest.mark.parametrize("options", [{"width": 0}, {"height": 1.5}, {"width": True},
                                    {"radius_scale": float("inf")}, {"minimum_half_extent": 0},
                                    {"padding_ink": 1.1}, {"padding_ink": float("nan")}])
def test_invalid_sampling_options_are_rejected(options: dict) -> None:
    with pytest.raises(ValueError):
        extract_original_patch(np.ones((8, 8), dtype=np.float32), (4, 4), 1, **options)


def test_nonfinite_center_and_overflowing_extent_are_rejected() -> None:
    pixels = np.ones((8, 8), dtype=np.float32)
    with pytest.raises(ValueError, match="Center"):
        extract_original_patch(pixels, (float("inf"), 0), 1)
    with pytest.raises(ValueError, match="extent"):
        extract_original_patch(pixels, (0, 0), 1e308)
