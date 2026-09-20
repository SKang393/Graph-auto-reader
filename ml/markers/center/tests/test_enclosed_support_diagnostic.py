# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import numpy as np
import pytest
from PIL import Image, ImageDraw

from ml.markers.center.enclosed_support_diagnostic import enclosed_center_support


@pytest.mark.parametrize('shape', ['ellipse', 'rectangle', 'diamond'])
@pytest.mark.parametrize('offset', [0, .4, -.4])
def test_one_pixel_closed_outline_supports_its_measured_interior(shape, offset):
    image = Image.new('L', (40, 40), 0)
    draw = ImageDraw.Draw(image)
    if shape == 'diamond':
        draw.polygon([(20, 15), (25, 20), (20, 25), (15, 20)], outline=255)
    else:
        getattr(draw, shape)((15, 15, 25, 25), outline=255, width=1)
    assert enclosed_center_support(np.array(image, dtype=np.float32) / 255, 20 + offset, 20 - offset)


@pytest.mark.parametrize('kind', ['empty', 'horizontal', 'diagonal', 'cross', 'broken_box', 'large_box'])
def test_open_strokes_or_nonlocal_enclosures_are_not_support(kind):
    image = Image.new('L', (64, 64), 0)
    draw = ImageDraw.Draw(image)
    if kind in ('horizontal', 'cross'):
        draw.line([(5, 32), (59, 32)], fill=255)
    if kind == 'cross':
        draw.line([(32, 5), (32, 59)], fill=255)
    if kind == 'diagonal':
        draw.line([(5, 5), (59, 59)], fill=255)
    if kind == 'broken_box':
        draw.rectangle((27, 27, 37, 37), outline=255)
        draw.point((32, 27), fill=0)
    if kind == 'large_box':
        draw.rectangle((5, 5, 59, 59), outline=255)
    assert not enclosed_center_support(np.array(image, dtype=np.float32) / 255, 32, 32)


def test_image_edge_does_not_close_an_open_shape():
    ink = np.zeros((8, 8), dtype=np.float32)
    ink[0, :], ink[7, :], ink[:, 7] = 1, 1, 1
    assert not enclosed_center_support(ink, 3, 3)
    assert not enclosed_center_support(ink, -1, 3)


@pytest.mark.parametrize('bad', [np.nan, np.inf, -0.1, 1.1])
def test_invalid_raster_fails_explicitly(bad):
    ink = np.zeros((8, 8), dtype=np.float32)
    ink[2, 2] = bad
    with pytest.raises(ValueError):
        enclosed_center_support(ink, 3, 3)


def test_coordinates_are_not_moved_to_a_neighboring_hole():
    ink = np.zeros((40, 40), dtype=np.float32)
    ink[15:22, 15], ink[15:22, 21] = 1, 1
    ink[15, 15:22], ink[21, 15:22] = 1, 1
    assert enclosed_center_support(ink, 18, 18)
    assert not enclosed_center_support(ink, 22, 18)
