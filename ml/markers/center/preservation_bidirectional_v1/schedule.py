# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fixed, unregistered synthetic-anchor schedule with full per-view epoch coverage."""
from dataclasses import dataclass
import torch

from ..real_positive_preservation_v1.component_source import VIEWS
from ..real_positive_preservation_v1.schedule import BATCHES_PER_EPOCH, EPOCHS, TOTAL_STEPS

ALGORITHM = "bidirectional-anchor-view-cycle-randperm-v1"
SEED = 20261002
BATCH_SIZE = 128
COMPONENT_ROWS = 3258
FAMILY_ROWS = 8230


@dataclass(frozen=True)
class DirectionalPlan:
    step: int
    epoch: int
    view: str
    component_indices: tuple[int, ...]
    family_indices: tuple[int, ...]


class DirectionalSchedule:
    """Reserve and commit once per step; no recovery or resumed schedule."""
    def __init__(self):
        if (BATCHES_PER_EPOCH // len(VIEWS)) * BATCH_SIZE < max(COMPONENT_ROWS, FAMILY_ROWS):
            raise RuntimeError("Every train anchor view must be covered each epoch")
        self.completed = 0
        self._pending = None
        self._epoch = -1
        self._component_orders = []
        self._family_orders = []

    def reserve(self, step: int) -> DirectionalPlan:
        if type(step) is not int or self._pending is not None or step != self.completed + 1 or step > TOTAL_STEPS:
            raise RuntimeError("Directional schedule is repeated, skipped, or complete")
        epoch, batch = divmod(step - 1, BATCHES_PER_EPOCH)
        if epoch != self._epoch:
            self._component_orders = [torch.randperm(COMPONENT_ROWS, generator=torch.Generator(device="cpu").manual_seed(
                SEED + epoch * 8 + view), device="cpu") for view in range(len(VIEWS))]
            self._family_orders = [torch.randperm(FAMILY_ROWS, generator=torch.Generator(device="cpu").manual_seed(
                SEED + epoch * 8 + 4 + view), device="cpu") for view in range(len(VIEWS))]
            self._epoch = epoch
        view = batch % len(VIEWS)
        start = (batch // len(VIEWS)) * BATCH_SIZE
        component = tuple(int(self._component_orders[view][(start + offset) % COMPONENT_ROWS]) for offset in range(BATCH_SIZE))
        family = tuple(int(self._family_orders[view][(start + offset) % FAMILY_ROWS]) for offset in range(BATCH_SIZE))
        if len(set(component)) != BATCH_SIZE or len(set(family)) != BATCH_SIZE:
            raise RuntimeError("A directional batch contains duplicate anchors")
        self._pending = DirectionalPlan(step, epoch + 1, VIEWS[view], component, family)
        return self._pending

    def commit(self, plan: DirectionalPlan) -> None:
        if plan is not self._pending or plan.step != self.completed + 1:
            raise RuntimeError("Directional schedule commit is not the pending step")
        self.completed = plan.step
        self._pending = None
