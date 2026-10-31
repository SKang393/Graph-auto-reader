# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Exploratory frozen-V37 objective; no training admission or optimizer."""
from __future__ import annotations

from dataclasses import dataclass

import torch
from torch import Tensor
from torch.nn import functional as F

from ..real_positive_context_v1.model import RealPositiveCenterNet
from ..real_positive_context_v1.objective import SyntheticLabels, _require_training_gpu, mixed_confidence_loss
from .component_source import ComponentNegativeStore, verify_v37_model

# Fixed for this unregistered exploratory foundation, not a tuned result.
REAL_POSITIVE_WEIGHT = 0.25
COMPONENT_RETENTION_WEIGHT = 1.0


@dataclass(frozen=True)
class PreservationLoss:
    total: Tensor
    mixed: Tensor
    component_upward_penalty: Tensor


def preservation_loss(model: RealPositiveCenterNet, synthetic: Tensor, labels: SyntheticLabels,
                      real_positive: Tensor, component: ComponentNegativeStore, indices: tuple[int, ...], *,
                      _allow_synthetic_fixture: bool = False) -> PreservationLoss:
    """Penalize only upward logit change on bound component-train negatives.

    Spatial features come from the frozen V37-equivalent backbone. Since the
    candidate logit is V37's logit plus the residual, the upward difference is
    exactly relu(residual(features)); geometry is never adapted. The caller is
    still responsible for registered run admission and real-positive provenance.
    """
    if not isinstance(model, RealPositiveCenterNet) or not isinstance(component, ComponentNegativeStore):
        raise TypeError("V37 residual model and component-negative source are required")
    _require_training_gpu(synthetic, real_positive, *model.parameters(), *model.buffers())
    if (model.local.training or any(parameter.requires_grad for parameter in model.local.parameters()) or
            any(not parameter.requires_grad for parameter in model.residual.parameters())):
        raise RuntimeError("Only the residual may train; the V37 local model must stay frozen")
    for patches in (synthetic, real_positive):
        if (patches.dtype != torch.float32 or patches.ndim != 4 or patches.shape[1:] != (3, 33, 33) or
                patches.shape[0] == 0 or not torch.isfinite(patches).all() or
                (patches < 0).any() or (patches > 1).any()):
            raise ValueError("GPU patches must be finite float32 runtime-channel data")
    if not component.fixture:
        verify_v37_model(model)
    negative_features = component.gpu_batch(indices, allow_synthetic_fixture=_allow_synthetic_fixture)
    if negative_features.requires_grad:
        raise RuntimeError("Cached V37 features must remain frozen")
    synthetic_logits, _ = model.logits_and_geometry(synthetic)
    positive_logits, _ = model.logits_and_geometry(real_positive)
    mixed = mixed_confidence_loss(synthetic_logits, labels, positive_logits, real_weight=REAL_POSITIVE_WEIGHT)
    difference = model.residual(negative_features).squeeze(-1)
    retention = F.relu(difference).square().mean()
    total = mixed + COMPONENT_RETENTION_WEIGHT * retention
    if not torch.isfinite(total):
        raise RuntimeError("Preservation loss is nonfinite")
    return PreservationLoss(total, mixed, retention)
