# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Isolated uniform family-background sampler regressions."""

from __future__ import annotations

from dataclasses import replace
from types import SimpleNamespace

import pytest
import torch

from ml.markers.center.mask_preserving_v24.family_scenes import FamilyScene
from ml.markers.center.mask_preserving_v24 import train_p1


def _scene(*, split: str = "train", hard_negatives=()) -> FamilyScene:
    return FamilyScene(
        split=split,
        family="owned-family",
        seed=39300,
        tensor=torch.zeros((3, 8, 8), dtype=torch.float32),
        centers=((0.0, 0.0),),
        diameters=(4.0,),
        hard_negatives=tuple(hard_negatives),
    )


def _background_proposals(count: int = 16):
    coordinates = [(0.0, 0.0)]
    coordinates.extend((100.0 + 4.0 * index, 40.0) for index in range(count - 1))
    return SimpleNamespace(
        coordinates=torch.tensor(coordinates, dtype=torch.float32),
        patches=torch.arange(count, dtype=torch.float32).reshape(count, 1, 1, 1).expand(
            count, 3, 33, 33
        ).clone(),
    )


def _mixed_proposals():
    hard = (
        (100.0, 100.0),
        (104.0, 100.0),
        (96.0, 100.0),
        (100.0, 104.0),
        (100.0, 96.0),
        (104.0, 104.0),
        (96.0, 96.0),
        (104.0, 96.0),
        (96.0, 104.0),
        (108.0, 100.0),
        (92.0, 100.0),
        (100.0, 108.0),
    )
    generic = tuple((200.0 + 4.0 * index, 40.0) for index in range(16))
    coordinates = ((0.0, 0.0), *hard, *generic)
    count = len(coordinates)
    return SimpleNamespace(
        coordinates=torch.tensor(coordinates, dtype=torch.float32),
        patches=torch.zeros((count, 3, 33, 33), dtype=torch.float32),
    )


def _sample(scenes, *, mode="prefix", seed=None, generator_seed=17):
    return train_p1._examples_with_report(
        scenes,
        10,
        torch.Generator().manual_seed(generator_seed),
        sampling_mode="family-train",
        family_generic_negative_mode=mode,
        family_generic_negative_seed=seed,
    )


def test_legacy_prefix_default_is_unchanged(monkeypatch):
    proposals = _background_proposals()
    monkeypatch.setattr(train_p1, "extract_proposals", lambda tensor: proposals)
    scene = _scene()

    default = train_p1._examples_with_report(
        (scene,), 10, torch.Generator().manual_seed(17), sampling_mode="family-train"
    )
    explicit = _sample((scene,))

    assert default[5] == explicit[5]
    assert default[5].selections == (tuple(range(11)),)
    for left, right in zip(default[:5], explicit[:5], strict=True):
        assert torch.equal(left, right)


def test_uniform_is_deterministic_without_replacement_and_reaches_full_pool(monkeypatch):
    proposals = _background_proposals()
    monkeypatch.setattr(train_p1, "extract_proposals", lambda tensor: proposals)
    scene = _scene()

    first = _sample(
        (scene,),
        mode=train_p1.FAMILY_GENERIC_NEGATIVE_UNIFORM,
        seed=train_p1.FAMILY_GENERIC_NEGATIVE_UNIFORM_SEED,
    )[5]
    second = _sample(
        (scene,),
        mode=train_p1.FAMILY_GENERIC_NEGATIVE_UNIFORM,
        seed=train_p1.FAMILY_GENERIC_NEGATIVE_UNIFORM_SEED,
    )[5]
    selected = first.selections[0]
    selected_background = set(selected) - {0}

    assert first == second
    assert first.counts == {
        "positive": 1,
        "hard_negative": 0,
        "other_negative": 10,
    }
    assert 0 in selected
    assert len(selected_background) == 10
    assert max(selected_background) > 10
    assert selected != tuple(range(11))


def test_uniform_preserves_positive_hard_and_passed_rng_streams(monkeypatch):
    proposals = _mixed_proposals()
    monkeypatch.setattr(train_p1, "extract_proposals", lambda tensor: proposals)
    hard_scene = _scene(hard_negatives=(("text", 100.0, 100.0),))
    background_scene = replace(hard_scene, hard_negatives=(), seed=39301)
    scenes = (hard_scene, background_scene)
    prefix_generator = torch.Generator().manual_seed(91)
    uniform_generator = torch.Generator().manual_seed(91)
    global_before = torch.get_rng_state().clone()

    prefix = train_p1._examples_with_report(
        scenes, 10, prefix_generator, sampling_mode="family-train"
    )[5]
    global_after_prefix = torch.get_rng_state().clone()
    uniform = train_p1._examples_with_report(
        scenes,
        10,
        uniform_generator,
        sampling_mode="family-train",
        family_generic_negative_mode=train_p1.FAMILY_GENERIC_NEGATIVE_UNIFORM,
        family_generic_negative_seed=train_p1.FAMILY_GENERIC_NEGATIVE_UNIFORM_SEED,
    )[5]

    assert torch.equal(global_before, global_after_prefix)
    assert torch.equal(global_before, torch.get_rng_state())
    assert torch.equal(
        torch.rand(16, generator=prefix_generator),
        torch.rand(16, generator=uniform_generator),
    )
    assert prefix.selections[0] == uniform.selections[0]
    assert prefix.counts == uniform.counts == {
        "positive": 2,
        "hard_negative": 10,
        "other_negative": 10,
    }
    assert all(len(indices) == len(set(indices)) for indices in uniform.selections)


@pytest.mark.parametrize(
    ("mode", "seed", "message"),
    [
        ("unknown", None, "unsupported"),
        ("prefix", 20260909, "does not accept a seed"),
        ("uniform_without_replacement", None, "requires seed 20260909"),
        ("uniform_without_replacement", 20260910, "requires seed 20260909"),
        ("uniform_without_replacement", True, "requires seed 20260909"),
        (["prefix"], None, "unsupported"),
    ],
)
def test_invalid_family_background_options_fail_before_sampling(
    monkeypatch, mode, seed, message
):
    monkeypatch.setattr(
        train_p1,
        "extract_proposals",
        lambda tensor: pytest.fail("invalid options reached proposal extraction"),
    )
    with pytest.raises(ValueError, match=message):
        _sample((_scene(),), mode=mode, seed=seed)


def test_uniform_rejects_development_and_nonfamily_sampling_before_extraction(monkeypatch):
    monkeypatch.setattr(
        train_p1,
        "extract_proposals",
        lambda tensor: pytest.fail("invalid split reached proposal extraction"),
    )
    options = {
        "family_generic_negative_mode": train_p1.FAMILY_GENERIC_NEGATIVE_UNIFORM,
        "family_generic_negative_seed": train_p1.FAMILY_GENERIC_NEGATIVE_UNIFORM_SEED,
    }
    with pytest.raises(ValueError, match="train FamilyScene"):
        train_p1._examples_with_report(
            (_scene(split="validation"),),
            10,
            torch.Generator().manual_seed(17),
            sampling_mode="family-train",
            **options,
        )
    with pytest.raises(ValueError, match="require family-train"):
        train_p1._examples_with_report(
            (_scene(),),
            10,
            torch.Generator().manual_seed(17),
            sampling_mode="subset-test",
            **options,
        )


def test_uniform_binds_frozen_protocol_before_extraction(monkeypatch):
    monkeypatch.setattr(train_p1, "_sha", lambda path: "0" * 64)
    monkeypatch.setattr(
        train_p1,
        "extract_proposals",
        lambda tensor: pytest.fail("changed protocol reached proposal extraction"),
    )
    with pytest.raises(RuntimeError, match="protocol identity changed"):
        _sample(
            (_scene(),),
            mode=train_p1.FAMILY_GENERIC_NEGATIVE_UNIFORM,
            seed=train_p1.FAMILY_GENERIC_NEGATIVE_UNIFORM_SEED,
        )


def test_config_dispatch_defaults_to_prefix_and_binds_uniform_seed():
    assert train_p1._family_generic_negative_config({}) == ("prefix", None)
    assert train_p1._family_generic_negative_config({
        "family_generic_negative_mode": "uniform_without_replacement",
        "family_generic_negative_seed": 20260909,
    }) == ("uniform_without_replacement", 20260909)
