# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fixed synthetic-only BCE plus pairwise confidence ranking objective.

This module is math-only. It does not load data, select a checkpoint, create an
optimizer, or authorize a training run. Its caller must authenticate synthetic
binary labels before calling it.
"""
from __future__ import annotations

import torch
from torch import Tensor
from torch.nn import functional as F


PAIRWISE_MARGIN_LOGITS = 1.0


def synthetic_confidence_loss(logits: Tensor, labels: Tensor, group_ids: Tensor) -> Tensor:
    """Return BCE plus within-report-group positive-vs-negative ranking.

    Every label must be an authenticated synthetic target. Real unlabeled or
    real positive samples are deliberately not accepted by this interface.
    Group IDs must come from the authenticated training report's ordered
    ``scope:transform`` row groups. Pairs never cross those groups. Batches
    without an in-group positive/negative pair retain BCE only.
    """
    if not all(isinstance(value, Tensor) for value in (logits, labels, group_ids)):
        raise TypeError("logits, synthetic labels and group IDs must be tensors")
    if (logits.ndim != 1 or labels.ndim != 1 or logits.numel() == 0 or
            group_ids.ndim != 1 or logits.numel() != labels.numel() or
            logits.numel() != group_ids.numel() or not logits.is_floating_point()):
        raise ValueError("logits, labels and group IDs must be equal-length vectors")
    if labels.is_complex():
        raise ValueError("synthetic labels must use a real binary dtype")
    if labels.requires_grad:
        raise ValueError("synthetic labels must not require gradients")
    if group_ids.is_floating_point() or group_ids.is_complex() or group_ids.dtype == torch.bool:
        raise ValueError("group IDs must use an integer dtype")
    if logits.device != labels.device or logits.device != group_ids.device:
        raise ValueError("logits, labels and group IDs must be on the same device")
    if not torch.isfinite(logits).all():
        raise ValueError("logits must be finite")
    if not torch.all((labels == 0) | (labels == 1)):
        raise ValueError("synthetic labels must be binary")
    if not torch.all(group_ids >= 0):
        raise ValueError("group IDs must be nonnegative")

    targets = labels.to(dtype=logits.dtype)
    bce = F.binary_cross_entropy_with_logits(logits, targets)
    positive = labels == 1
    negative = labels == 0
    pair_mask = (
        (group_ids[:, None] == group_ids[None, :]) &
        positive[:, None] & negative[None, :]
    )
    if not pair_mask.any():
        return bce

    margins = logits[:, None] - logits[None, :]
    pairwise = F.softplus(PAIRWISE_MARGIN_LOGITS - margins)[pair_mask].mean()
    return bce + pairwise
