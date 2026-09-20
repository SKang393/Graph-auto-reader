# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from copy import deepcopy

import numpy as np
import pytest

from ml.synthetic.annotation_clearance import _ltrb, _overlap
from ml.synthetic.legend_clearance import separate_legends
from ml.synthetic.renderer import render_scene
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
