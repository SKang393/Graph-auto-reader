# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Equal per-row log loss on the complete admitted training population."""
from __future__ import annotations

import torch
from torch import Tensor
from torch.nn import functional


def confidence_loss(logits: Tensor, labels: Tensor) -> Tensor:
    if (logits.ndim != 1 or logits.shape != labels.shape or logits.numel() == 0 or
            not logits.is_floating_point() or labels.dtype != logits.dtype or
            logits.device != labels.device or not torch.isfinite(logits).all() or
            not torch.isfinite(labels).all() or not ((labels == 0) | (labels == 1)).all()):
        raise ValueError("Confidence loss requires finite equal-length logits and binary labels")
    return functional.binary_cross_entropy_with_logits(logits, labels)
