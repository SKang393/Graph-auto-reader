# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import hashlib

import pytest
import torch

from ml.markers.center.mask_preserving_v24.family_scenes import build_family_split
from ml.synthetic.dataset import family_holdout_audit


def _tensor_sha256(value: torch.Tensor) -> str:
    return hashlib.sha256(value.numpy().tobytes(order="C")).hexdigest()


def test_family_scenes_are_deterministic_and_match_the_five_axis_audit():
    audit = family_holdout_audit()
    train = build_family_split("train")
    dev = build_family_split("dev")

    assert len(train) == audit["splits"]["train"]["scene_count"] == 4
    assert len(dev) == audit["splits"]["dev"]["scene_count"] == 3
    assert sum(len(scene.centers) for scene in train) == 100
    assert sum(len(scene.centers) for scene in dev) == 206
    assert {scene.family for scene in train}.isdisjoint(
        {scene.family for scene in dev}
    )
    assert _tensor_sha256(train[0].tensor) == _tensor_sha256(
        build_family_split("train")[0].tensor
    )


@pytest.mark.parametrize("split", ["train", "dev"])
def test_family_scenes_obey_the_v24_tensor_and_truth_contract(split):
    scenes = build_family_split(split)
    assert scenes
    for scene in scenes:
        assert scene.tensor.shape[0] == 3
        assert scene.tensor.dtype == torch.float32
        assert bool(torch.isfinite(scene.tensor).all())
        assert float(scene.tensor.min()) >= 0
        assert float(scene.tensor.max()) <= 1
        assert len(scene.centers) == len(scene.diameters) > 0
        assert scene.hard_negatives
        assert all(diameter > 0 for diameter in scene.diameters)


def test_family_split_rejects_unknown_name():
    with pytest.raises(ValueError, match="split"):
        build_family_split("sealed")
