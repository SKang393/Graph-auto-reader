# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from copy import deepcopy

import numpy as np

from ml.synthetic.annotation_clearance import separate_arrow_labels, _line_hits, _ltrb, _overlap
from ml.synthetic.renderer import render_scene
from ml.synthetic.templates import build_scene


def _scene():
    scene = build_scene("ab", 1047, "vector_clean", panel_count=1, session_count=8)
    panel = scene["panels"][0]
    note = next(r for r in scene["annotations"]["text_regions"] if r["role"] == "annotation" and r["text"] == panel["arrows"][0]["label"])
    old = note["box"][:]
    legend = panel["legend"]["box"]
    note["box"][:2] = [legend[0] + 35, legend[1] + 9]
    for artifact in scene["annotations"]["artifacts"]:
        if artifact["kind"] == "text" and artifact["geometry"]["coordinates"] == old:
            artifact["geometry"]["coordinates"] = note["box"][:]
    panel["arrows"][0]["tip"] = [legend[0] - 90, legend[1] + legend[3] + 50]
    return scene


def test_annotation_moves_without_changing_values_markers_or_other_labels():
    scene = _scene()
    before = deepcopy(scene)
    result = separate_arrow_labels(scene)
    assert scene == before
    assert len(result.changes) == 1 and not result.unresolved
    assert result == separate_arrow_labels(scene)
    assert separate_arrow_labels(result.scene).scene == result.scene
    for old, new in zip(scene["panels"], result.scene["panels"]):
        assert {k: v for k, v in old.items() if k != "arrows"} == {k: v for k, v in new.items() if k != "arrows"}
        assert old["arrows"][0]["tip"] == new["arrows"][0]["tip"]
    old_text = {r["region_id"]: r for r in scene["annotations"]["text_regions"]}
    for region in result.scene["annotations"]["text_regions"]:
        if region["region_id"] in old_text:
            assert region == old_text[region["region_id"]]
    old_image, _, old_mask = render_scene(scene)
    new_image, annotation, new_mask = render_scene(result.scene)
    assert np.array_equal(old_mask, new_mask)
    assert not np.array_equal(old_image, new_image)
    text = next(t for p in annotation["panels"] for t in p["texts"] if t["region_id"] == result.changes[0]["region_id"])
    assert not _overlap(_ltrb(text["rendered_pixel_box"]), _ltrb(scene["panels"][0]["legend"]["box"]), 4)
    boxes = [a["geometry"]["coordinates"] for a in result.scene["annotations"]["artifacts"] if a["kind"] == "text"]
    assert result.changes[0]["box"] in boxes


def test_hidden_labels_and_frames_are_preserved():
    scene = _scene()
    scene["panels"][0]["legend"]["visible"] = False
    result = separate_arrow_labels(scene)
    assert result.scene == scene and not result.changes and not result.unresolved


def test_target_inside_legend_is_reported_without_dropping_or_moving_label():
    scene = _scene()
    box = scene["panels"][0]["legend"]["box"]
    scene["panels"][0]["arrows"][0]["tip"] = [box[0] + 10, box[1] + 10]
    result = separate_arrow_labels(scene)
    assert result.scene == scene and not result.changes
    assert result.unresolved[0]["reason"] == "arrow_target_inside_legend"


def test_line_obstacles_cover_crossing_parallel_tangent_and_point_cases():
    box = (2, 2, 4, 4)
    assert _line_hits((0, 3), (6, 3), box)
    assert _line_hits((3, 0), (3, 6), box)
    assert _line_hits((0, 2), (6, 2), box)
    assert _line_hits((3, 3), (3, 3), box)
    assert not _line_hits((0, 0), (1, 1), box)
    assert not _line_hits((0, 1), (6, 1), box)
