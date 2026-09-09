# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Regressions for the preregistered global family-background sampler."""

from __future__ import annotations

from types import SimpleNamespace

import pytest
import torch

from ml.markers.center.mask_preserving_v24.family_scenes import FamilyScene
from ml.markers.center.mask_preserving_v24.stratified_background import (
    BIN_COUNT,
    allocate_quotas,
    select_stratified_background,
)
from ml.markers.center.mask_preserving_v24 import train_p1


def _patches(count: int, *, distinct_mask_bins: bool = False) -> torch.Tensor:
    patches = torch.zeros((count, 3, 33, 33), dtype=torch.float32)
    if distinct_mask_bins:
        for index in range(count):
            mask_bin = index % 4
            if mask_bin & 1:
                patches[index, 1, 16, 16] = .35
            if mask_bin & 2:
                patches[index, 2, 16, 16] = .35
    return patches


def _scene(tag: int, *, centers=((0.0, 0.0),), hard_negatives=()) -> FamilyScene:
    tensor = torch.zeros((3, 8, 8), dtype=torch.float32)
    tensor[0, 0, 0] = float(tag)
    return FamilyScene(
        split="train",
        family="owned-family",
        seed=39300 + tag,
        tensor=tensor,
        centers=tuple(centers),
        diameters=tuple(4.0 for _ in centers),
        hard_negatives=tuple(hard_negatives),
    )


def _proposals(coordinates, *, distinct_mask_bins: bool = False):
    coordinates = tuple(coordinates)
    return SimpleNamespace(
        coordinates=torch.tensor(coordinates, dtype=torch.float32).reshape(-1, 2),
        patches=_patches(len(coordinates), distinct_mask_bins=distinct_mask_bins),
    )


def _sample(scenes, generator):
    return train_p1._examples_with_report(
        scenes,
        10,
        generator,
        sampling_mode="family-train",
        family_generic_negative_mode=train_p1.FAMILY_GENERIC_NEGATIVE_STRATIFIED,
        family_generic_negative_seed=train_p1.FAMILY_GENERIC_NEGATIVE_STRATIFIED_SEED,
    )


def _bin_map(value: int = 0) -> dict[str, int]:
    return {str(bin_id): value for bin_id in range(BIN_COUNT)}


def _stratified_config() -> dict:
    return {
        "family_generic_negative_mode": train_p1.FAMILY_GENERIC_NEGATIVE_STRATIFIED,
        "family_generic_negative_seed": train_p1.FAMILY_GENERIC_NEGATIVE_STRATIFIED_SEED,
        train_p1.FAMILY_STRATIFIED_BIN_CAPACITIES_CONFIG_KEY: _bin_map(),
        train_p1.FAMILY_STRATIFIED_BIN_QUOTAS_CONFIG_KEY: _bin_map(),
    }


def test_quota_allocation_uses_one_each_then_residual_largest_remainder():
    capacities = (5, 3, 2) + (0,) * (BIN_COUNT - 3)

    assert allocate_quotas(capacities, 6)[:3] == (3, 2, 1)
    assert allocate_quotas(capacities, 2)[:3] == (1, 1, 0)
    assert allocate_quotas(capacities, 10)[:3] == capacities[:3]


def test_quota_allocation_rejects_impossible_or_malformed_budgets():
    capacities = (1,) + (0,) * (BIN_COUNT - 1)

    with pytest.raises(ValueError, match="exceeds eligible capacity"):
        allocate_quotas(capacities, 2)
    with pytest.raises(ValueError, match="64"):
        allocate_quotas((1,), 1)
    with pytest.raises(ValueError, match="non-negative integers"):
        allocate_quotas(capacities, True)


def test_empty_eligible_pool_is_valid_only_with_zero_budget():
    patches = (_patches(3),)
    eligible = (torch.empty(0, dtype=torch.int64),)

    result = select_stratified_background(patches, eligible, 0, 20260909)

    assert result.selections == ((),)
    assert result.capacities == (0,) * BIN_COUNT
    assert result.quotas == (0,) * BIN_COUNT
    with pytest.raises(ValueError, match="exceeds eligible capacity"):
        select_stratified_background(patches, eligible, 1, 20260909)


def test_stratified_selection_is_deterministic_sorted_and_without_replacement():
    patches = (_patches(20, distinct_mask_bins=True),)
    eligible = (torch.arange(20, dtype=torch.int64),)

    first = select_stratified_background(patches, eligible, 11, 20260909)
    second = select_stratified_background(patches, eligible, 11, 20260909)

    assert first == second
    assert first.selections[0] == tuple(sorted(first.selections[0]))
    assert len(first.selections[0]) == len(set(first.selections[0])) == 11
    assert sum(first.quotas) == 11
    assert all(quota <= capacity for quota, capacity in zip(first.quotas, first.capacities, strict=True))


def test_global_sampler_retains_positive_and_hard_indices_and_uses_background_panels(
    monkeypatch,
):
    hard_coordinates = (
        (100.0, 100.0), (104.0, 100.0), (96.0, 100.0),
        (100.0, 104.0), (100.0, 96.0), (104.0, 104.0),
        (96.0, 96.0), (104.0, 96.0), (96.0, 104.0),
        (108.0, 100.0), (92.0, 100.0), (100.0, 108.0),
    )
    proposal_by_tag = {
        1: _proposals(((0.0, 0.0), *hard_coordinates, *((200.0 + i, 40.0) for i in range(8)))),
        2: _proposals(((300.0 + i, 40.0) for i in range(8)), distinct_mask_bins=True),
        3: _proposals(((0.0, 0.0), *((400.0 + i, 40.0) for i in range(8)))),
    }
    monkeypatch.setattr(
        train_p1,
        "extract_proposals",
        lambda tensor: proposal_by_tag[int(tensor[0, 0, 0])],
    )
    scenes = (
        _scene(1, hard_negatives=(("text", 100.0, 100.0),)),
        _scene(2, centers=()),
        _scene(3),
    )
    prefix_generator = torch.Generator().manual_seed(91)
    stratified_generator = torch.Generator().manual_seed(91)

    prefix = train_p1._examples_with_report(
        scenes, 10, prefix_generator, sampling_mode="family-train"
    )[5]
    stratified = _sample(scenes, stratified_generator)[5]

    hard_indices = set(range(1, 13))
    assert set(prefix.selections[0]) & hard_indices == set(stratified.selections[0]) & hard_indices
    assert 0 in stratified.selections[0] and 0 in stratified.selections[2]
    assert stratified.selections[1]
    assert stratified.counts == {
        "positive": 2,
        "hard_negative": 10,
        "other_negative": 10,
    }
    assert sum(stratified.stratified_bin_quotas.values()) == 10
    assert len(stratified.stratified_bin_capacities) == BIN_COUNT
    assert torch.equal(
        torch.rand(16, generator=prefix_generator),
        torch.rand(16, generator=stratified_generator),
    )


def test_stratified_sampler_rejects_wrong_seed_or_changed_protocol_before_proposals(
    monkeypatch,
):
    scene = _scene(1)
    monkeypatch.setattr(
        train_p1,
        "extract_proposals",
        lambda tensor: pytest.fail("invalid binding reached proposal extraction"),
    )
    with pytest.raises(ValueError, match="requires seed 20260909"):
        train_p1._examples_with_report(
            (scene,),
            10,
            torch.Generator().manual_seed(1),
            sampling_mode="family-train",
            family_generic_negative_mode=train_p1.FAMILY_GENERIC_NEGATIVE_STRATIFIED,
            family_generic_negative_seed=20260910,
        )

    monkeypatch.setattr(train_p1, "_sha", lambda path: "0" * 64)
    with pytest.raises(RuntimeError, match="stratified family background protocol identity changed"):
        _sample((scene,), torch.Generator().manual_seed(1))


@pytest.mark.parametrize(
    ("field", "value", "message"),
    [
        (train_p1.FAMILY_STRATIFIED_BIN_CAPACITIES_CONFIG_KEY, None, "exactly numeric keys"),
        (train_p1.FAMILY_STRATIFIED_BIN_QUOTAS_CONFIG_KEY, [], "exactly numeric keys"),
        (train_p1.FAMILY_STRATIFIED_BIN_CAPACITIES_CONFIG_KEY, {str(index): 0 for index in range(63)}, "exactly numeric keys"),
        (train_p1.FAMILY_STRATIFIED_BIN_QUOTAS_CONFIG_KEY, {index: 0 for index in range(BIN_COUNT)}, "exactly numeric keys"),
        (train_p1.FAMILY_STRATIFIED_BIN_CAPACITIES_CONFIG_KEY, {**_bin_map(), "0": True}, "non-negative integers"),
        (train_p1.FAMILY_STRATIFIED_BIN_QUOTAS_CONFIG_KEY, {**_bin_map(), "63": -1}, "non-negative integers"),
    ],
)
def test_stratified_config_rejects_missing_or_malformed_bin_maps(field, value, message):
    config = _stratified_config()
    config[field] = value

    with pytest.raises(ValueError, match=message):
        train_p1._family_generic_negative_config(config)


@pytest.mark.parametrize(
    "field",
    [
        train_p1.FAMILY_STRATIFIED_BIN_CAPACITIES_CONFIG_KEY,
        train_p1.FAMILY_STRATIFIED_BIN_QUOTAS_CONFIG_KEY,
    ],
)
def test_stratified_config_requires_both_preflight_bin_maps(field):
    config = _stratified_config()
    del config[field]

    with pytest.raises(ValueError, match="exactly numeric keys"):
        train_p1._family_generic_negative_config(config)


@pytest.mark.parametrize(
    ("field", "report_field", "message"),
    [
        (
            train_p1.FAMILY_STRATIFIED_BIN_CAPACITIES_CONFIG_KEY,
            "stratified_bin_capacities",
            "capacities changed",
        ),
        (
            train_p1.FAMILY_STRATIFIED_BIN_QUOTAS_CONFIG_KEY,
            "stratified_bin_quotas",
            "quotas changed",
        ),
    ],
)
def test_stratified_config_rejects_structurally_valid_changed_bin_maps(
    field,
    report_field,
    message,
):
    config = _stratified_config()
    actual = _bin_map()
    actual["17"] = 1
    report = SimpleNamespace(
        stratified_bin_capacities=_bin_map(),
        stratified_bin_quotas=_bin_map(),
    )
    setattr(report, report_field, actual)

    with pytest.raises(RuntimeError, match=message):
        train_p1._validate_stratified_family_sampling_config(config, report)


def test_stratified_config_accepts_exact_64_bin_preflight_maps():
    config = _stratified_config()
    capacities = _bin_map()
    quotas = _bin_map()
    capacities.update({"0": 6032, "49": 1, "56": 0, "63": 44})
    quotas.update({"0": 301, "49": 1, "56": 0, "63": 3})
    config[train_p1.FAMILY_STRATIFIED_BIN_CAPACITIES_CONFIG_KEY] = capacities
    config[train_p1.FAMILY_STRATIFIED_BIN_QUOTAS_CONFIG_KEY] = quotas
    report = SimpleNamespace(
        stratified_bin_capacities=dict(capacities),
        stratified_bin_quotas=dict(quotas),
    )

    assert train_p1._family_generic_negative_config(config) == (
        train_p1.FAMILY_GENERIC_NEGATIVE_STRATIFIED,
        train_p1.FAMILY_GENERIC_NEGATIVE_STRATIFIED_SEED,
    )
    train_p1._validate_stratified_family_sampling_config(config, report)


def test_existing_modes_remain_available_with_their_existing_defaults():
    assert train_p1._family_generic_negative_config({}) == (
        train_p1.FAMILY_GENERIC_NEGATIVE_PREFIX,
        None,
    )
    assert train_p1._family_generic_negative_config({
        "family_generic_negative_mode": train_p1.FAMILY_GENERIC_NEGATIVE_UNIFORM,
        "family_generic_negative_seed": 20260909,
    }) == (train_p1.FAMILY_GENERIC_NEGATIVE_UNIFORM, 20260909)
    config = _stratified_config()
    assert train_p1._family_generic_negative_config(config) == (
        train_p1.FAMILY_GENERIC_NEGATIVE_STRATIFIED,
        20260909,
    )
    assert train_p1._family_generic_negative_config({
        "family_generic_negative_mode": train_p1.FAMILY_GENERIC_NEGATIVE_STRATIFIED,
        "family_generic_negative_seed": 20260909,
        train_p1.FAMILY_STRATIFIED_BIN_CAPACITIES_CONFIG_KEY: _bin_map(),
        train_p1.FAMILY_STRATIFIED_BIN_QUOTAS_CONFIG_KEY: _bin_map(),
    }) == (train_p1.FAMILY_GENERIC_NEGATIVE_STRATIFIED, 20260909)
