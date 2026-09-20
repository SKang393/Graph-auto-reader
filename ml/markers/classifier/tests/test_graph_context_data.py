# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import hashlib

import numpy as np
import pytest

from ml.markers.classifier.graph_context_data import CoverageCase, cases, prepare_case
from ml.markers.classifier.graph_context_cache import text_components, validate_pixels
from ml.markers.classifier.native_context_cache import observable_targets


def test_complete_train_only_recipe_inventory():
    recipes = cases()
    assert len(recipes) == 9*3*6*64 + 8*256
    assert len({r.sample_id for r in recipes}) == len(recipes)
    assert {r.shape for r in recipes if r.shape} == {
        "circle", "square", "triangle_up", "triangle_down", "diamond", "star", "asterisk", "cross", "other"}


@pytest.mark.parametrize("variant", range(8))
@pytest.mark.parametrize("shape,fill,context", [
    ("circle", "filled", "bent_line"), ("square", "open", "horizontal_line"),
    ("cross", "degraded", "isolated"), (None, None, "text"), (None, None, "legend")])
def test_print_damage_keeps_finite_repeatable_original_raster_patches(variant, shape, fill, context):
    case = CoverageCase(shape, fill, context, variant)
    first, row = prepare_case(case)
    second, other = prepare_case(case)
    np.testing.assert_array_equal(first, second)
    assert row == other and row["split"] == "train"
    assert first.shape == (1, 32, 32) and first.dtype == np.float32
    assert np.isfinite(first).all() and first.min() >= 0 and first.max() <= 1
    if shape:
        assert np.ptp(first) > 0
    if fill == "degraded":
        assert row["fill_index"] == 2
    for stage in row["degradations"]:
        if stage["kind"] == "halftone":
            assert row["stroke"] >= stage["parameters"]["cell_size"]


def test_connections_are_not_two_fixed_training_angles():
    angles = []
    for variant in range(16):
        _, row = prepare_case(CoverageCase("circle", "open", "bent_line", variant))
        angles.extend(row["angles"])
    assert len(set(angles)) == 32 and min(angles) < -60 and max(angles) > 60


@pytest.mark.parametrize("case", [CoverageCase("cat", "open", "isolated", 0),
    CoverageCase("circle", "open", "isolated", 64), CoverageCase(None, "open", "text", 0),
    CoverageCase(None, None, "private", 0)])
def test_unregistered_recipe_is_rejected(case):
    with pytest.raises(ValueError, match="recipe"):
        prepare_case(case)


def test_text_components_preserve_original_coordinates_and_separate_ink():
    pixels = np.ones((30, 40), np.float32)
    pixels[12:17, 8:11] = 0
    pixels[12:17, 17:21] = 0
    result = text_components(pixels, [5, 10, 20, 10])
    assert result == [((9., 14.), 2.5), ((18.5, 14.), 2.5)]
    assert text_components(pixels, [50, 50, 5, 5]) == []


def record(patch, *, artifact=False, split="train"):
    row = {"sample_id": "one", "split": split, "artifact": artifact,
           "shape_index": -1 if artifact else 0, "fill_index": -1 if artifact else 1,
           "patch_sha256": hashlib.sha256(patch.tobytes()).hexdigest()}
    row.update(observable_targets([row])[0])
    return row


def test_blank_negative_is_not_an_invented_visible_marker():
    patch = np.zeros((1, 32, 32), np.float32)
    validate_pixels(patch[None], [record(patch, artifact=True)], "train")
    with pytest.raises(ValueError, match="Invisible"):
        validate_pixels(patch[None], [record(patch)], "train")


def test_training_loader_rejects_dev_split_or_changed_tensor():
    patch = np.zeros((1, 32, 32), np.float32); patch[0, 10:20, 10:20] = 1
    with pytest.raises(ValueError, match="split"):
        validate_pixels(patch[None], [record(patch, split="dev")], "train")
    row = record(patch)
    patch[0, 0, 0] = .5
    with pytest.raises(ValueError, match="patch"):
        validate_pixels(patch[None], [row], "train")
