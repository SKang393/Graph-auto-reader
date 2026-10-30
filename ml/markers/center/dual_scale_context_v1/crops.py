# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Crop unmodified runtime input channels at existing integer proposal anchors."""
from __future__ import annotations

import torch
from torch import Tensor
import torch.nn.functional as functional


def crop_context(scene: Tensor, coordinates: Tensor) -> Tensor:
    if scene.ndim != 3 or scene.shape[0] != 3:
        raise ValueError("Expected original ink, text-mask and artifact-mask planes")
    if coordinates.ndim != 2 or coordinates.shape[1] != 2:
        raise ValueError("Expected proposal coordinates [N,2] in x,y order")
    if not torch.isfinite(scene).all() or not torch.isfinite(coordinates).all():
        raise ValueError("Input contains non-finite values")
    if not torch.equal(coordinates, coordinates.round()):
        raise ValueError("Proposal anchors must be integral")
    height, width = scene.shape[-2:]
    if (coordinates[:, 0].lt(0).any() or coordinates[:, 0].ge(width).any() or
            coordinates[:, 1].lt(0).any() or coordinates[:, 1].ge(height).any()):
        raise ValueError("Proposal anchor outside the source raster")
    if len(coordinates) == 0:
        return scene.new_empty((0, 3, 65, 65))
    padded = functional.pad(scene, (32, 32, 32, 32))
    return torch.stack([padded[:, int(y):int(y)+65, int(x):int(x)+65] for x, y in coordinates])
