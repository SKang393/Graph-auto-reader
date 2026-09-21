# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from copy import deepcopy

import numpy as np
import pytest

from ml.synthetic.annotation_clearance import _ltrb, _overlap
from ml.synthetic.legend_clearance import separate_legends
from ml.synthetic.renderer import render_scene
from ml.synthetic.renderer import _font_settings
from ml.synthetic.fonts import FontResolver
from ml.synthetic.templates import build_scene


def _scene():
    return build_scene("ab", 1047, "vector_clean", panel_count=1, session_count=8)


def test_legend_moves_as_a_unit_without_altering_scientific_content():
    scene = _scene()
    before = deepcopy(scene)
    result = separate_legends(scene)
    assert scene == before
    assert len(result.changes) == 1 and not result.unresolved
    assert result == separate_legends(scene)
    assert separate_legends(result.scene).scene == result.scene
    old, new = scene["panels"][0], result.scene["panels"][0]
    assert {k: v for k, v in old.items() if k != "legend"} == {k: v for k, v in new.items() if k != "legend"}
    dx, dy = result.changes[0]["translation"]
    for a, b in zip(old["legend"]["entries"], new["legend"]["entries"]):
        assert a["text"] == b["text"] and a["series_id"] == b["series_id"]
        for field in ("glyph_box", "text_box"):
            assert b[field] == [a[field][0] + dx, a[field][1] + dy, *a[field][2:]]
    old_regions = {r["region_id"]: r for r in scene["annotations"]["text_regions"]}
    for region in result.scene["annotations"]["text_regions"]:
        if region["region_id"] in old_regions:
            assert region == old_regions[region["region_id"]]
    old_image, _, old_mask = render_scene(scene)
    new_image, annotation, new_mask = render_scene(result.scene)
    assert np.array_equal(old_mask, new_mask)
    assert not np.array_equal(old_image, new_image)
    rendered = {r["region_id"]: r for p in annotation["panels"] for r in p["texts"]}
    for entry in new["legend"]["entries"]:
        region = next(r for r in result.scene["annotations"]["text_regions"] if r["box"] == entry["text_box"])
        assert rendered[region["region_id"]]["text"] == entry["text"]
    artifacts = result.scene["annotations"]["artifacts"]
    assert new["legend"]["box"] in [a["geometry"]["coordinates"] for a in artifacts if a["kind"] == "legend"]
    assert all(e["text_box"] in [a["geometry"]["coordinates"] for a in artifacts if a["role"] == "legend_text"] for e in new["legend"]["entries"])


def test_hidden_legend_is_unchanged_and_ambiguous_binding_is_reported():
    scene = _scene()
    scene["panels"][0]["legend"]["visible"] = False
    result = separate_legends(scene)
    assert result.scene == scene and not result.changes and not result.unresolved
    scene["panels"][0]["legend"]["visible"] = True
    entry = scene["panels"][0]["legend"]["entries"][0]
    entry["text"] += " unbound"
    result = separate_legends(scene)
    assert result.scene == scene and not result.changes
    assert result.unresolved[0]["reason"] == "legend_text_binding_not_unique"


def test_no_space_retains_every_label_and_point():
    scene = _scene()
    scene["panels"][0]["legend"]["box"][2:] = [1100.0, 200.0]
    result = separate_legends(scene)
    assert result.scene == scene and not result.changes
    assert result.unresolved[0]["reason"] == "no_clear_legend_area"


def test_private_input_is_rejected():
    scene = _scene()
    scene["provenance"]["private_data"] = True
    with pytest.raises(ValueError):
        separate_legends(scene)


def test_measured_label_extents_can_differ_from_legend_entry_dimensions():
    scene = _scene()
    entry = scene["panels"][0]["legend"]["entries"][0]
    region = next(r for r in scene["annotations"]["text_regions"] if r["role"] == "legend_text")
    original = region["box"][:]
    region["box"][2:] = [121.0, 11.0]
    for a in scene["annotations"]["artifacts"]:
        if a["role"] == "legend_text" and a["geometry"]["coordinates"] == original:
            a["geometry"]["coordinates"] = region["box"][:]
    result = separate_legends(scene)
    assert len(result.changes) == 1 and not result.unresolved
    moved = next(r for r in result.scene["annotations"]["text_regions"] if r["role"] == "legend_text")
    new_entry = result.scene["panels"][0]["legend"]["entries"][0]
    assert moved["box"][2:] == [121.0, 11.0]
    assert new_entry["text_box"][2:] == entry["text_box"][2:]
    assert moved["box"][:2] == new_entry["text_box"][:2]


def _translate_legend(scene, dx, dy):
    panel = scene["panels"][0]
    legend = panel["legend"]
    legend["position"] = "outside"
    boxes = [legend["box"]] + [e[field] for e in legend["entries"] for field in ("text_box", "glyph_box")]
    boxes += [r["box"] for r in scene["annotations"]["text_regions"] if r["role"] == "legend_text"]
    boxes += [a["geometry"]["coordinates"] for a in scene["annotations"]["artifacts"] if
              a["kind"] == "legend" or a["role"] == "legend_text"]
    for box in boxes:
        box[0] += dx
        box[1] += dy


@pytest.mark.parametrize("left", [-220.0, 1180.0, 1400.0])
def test_clipped_or_fully_outside_legend_is_relocated_without_losing_letters(left):
    scene = _scene()
    _translate_legend(scene, left - scene["panels"][0]["legend"]["box"][0], 0)
    before = deepcopy(scene)
    result = separate_legends(scene)
    assert scene == before and len(result.changes) == 1 and not result.unresolved
    assert result.changes[0]["canvas_clipped_before"]
    old, new = scene["panels"][0], result.scene["panels"][0]
    assert {k: v for k, v in old.items() if k != "legend"} == {k: v for k, v in new.items() if k != "legend"}
    frame = _ltrb(new["legend"]["box"])
    image, _, mask = render_scene(result.scene)
    assert 0 <= frame[0] < frame[2] <= image.width and 0 <= frame[1] < frame[3] <= image.height
    assert not _overlap(frame, _ltrb(new["plot_box"]), 4)
    _, _, original_mask = render_scene(scene)
    assert np.array_equal(original_mask, mask)
    assert [e["text"] for e in new["legend"]["entries"]] == [e["text"] for e in old["legend"]["entries"]]
    assert separate_legends(result.scene).scene == result.scene


def test_frame_contains_measured_font_extents_even_when_template_width_is_too_small():
    scene = _scene()
    legend = scene["panels"][0]["legend"]
    region = next(r for r in scene["annotations"]["text_regions"] if r["role"] == "legend_text")
    text = "A much longer outcome label"
    region["text"] = legend["entries"][0]["text"] = text
    result = separate_legends(scene)
    assert result.changes and not result.unresolved and result.changes[0]["frame_resized"]
    new = result.scene["panels"][0]["legend"]
    label = next(r for r in result.scene["annotations"]["text_regions"] if r["role"] == "legend_text")
    name, size, paths = _font_settings(result.scene)
    font = FontResolver(paths).resolve(name, size).load()
    a, b, c, d = font.getbbox(text, anchor="lt")
    x, y = label["box"][:2]
    left, top, right, bottom = _ltrb(new["box"])
    assert left + 4 <= x + a < x + c <= right - 4
    assert top + 4 <= y + b < y + d <= bottom - 4
    assert label["text"] == text and label["box"][2:] == region["box"][2:]
    assert separate_legends(result.scene).scene == result.scene
