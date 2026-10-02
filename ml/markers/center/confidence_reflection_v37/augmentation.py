# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Reflect all input planes together about the unchanged patch anchor."""
from __future__ import annotations

import torch
from torch import Tensor

TRANSFORMS = ('identity', 'horizontal', 'vertical', 'both')


def reflect_patches(patches: Tensor, transform: str) -> Tensor:
    if transform not in TRANSFORMS:
        raise ValueError('Unknown fixed reflection')
    if (patches.ndim != 4 or patches.shape[1:] != (3, 33, 33) or
            patches.dtype != torch.float32 or not torch.isfinite(patches).all() or
            (patches < 0).any() or (patches > 1).any()):
        raise ValueError('Reflection requires finite float32 NCHW [N,3,33,33] probabilities')
    dimensions = {'identity': (), 'horizontal': (-1,), 'vertical': (-2,), 'both': (-2, -1)}[transform]
    return torch.flip(patches, dimensions) if dimensions else patches.clone()
