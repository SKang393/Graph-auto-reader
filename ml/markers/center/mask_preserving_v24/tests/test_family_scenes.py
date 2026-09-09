# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import hashlib
from dataclasses import replace

import pytest
import torch

from ml.markers.center.mask_preserving_v24.family_scenes import build_family_split
from ml.markers.center.mask_preserving_v24.train_p1 import _configure, _examples_with_report
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
    assert all(
        values["train_dev_disjoint"] and values["overlap_count"] == 0
        for values in audit["axes"].values()
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


def test_family_train_examples_are_bounded_and_weight_declared_hard_negatives():
    _configure(20260903)
    values = _examples_with_report(
        build_family_split("train"),
        10,
        torch.Generator().manual_seed(20260904),
        sampling_mode="family-train",
    )
    _, labels, _, _, hard, sampling = values

    assert len(labels) == 1936
    assert int((labels > 0.5).sum()) == 176
    assert int((labels <= 0.5).sum()) == 1760
    assert int(hard.sum()) == 348
    assert sampling.counts == {
        "positive": 176,
        "hard_negative": 348,
        "other_negative": 1412,
    }


def test_family_sampling_digest_is_deterministic_and_identity_bound():
    _configure(20260903)
    scenes = build_family_split("train")

    def digest(selected_scenes):
        return _examples_with_report(
            selected_scenes,
            10,
            torch.Generator().manual_seed(20260904),
            sampling_mode="family-train",
        )[5].selected_index_sha256

    # Legacy oracle-mask diagnostic after repairing rendered label collisions.
    # Actual training binds the separate runtime panel sampling identity.
    expected = "801477ff5db601a54394f9be5b19441170af3802bf2f73816dbe6913c5acdad7"
    assert digest(scenes) == expected
    assert digest(scenes) == expected
    assert digest((replace(scenes[0], seed=scenes[0].seed + 1), *scenes[1:])) != expected


def test_family_production_sampling_rejects_dev_scenes():
    with pytest.raises(ValueError, match="train FamilyScene"):
        _examples_with_report(
            build_family_split("dev"),
            10,
            torch.Generator().manual_seed(20260904),
            sampling_mode="family-train",
        )


def test_real_range_production_sampling_rejects_dev_scenes_before_sampling():
    dev_scene = build_family_split("dev")[0]
    with pytest.raises(ValueError, match="167-scene train split"):
        _examples_with_report(
            (dev_scene,) * 167,
            10,
            torch.Generator().manual_seed(20260904),
            sampling_mode="real-range-train",
        )


def test_sampling_mode_is_required_instead_of_inferred_from_scene_count():
    with pytest.raises(TypeError, match="sampling_mode"):
        _examples_with_report(
            build_family_split("train"),
            10,
            torch.Generator().manual_seed(20260904),
        )
