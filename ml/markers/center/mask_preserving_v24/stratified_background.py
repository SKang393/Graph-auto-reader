# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Deterministic global stratification for V24 family generic negatives."""

from __future__ import annotations

from dataclasses import dataclass

import torch


MASK_THRESHOLD = 0.35
INK_THRESHOLD = 0.12
DENSITY_BOUNDARIES = (0.25, 0.5, 0.75)
BIN_COUNT = 64
FEATURE_CHUNK_SIZE = 4096


@dataclass(frozen=True)
class StratifiedBackgroundSelection:
    selections: tuple[tuple[int, ...], ...]
    capacities: tuple[int, ...]
    quotas: tuple[int, ...]


def allocate_quotas(capacities: tuple[int, ...], budget: int) -> tuple[int, ...]:
    """Allocate one per bin, then residual capacity by largest remainder."""
    if len(capacities) != BIN_COUNT:
        raise ValueError("stratified background requires exactly 64 bin capacities")
    if type(budget) is not int or budget < 0 or any(
        type(capacity) is not int or capacity < 0 for capacity in capacities
    ):
        raise ValueError("stratified background capacities and budget must be non-negative integers")
    if budget > sum(capacities):
        raise ValueError("stratified background budget exceeds eligible capacity")

    quotas = [0] * BIN_COUNT
    nonempty = [bin_id for bin_id, capacity in enumerate(capacities) if capacity]
    for bin_id in nonempty[:budget]:
        quotas[bin_id] = 1
    remaining = budget - sum(quotas)
    if remaining == 0:
        return tuple(quotas)

    residual = [capacity - quota for capacity, quota in zip(capacities, quotas, strict=True)]
    residual_total = sum(residual)
    floors = [(remaining * capacity) // residual_total for capacity in residual]
    for bin_id, count in enumerate(floors):
        quotas[bin_id] += count
    left = remaining - sum(floors)
    order = sorted(
        (
            ((remaining * residual[bin_id]) % residual_total, bin_id)
            for bin_id in range(BIN_COUNT)
            if residual[bin_id] > floors[bin_id]
        ),
        key=lambda item: (-item[0], item[1]),
    )
    for _, bin_id in order[:left]:
        quotas[bin_id] += 1
    if sum(quotas) != budget or any(
        quota > capacity for quota, capacity in zip(quotas, capacities, strict=True)
    ):
        raise RuntimeError("stratified background quota allocation is inconsistent")
    return tuple(quotas)


def _bin_ids(patches: torch.Tensor) -> torch.Tensor:
    if patches.ndim != 4 or tuple(patches.shape[1:]) != (3, 33, 33):
        raise ValueError("stratified background patches must have shape [N,3,33,33]")
    center = patches[:, 1:, 14:19, 14:19]
    ocr = center[:, 0].amax(dim=(1, 2)).ge(MASK_THRESHOLD).to(torch.int64)
    artifact = center[:, 1].amax(dim=(1, 2)).ge(MASK_THRESHOLD).to(torch.int64)
    mask_bin = ocr + (artifact * 2)
    dark = patches[:, 0].ge(INK_THRESHOLD).to(torch.float32)
    row_density = dark.mean(dim=2).amax(dim=1)
    column_density = dark.mean(dim=1).amax(dim=1)
    boundaries = torch.tensor(DENSITY_BOUNDARIES, dtype=torch.float32, device=patches.device)
    row_bin = torch.bucketize(row_density, boundaries, right=True)
    column_bin = torch.bucketize(column_density, boundaries, right=True)
    return mask_bin * 16 + row_bin * 4 + column_bin


def select_stratified_background(
    patches_by_scene: tuple[torch.Tensor, ...],
    eligible_indices_by_scene: tuple[torch.Tensor, ...],
    budget: int,
    seed: int,
) -> StratifiedBackgroundSelection:
    if len(patches_by_scene) != len(eligible_indices_by_scene) or not patches_by_scene:
        raise ValueError("stratified background requires aligned non-empty scene inputs")
    if type(seed) is not int:
        raise ValueError("stratified background seed must be an integer")

    entries: list[list[tuple[int, int]]] = [[] for _ in range(BIN_COUNT)]
    for scene_index, (patches, eligible) in enumerate(
        zip(patches_by_scene, eligible_indices_by_scene, strict=True)
    ):
        if eligible.ndim != 1 or eligible.dtype != torch.int64:
            raise ValueError("eligible proposal indices must be one-dimensional int64 tensors")
        if len(eligible) and (
            int(eligible.min()) < 0
            or int(eligible.max()) >= len(patches)
            or len(eligible.unique()) != len(eligible)
        ):
            raise ValueError("eligible proposal indices are out of range or duplicated")
        for start in range(0, len(eligible), FEATURE_CHUNK_SIZE):
            indices = eligible[start : start + FEATURE_CHUNK_SIZE]
            bins = _bin_ids(patches.index_select(0, indices)).cpu().tolist()
            for proposal_index, bin_id in zip(indices.cpu().tolist(), bins, strict=True):
                entries[bin_id].append((scene_index, proposal_index))

    capacities = tuple(len(items) for items in entries)
    quotas = allocate_quotas(capacities, budget)
    generator = torch.Generator(device="cpu").manual_seed(seed)
    selected: list[list[int]] = [[] for _ in patches_by_scene]
    for bin_id, (items, quota) in enumerate(zip(entries, quotas, strict=True)):
        if quota == 0:
            continue
        order = torch.randperm(len(items), generator=generator)[:quota].tolist()
        for offset in order:
            scene_index, proposal_index = items[offset]
            selected[scene_index].append(proposal_index)
    selections = tuple(tuple(sorted(indices)) for indices in selected)
    if sum(map(len, selections)) != budget:
        raise RuntimeError("stratified background selection count differs from its budget")
    return StratifiedBackgroundSelection(selections, capacities, quotas)
