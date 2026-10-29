# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Add fixed-reflection negative features without modifying retained rows."""
from __future__ import annotations

import torch

from ..confidence_reflection_v37.augmentation import TRANSFORMS, reflect_patches
from ..confidence_spatial_v35.model import frozen_spatial_features


def append_residual_features(training, payload, base, budget):
    """Append four fixed views of validated train-only background patches."""
    features, labels, hard = training
    patches = payload["patches"]
    negative_labels = payload["labels"]
    negative_hard = payload["hard_negative"]
    count = len(negative_labels)
    if (patches.shape != (count, 3, 33, 33) or patches.dtype != torch.float32 or
            negative_labels.shape != (count,) or negative_labels.dtype != torch.float32 or
            negative_hard.shape != (count,) or negative_hard.dtype != torch.float32 or
            not torch.isfinite(patches).all() or (patches < 0).any() or (patches > 1).any() or
            not torch.equal(negative_labels, torch.zeros_like(negative_labels)) or
            not torch.equal(negative_hard, torch.ones_like(negative_hard))):
        raise ValueError("Residual training payload is not a valid negative patch population")
    if (features.ndim != 2 or features.shape[1] != 768 or labels.shape != (len(features),) or
            hard.shape != (len(features),) or not count):
        raise ValueError("Retained training feature population changed")
    if base.training or any(parameter.requires_grad for parameter in base.parameters()):
        raise ValueError("Residual features require a frozen evaluation-mode base")
    new_features = []
    for transform in TRANSFORMS:
        with budget.work_block(), torch.no_grad():
            transformed = reflect_patches(patches, transform)
            value = frozen_spatial_features(base, transformed)
            if value.shape != (count, 768) or not torch.isfinite(value).all():
                raise ValueError("Residual spatial features changed")
            new_features.append(value)
    with budget.work_block():
        return (torch.cat((features, *new_features)),
                torch.cat((labels, *(negative_labels for _ in TRANSFORMS))),
                torch.cat((hard, *(negative_hard for _ in TRANSFORMS))))
