# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import hashlib

import numpy as np
import pytest

from ml.markers.classifier.presence_context_cache import audit, crop_radius, validate


def row(patch, **changes):
    return {"sample_id": "one", "split": "train", "target_kind": "presence_only",
            "shape": None, "fill": None, "artifact": False, "context": "component_marker",
            "patch_sha256": hashlib.sha256(patch.tobytes()).hexdigest(), **changes}


def test_large_solid_marker_has_presence_without_an_invented_shape():
    patch = np.ones((1, 32, 32), np.float32)
    validate(patch[None], [row(patch)])
    with pytest.raises(ValueError):
        validate(patch[None], [row(patch, shape="square")])


@pytest.mark.parametrize("split", ["dev", "validation", "sealed"])
def test_nontraining_rows_cannot_enter_supplement(split):
    patch = np.ones((1, 32, 32), np.float32)
    with pytest.raises(ValueError):
        validate(patch[None], [row(patch, split=split)])


def test_blank_white_positive_is_invalid():
    patch = np.zeros((1, 32, 32), np.float32)
    with pytest.raises(ValueError):
        validate(patch[None], [row(patch)])
    validate(patch[None], [row(patch, artifact=True)])


def test_presence_conflicts_and_exact_development_leakage_fail_audit():
    patch = np.ones((1, 32, 32), np.float32)
    r = row(patch)
    base = {"train": (None, [dict(r, artifact=True)]), "dev": (None, [r])}
    result = audit(base, [r])
    assert result["status"] == "invalid"
    assert result["train_dev_exact_pixel_overlap"] == [r["patch_sha256"]]
    assert result["contradictory_presence_targets"] == [r["patch_sha256"]]


def test_authored_crops_respect_the_actual_radius_bounds():
    assert crop_radius(1., .7) == 2.5
    assert crop_radius(49., 1.3) == 8.
    assert crop_radius(10., .7) == 3.5
