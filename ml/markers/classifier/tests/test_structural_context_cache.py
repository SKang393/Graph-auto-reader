# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import numpy as np
import pytest

from ml.markers.classifier import structural_context_cache as cache


def annotation():
    return {"panels": [], "axes": [], "ticks": [], "top_bars": [], "arrows": [], "brackets": [],
            "markers": [{"box": [8, 48, 4, 4]}, {"box": [188, 78, 4, 4]}],
            "edges": [{"line": [[10, 50], [190, 80]], "drawn": True}],
            "dividers": [{"line": [[100, 0], [100, 200]], "drawn": True}],
            "legends": [{"box": [200, 20, 100, 40], "visible": True,
                         "entries": [{"glyph_box": [210, 32, 12, 12]}]}]}


def test_oblique_intersections_are_authored_even_between_sampled_points():
    values = cache.structural_anchors(annotation())
    assert ("intersection", (100., 65.)) in values
    assert {kind for kind, _ in values} == {"connector", "divider", "legend_frame", "intersection"}
    assert cache.segment_intersection([[0, 0], [10, 0]], [[0, 1], [10, 1]]) is None
    assert cache.segment_intersection([[0, 0], [1, 1]], [[2, 0], [2, 3]]) is None


def test_true_marker_and_legend_glyph_pixels_are_never_negative_crop_context():
    data = annotation()
    boxes = cache.protected_boxes(data)
    assert not cache.clear_of_markers((10, 50), 2.5, boxes)
    assert not cache.clear_of_markers((216, 38), 2.5, boxes)
    first = cache.select_anchors(data, "a"*64)
    assert first == cache.select_anchors(data, "a"*64)
    assert any(kind == "intersection" for kind, _, _ in first)
    for _, _, center in first:
        for radius in cache.RADII:
            for dx, dy in cache.SHIFTS:
                assert cache.clear_of_markers((center[0]+dx, center[1]+dy), radius, boxes)


def test_absent_or_hidden_structure_does_not_create_training_examples():
    data = annotation()
    data["edges"][0]["drawn"] = False
    data["legends"][0]["visible"] = False
    assert {kind for kind, _ in cache.structural_anchors(data)} == {"divider"}


@pytest.mark.parametrize("line", [[[0, 0], [float("nan"), 2]], [0, 1, 2, 3]])
def test_malformed_structure_is_rejected(line):
    data = annotation()
    data["edges"][0]["line"] = line
    with pytest.raises(ValueError, match="finite original-pixel"):
        cache.structural_anchors(data)


def test_bounded_population_keeps_rare_intersections_and_full_panel_inventory():
    data = annotation()
    data["dividers"][0]["line"] = [[100, 0], [100, 10000]]
    data["panels"] = [{"ticks": [{"line": [[150, 400], [160, 400]], "visible": True}]}]
    selected = cache.select_anchors(data, "b"*64)
    assert sum(kind == "divider" for kind, _, _ in selected) == cache.MAX_ANCHORS_PER_KIND
    assert sum(kind == "intersection" for kind, _, _ in selected) == 1
    assert any(kind == "tick" for kind, _, _ in selected)
    assert len({identity for _, identity, _ in selected}) == len(selected)


def test_crops_reuse_the_shipped_native_bilinear_preprocessing():
    pixels = np.ones((96, 96), np.float32)
    pixels[20:80, 47] = 0
    pixels[51, 10:85] = 0
    patch = cache.extract_original_patch(pixels, (47.375, 50.375), 4.)
    assert patch.shape == (1, 32, 32) and patch.dtype == np.float32
    assert np.isfinite(patch).all() and 0 <= patch.min() < patch.max() <= 1
