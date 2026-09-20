# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from collections import Counter
from copy import deepcopy

import numpy as np
import pytest

from ml.synthetic.condition_label_layout import VERSION, bind_condition_label_layout
from ml.synthetic.renderer import render_scene
from ml.synthetic.templates import build_scene


def _fixture(design="aba"):
    scene = build_scene(design, 9047, "vector_clean", panel_count=1, session_count=18)
    labels = [r for r in scene["annotations"]["text_regions"] if r["role"] == "condition_label"]
    bars = scene["panels"][0]["condition_bars"]
    assert len(labels) == len(bars)
    assert [r["text"] for r in labels] == [b["label"] for b in bars]
    return scene, {r["region_id"]: b["bar_id"] for r, b in zip(labels, bars)}


def _move(scene, region, x, y):
    old = region["box"][:]
    artifact = next(a for a in scene["annotations"]["artifacts"] if a["kind"] == "text" and
                    a["role"] == region["role"] and a["geometry"]["coordinates"] == old)
    region["box"][:2] = [x, y]
    artifact["geometry"]["coordinates"][:2] = [x, y]


@pytest.mark.parametrize("split", ["train", "dev"])
def test_repeated_condition_moves_back_to_its_explicit_phase_without_changing_points(split):
    scene, bindings = _fixture()
    conditions = [r for r in scene["annotations"]["text_regions"] if r["role"] == "condition_label"]
    assert conditions[0]["text"] == conditions[-1]["text"] == "A"
    _move(scene, conditions[-1], conditions[0]["box"][0] + 30, 24)
    original = deepcopy(scene)
    result = bind_condition_label_layout(scene, split=split, label_bindings=bindings)
    assert result.version == VERSION and scene == original
    assert result.scene["panels"] == scene["panels"]
    assert result.scene["hard_negatives"] == scene["hard_negatives"]
    assert result.scene["degradations"] == scene["degradations"]
    labels = lambda s: Counter((r["panel_id"],r["role"],r["text"],r["visible"]) for r in s["annotations"]["text_regions"])
    assert labels(result.scene) == labels(scene)
    change = next(c for c in result.changes if c["old_region_id"] == conditions[-1]["region_id"])
    assert change["phase_id"] == scene["panels"][0]["phases"][-1]["phase_id"]
    assert change["outside_phase_before"]
    assert result == bind_condition_label_layout(scene, split=split, label_bindings=bindings)
    _, before, old_mask = render_scene(scene)
    _, after, new_mask = render_scene(result.scene)
    assert np.array_equal(old_mask, new_mask)
    assert before["panels"][0]["markers"] == after["panels"][0]["markers"]
    for entry in result.changes:
        scratch = deepcopy(result.scene)
        region = next(r for r in scratch["annotations"]["text_regions"] if r["region_id"] == entry["region_id"])
        text = next(t for p in after["panels"] for t in p["texts"] if t["region_id"] == region["region_id"])
        x,y,w,h = text["rendered_pixel_box"]
        l,t,r,b = entry["phase_header_area"]
        assert l <= x < x+w <= r and t <= y < y+h <= b
        region["visible"] = False
        background,_,_ = render_scene(scratch)
        assert np.all(np.asarray(background)[max(0,int(y)-4):int(y+h)+4,max(0,int(x)-4):int(x+w)+4] == 255)
    rebound = {c["old_region_id"]: c["region_id"] for c in result.changes}
    bindings = {rebound.get(key,key):value for key,value in bindings.items()}
    again = bind_condition_label_layout(result.scene, split=split, label_bindings=bindings)
    assert again.scene == result.scene and not again.changes


@pytest.mark.parametrize("defect", ["missing", "duplicate", "unknown", "wrong_text"])
def test_bad_bindings_fail_before_placement(defect):
    scene, bindings = _fixture()
    ids = list(bindings)
    if defect == "missing": bindings.pop(ids[0])
    elif defect == "duplicate": bindings[ids[1]] = bindings[ids[0]]
    elif defect == "unknown": bindings[ids[0]] = "missing-bar"
    else: bindings[ids[0]],bindings[ids[1]] = bindings[ids[1]],bindings[ids[0]]
    with pytest.raises(ValueError, match="binding|bind|bar"):
        bind_condition_label_layout(scene, split="dev", label_bindings=bindings)


@pytest.mark.parametrize("split", ["sealed", "private", "validation"])
def test_unregistered_splits_are_rejected(split):
    scene, bindings = _fixture()
    with pytest.raises(ValueError, match="train/dev"):
        bind_condition_label_layout(scene, split=split, label_bindings=bindings)


def test_no_space_retains_the_label_and_reports_the_failure():
    scene, bindings = _fixture()
    label = next(r for r in scene["annotations"]["text_regions"] if r["role"] == "condition_label")
    _move(scene, label, 10, 2)
    # Exhaust the header area without altering the authored phase bounds.
    panel = scene["panels"][0]
    panel["box"][1] = panel["plot_box"][1] - 2
    panel["box"][3] -= panel["box"][1]
    result = bind_condition_label_layout(scene, split="train", label_bindings=bindings)
    assert label in result.scene["annotations"]["text_regions"]
    assert any(c["region_id"] == label["region_id"] for c in result.unresolved)


def test_private_input_is_rejected():
    scene, bindings = _fixture()
    scene["provenance"]["private_data"] = True
    with pytest.raises(ValueError, match="private_data|owned"):
        bind_condition_label_layout(scene, split="train", label_bindings=bindings)


def test_authored_caption_does_not_have_to_equal_a_semantic_phase_code():
    scene, bindings = _fixture("ab")
    label = next(r for r in scene["annotations"]["text_regions"] if r["role"] == "condition_label")
    bar = next(b for b in scene["panels"][0]["condition_bars"] if b["bar_id"] == bindings[label["region_id"]])
    label["text"] = bar["label"] = "Review"
    _move(scene, label, 5, 30)
    # Authored text replacements can resize the region while retaining the
    # corresponding artifact's original dimensions at the same unique origin.
    label["box"][2:] = [45.0, 10.0]
    result = bind_condition_label_layout(scene, split="dev", label_bindings=bindings)
    assert result.scene["panels"] == scene["panels"]
    assert any(c["old_region_id"] == label["region_id"] for c in result.changes)
    assert any(r["text"] == "Review" for r in result.scene["annotations"]["text_regions"])


def test_ambiguous_artifact_origin_is_rejected():
    scene, bindings = _fixture()
    artifact = next(a for a in scene["annotations"]["artifacts"] if a["role"] == "condition_label")
    duplicate = deepcopy(artifact)
    duplicate["artifact_id"] = "71111111-1111-1111-1111-111111111111"
    scene["annotations"]["artifacts"].append(duplicate)
    with pytest.raises(ValueError, match="unique artifact"):
        bind_condition_label_layout(scene, split="dev", label_bindings=bindings)
