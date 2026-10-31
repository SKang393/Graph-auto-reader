# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Unregistered additions to the existing V37-relative preservation objective."""
from __future__ import annotations

from dataclasses import dataclass
import math
import torch
from torch import Tensor
from torch.nn import functional as F

from ..real_positive_context_v1.model import RealPositiveCenterNet
from ..real_positive_context_v1.objective import _require_training_gpu
from ..real_positive_preservation_v1.component_source import verify_v37_model
from .source import SyntheticTrainFeatures


@dataclass(frozen=True)
class DirectionalPenalties:
    weighted_total: Tensor
    component_positive_downward: Tensor
    family_negative_upward: Tensor


def directional_penalties(model: RealPositiveCenterNet,
                          component_positive: SyntheticTrainFeatures, component_indices: tuple[int, ...],
                          family_negative: SyntheticTrainFeatures, family_indices: tuple[int, ...], *,
                          component_weight: float, family_weight: float,
                          _allow_synthetic_fixture: bool = False) -> DirectionalPenalties:
    """Penalize P1 residual shifts that suppress synthetic positives or promote negatives.

    This adds to, and does not replace, the existing mixed loss and component-negative
    penalty. Weights must be explicitly fixed by a future registered protocol.
    Anchor labels are synthetic and may differ from decoded-object truth.
    """
    if (not isinstance(model, RealPositiveCenterNet) or not isinstance(component_positive, SyntheticTrainFeatures) or
            not isinstance(family_negative, SyntheticTrainFeatures) or
            component_positive.cohort != "component_positive" or family_negative.cohort != "family_negative"):
        raise ValueError("The two authenticated train cohorts and V37 residual model are required")
    for value in (component_weight, family_weight):
        if type(value) not in (int, float) or not math.isfinite(value) or value <= 0:
            raise ValueError("Explicit positive finite loss weights are required")
    _require_training_gpu(*model.parameters(), *model.buffers())
    if (model.local.training or any(parameter.requires_grad for parameter in model.local.parameters()) or
            any(not parameter.requires_grad for parameter in model.residual.parameters())):
        raise RuntimeError("Only the confidence residual may train; V37 must remain frozen")
    if not (component_positive.fixture and family_negative.fixture and _allow_synthetic_fixture):
        if component_positive.fixture or family_negative.fixture:
            raise RuntimeError("Fixtures cannot be mixed with authenticated sources")
        verify_v37_model(model)
    positive_features = component_positive.gpu_batch(component_indices, allow_synthetic_fixture=_allow_synthetic_fixture)
    negative_features = family_negative.gpu_batch(family_indices, allow_synthetic_fixture=_allow_synthetic_fixture)
    positive_shift = model.residual(positive_features).squeeze(-1)
    negative_shift = model.residual(negative_features).squeeze(-1)
    component_penalty = F.relu(-positive_shift).square().mean()
    family_penalty = F.relu(negative_shift).square().mean()
    total = component_weight * component_penalty + family_weight * family_penalty
    if not torch.isfinite(total):
        raise RuntimeError("Directional preservation loss is nonfinite")
    return DirectionalPenalties(total, component_penalty, family_penalty)
