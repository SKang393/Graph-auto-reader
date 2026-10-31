# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fixed component-train-negative batches. No model or optimizer operations."""
from dataclasses import dataclass

import torch

from .component_source import VIEWS

ALGORITHM = "component-negative-view-cycle-randperm-v1"
SEED = 20261002
BATCH_SIZE = 512
ROWS_PER_VIEW = 32580
BATCHES_PER_EPOCH = 283
EPOCHS = 8
TOTAL_STEPS = BATCHES_PER_EPOCH * EPOCHS


@dataclass(frozen=True)
class ComponentPlan:
    step: int
    epoch: int
    view: str
    indices: tuple[int, ...]


class ComponentSchedule:
    """One-time sequential schedule with full coverage of every view per epoch."""
    def __init__(self) -> None:
        if (BATCHES_PER_EPOCH // len(VIEWS)) * BATCH_SIZE < ROWS_PER_VIEW:
            raise RuntimeError("The fixed component schedule cannot cover every view")
        self.completed = 0
        self._pending: ComponentPlan | None = None
        self._epoch = -1
        self._orders: list[torch.Tensor] = []

    def reserve(self, step: int) -> ComponentPlan:
        if type(step) is not int or self._pending is not None or step != self.completed + 1 or step > TOTAL_STEPS:
            raise RuntimeError("Component schedule is repeated, skipped, or complete")
        zero = step - 1
        epoch, batch = divmod(zero, BATCHES_PER_EPOCH)
        if epoch != self._epoch:
            self._orders = [torch.randperm(ROWS_PER_VIEW, generator=torch.Generator(device="cpu").manual_seed(
                SEED + epoch * len(VIEWS) + view), device="cpu") for view in range(len(VIEWS))]
            self._epoch = epoch
        view = batch % len(VIEWS)
        start = ((batch // len(VIEWS)) * BATCH_SIZE) % ROWS_PER_VIEW
        indices = tuple(int(self._orders[view][(start + offset) % ROWS_PER_VIEW]) for offset in range(BATCH_SIZE))
        if len(set(indices)) != BATCH_SIZE:
            raise RuntimeError("Component batch contains duplicate indices")
        self._pending = ComponentPlan(step, epoch + 1, VIEWS[view], indices)
        return self._pending

    def commit(self, plan: ComponentPlan) -> None:
        if plan is not self._pending or plan.step != self.completed + 1:
            raise RuntimeError("Component schedule commit is not the pending step")
        self.completed = plan.step
        self._pending = None
