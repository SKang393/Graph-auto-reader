# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from collections import Counter
from copy import deepcopy

import numpy as np
import pytest

from ml.synthetic.renderer import render_scene
from ml.synthetic.templates import build_scene
from ml.synthetic.text_layout_clearance import separate_peripheral_labels, VERSION


def _scene():
    return build_scene("ab", 1047, "vector_clean", panel_count=1, session_count=8)


def _move_region(scene, region, position):
    old = region["box"][:]
    region["box"][:2] = position
    artifact = next(a for a in scene["annotations"]["artifacts"] if
                    a["kind"] == "text" and a["role"] == region["role"] and
                    a["geometry"]["coordinates"] == old)
    artifact["geometry"]["coordinates"][:2] = position


def _assert_clear(scene, changes):
    # Check the final image with each moved label hidden. Other moved labels
    # remain obstacles, so a later move cannot silently invalidate an earlier one.
    for change in changes:
        background = deepcopy(scene)
        background["degradations"] = []
        target = next(r for r in background["annotations"]["text_regions"]
                      if r["region_id"] == change["region_id"])
        _, annotation, _ = render_scene(background)
        text = next(t for p in annotation["panels"] for t in p["texts"]
                    if t["region_id"] == change["region_id"])
        x, y, w, h = text["rendered_pixel_box"]
        target["visible"] = False
        image, _, _ = render_scene(background)
        assert np.all(np.asarray(image)[max(0, int(y)-4):int(np.ceil(y+h))+4,
                                      max(0, int(x)-4):int(np.ceil(x+w))+4] == 255)


def test_overlapping_participant_moves_without_altering_scientific_content():
    scene = _scene()
    before = deepcopy(scene)
    result = separate_peripheral_labels(scene)
    assert scene == before and result.version == VERSION
    assert result.changes and not result.unresolved
    assert any(c["role"] == "participant" for c in result.changes)
    assert result == separate_peripheral_labels(scene)
    assert separate_peripheral_labels(result.scene).scene == result.scene
    assert result.scene["panels"] == scene["panels"]
    old_image, _, old_mask = render_scene(scene)
    new_image, _, new_mask = render_scene(result.scene)
    assert np.array_equal(old_mask, new_mask)
    assert not np.array_equal(old_image, new_image)
    assert Counter((r["text"], r["role"], r["visible"]) for r in scene["annotations"]["text_regions"]) == Counter(
        (r["text"], r["role"], r["visible"]) for r in result.scene["annotations"]["text_regions"])
    allowed = {c["old_region_id"] for c in result.changes}
    assert [r for r in scene["annotations"]["text_regions"] if r["region_id"] not in allowed] == [
        r for r in result.scene["annotations"]["text_regions"] if r["region_id"] not in {c["region_id"] for c in result.changes}]
    _assert_clear(result.scene, result.changes)


def test_condition_caption_and_axis_title_remain_in_their_semantic_areas():
    scene = _scene()
    regions = scene["annotations"]["text_regions"]
    heading = next(r for r in regions if r["role"] == "phase_heading")
    condition = next(r for r in regions if r["role"] == "condition_label")
    _move_region(scene, condition, heading["box"][:2])
    title = next(r for r in regions if r["role"] == "axis_title" and r["text"] == "Outcome")
    tick = next(r for r in regions if r["role"] == "y_tick" and r["text"] == "5")
    _move_region(scene, title, [23.0, tick["box"][1]])
    result = separate_peripheral_labels(scene)
    phase = scene["panels"][0]["phases"][0]
    captions = [c for c in result.changes if c["old_region_id"] == condition["region_id"]]
    assert len(captions) == 1 and captions[0]["area"] == "same_phase_header"
    assert phase["screen_x_min"] <= captions[0]["box"][0] < phase["screen_x_max"]
    titles = [c for c in result.changes if c["old_region_id"] == title["region_id"]]
    assert len(titles) == 1 and titles[0]["area"] == "y_axis_margin"
    assert titles[0]["box"][0] < scene["panels"][0]["plot_box"][0]
    assert not result.unresolved
    _assert_clear(result.scene, result.changes)


def test_no_space_and_ambiguous_artifact_retain_the_original_labels():
    scene = _scene()
    participant = next(r for r in scene["annotations"]["text_regions"] if r["role"] == "participant")
    participant["text"] = "W" * 200
    result = separate_peripheral_labels(scene)
    assert participant in result.scene["annotations"]["text_regions"]
    assert any(u == {"region_id": participant["region_id"], "reason": "no_clear_semantic_label_area"} for u in result.unresolved)
    scene = _scene()
    participant = next(r for r in scene["annotations"]["text_regions"] if r["role"] == "participant")
    scene["annotations"]["artifacts"] = [a for a in scene["annotations"]["artifacts"] if a["role"] != "participant"]
    result = separate_peripheral_labels(scene)
    assert participant in result.scene["annotations"]["text_regions"]
    assert any(u["reason"] == "text_artifact_binding_not_unique" for u in result.unresolved)


def test_hidden_and_private_labels_are_not_processed():
    scene = _scene()
    for r in scene["annotations"]["text_regions"]:
        if r["role"] in ("participant", "axis_title", "condition_label"):
            r["visible"] = False
    result = separate_peripheral_labels(scene)
    assert result.scene == scene and not result.changes and not result.unresolved
    scene["provenance"]["private_data"] = True
    with pytest.raises(ValueError, match="private_data|procedural"):
        separate_peripheral_labels(scene)
