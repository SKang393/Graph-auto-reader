# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from copy import deepcopy

import numpy as np
import pytest

from ml.synthetic.contextual_negatives import VERSION, render_contextual_scene
from ml.synthetic.renderer import render_scene
from ml.synthetic.templates import build_scene


def _scene():
    return build_scene("ab", 1047, "vector_clean", panel_count=1, session_count=8)


def test_profile_preserves_source_and_all_scientific_and_text_annotations():
    scene = _scene()
    original = deepcopy(scene)
    old_image, old_annotation, old_mask = render_scene(scene)
    image, annotation, mask = render_contextual_scene(scene, split="train")
    assert scene == original
    assert old_annotation["panels"] == annotation["panels"]
    assert np.array_equal(old_mask, mask)
    assert not np.array_equal(old_image, image)
    assert annotation["contextual_negative_profile"]["replaced_orphan_requests"] == scene["hard_negatives"]
    assert annotation["contextual_negative_profile"]["id"] == VERSION
    repeat_image, repeat_annotation, repeat_mask = render_contextual_scene(scene, split="train")
    assert repeat_annotation == annotation
    assert np.array_equal(repeat_image, image) and np.array_equal(repeat_mask, mask)
    historical_image, historical_annotation, _ = render_scene(scene)
    assert historical_annotation == old_annotation and np.array_equal(historical_image, old_image)


def test_pixels_equal_native_graph_and_each_negative_has_visible_context():
    scene = _scene()
    native = deepcopy(scene)
    native["hard_negatives"] = []
    expected, _, _ = render_scene(native)
    image, annotation, mask = render_contextual_scene(scene, split="dev")
    assert np.array_equal(image, expected)
    assert annotation["hard_negatives"]
    assert {r["kind"] for r in annotation["hard_negatives"]} >= {"text", "legend_symbol", "tick_mark", "bracket", "phase_divider"}
    for record in annotation["hard_negatives"]:
        assert record["context"]["category"] in {"texts", "legends", "ticks", "arrows", "brackets", "dividers"}
        x, y, width, height = record["geometry"]["box"]
        pixels = np.asarray(mask)[int(y):int(np.ceil(y + height)), int(x):int(np.ceil(x + width))]
        assert not pixels.any()
        assert record["source_reference"]


def test_arrow_pointing_at_true_marker_is_not_a_negative():
    scene = _scene()
    panel = scene["panels"][0]
    arrow = panel["arrows"][0]
    arrow["tip"] = panel["points"][0]["center"][:]
    arrow["start"] = [arrow["tip"][0] - 30, arrow["tip"][1] - 10]
    _, annotation, _ = render_contextual_scene(scene, split="dev")
    assert not any(n["kind"] == "arrowhead" and n["source_reference"] == arrow["arrow_id"] for n in annotation["hard_negatives"])
    assert any(n["kind"] == "arrowhead" and n["reason"] == "context_overlaps_true_marker_clearance"
               for n in annotation["contextual_negative_profile"]["omitted_native_negative_candidates"])
    assert len(annotation["panels"][0]["markers"]) == len(panel["points"])


def test_hidden_legend_and_ticks_are_not_sampled():
    scene = _scene()
    panel = scene["panels"][0]
    panel["legend"]["visible"] = False
    for tick in panel["ticks"]:
        tick["hidden"] = True
    _, annotation, _ = render_contextual_scene(scene, split="dev")
    assert not any(n["kind"] in {"legend_symbol", "tick_mark"} for n in annotation["hard_negatives"])


@pytest.mark.parametrize("split", ["sealed", "test", "real-dev", "real-sealed", "validation"])
def test_other_splits_are_rejected(split):
    with pytest.raises(ValueError, match="restricted to train/dev"):
        render_contextual_scene(_scene(), split=split)


def test_private_scene_is_rejected():
    scene = _scene()
    scene["provenance"]["private_data"] = True
    with pytest.raises(ValueError, match="private_data|project-owned procedural"):
        render_contextual_scene(scene, split="train")


def test_transformed_annotations_use_rendered_coordinates():
    scene = build_scene("ab", 1047, "hand_drawn", panel_count=1, session_count=8)
    _, original, mask = render_scene(scene)
    image, annotation, corrected_mask = render_contextual_scene(scene, split="dev")
    assert original["panels"] == annotation["panels"]
    assert np.array_equal(mask, corrected_mask)
    assert all(0 <= r["geometry"]["center"][0] < image.width and 0 <= r["geometry"]["center"][1] < image.height
               for r in annotation["hard_negatives"])


def test_visible_content_profile_retains_existing_morphology_and_points():
    from ml.synthetic.runtime_graph_visible_content_v3 import render_visible_content_source
    scene = build_scene("ab", 1047, "print_monochrome", panel_count=1, session_count=8)
    original = render_visible_content_source(scene)
    _, annotation, mask = render_contextual_scene(scene, split="dev", source_profile="visible-content-v3")
    assert annotation["panels"] == original.annotation["panels"]
    assert np.array_equal(mask, original.marker_mask)
    assert annotation["contextual_negative_profile"]["source_profile"] == "visible-content-v3"


def test_unknown_profile_is_rejected():
    with pytest.raises(ValueError, match="Unknown contextual negative"):
        render_contextual_scene(_scene(), split="train", source_profile="unknown")
