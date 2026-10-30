# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""A zero-initialized confidence residual around a frozen 33-pixel model."""
from __future__ import annotations

import torch
from torch import Tensor, nn

from ..confidence_spatial_v35.model import SpatialCenterNet, frozen_spatial_features


OPERATING_THRESHOLD = 0.1


class RealPositiveCenterNet(nn.Module):
    """Keep the supplied local model immutable and change confidence only.

    The caller must bind the supplied weights to the authorized V37 checkpoint.
    Construction alone does not verify source identity or approve this model.
    Inputs are genuine runtime-bound 33-pixel, three-channel patches.
    """

    def __init__(self, local: SpatialCenterNet) -> None:
        super().__init__()
        if not isinstance(local, SpatialCenterNet):
            raise TypeError("The retained model must be a SpatialCenterNet")
        self.local = local.eval().requires_grad_(False)
        projection = local.confidence.projection
        self.residual = nn.Linear(projection.in_features, 1,
                                  device=projection.weight.device,
                                  dtype=projection.weight.dtype)
        nn.init.zeros_(self.residual.weight)
        nn.init.zeros_(self.residual.bias)
        self.eval()

    def train(self, mode: bool = True):
        super().train(mode)
        self.local.eval()
        return self

    def logits_and_geometry(self, patches: Tensor) -> tuple[Tensor, Tensor]:
        if patches.ndim != 4 or patches.shape[1:] != (3, 33, 33):
            raise ValueError("Expected runtime-channel patches of shape [N,3,33,33]")
        with torch.no_grad():
            spatial = frozen_spatial_features(self.local.base, patches)
            raw = self.local.base.head[3](self.local.base.head[2](self.local.base.head[1](spatial)))
            retained_logit = self.local.confidence(spatial)
            geometry = torch.cat((torch.tanh(raw[:, 1:3]) * .75,
                                  2.5 + torch.sigmoid(raw[:, 3:4]) * 5.5), dim=1)
        return retained_logit + self.residual(spatial).squeeze(-1), geometry

    def forward(self, patches: Tensor) -> Tensor:
        logit, geometry = self.logits_and_geometry(patches)
        return torch.cat((torch.sigmoid(logit).unsqueeze(-1), geometry), dim=1)
