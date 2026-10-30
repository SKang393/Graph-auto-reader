# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Retain the complete local predictor and add a zero-initialized context logit."""
from __future__ import annotations

import torch
from torch import Tensor, nn

from ..confidence_spatial_v35.model import SpatialCenterNet, frozen_spatial_features


class DualScaleContextNet(nn.Module):
    """Input is the original three runtime channels cropped around a proposal.

    The central 33 pixels go to the immutable local model. The extra receptive
    field can affect confidence only; it cannot alter decoded geometry.
    """

    def __init__(self, local: SpatialCenterNet) -> None:
        super().__init__()
        self.local = local.eval().requires_grad_(False)
        self.context = nn.Sequential(
            nn.Conv2d(3, 8, 5, stride=2, padding=2), nn.SiLU(),
            nn.Conv2d(8, 16, 3, stride=2, padding=1), nn.SiLU(),
            nn.Conv2d(16, 32, 3, stride=2, padding=1), nn.SiLU(),
            nn.AdaptiveAvgPool2d(1), nn.Flatten(), nn.Linear(32, 1),
        )
        nn.init.zeros_(self.context[-1].weight)
        nn.init.zeros_(self.context[-1].bias)
        self.eval()

    def train(self, mode: bool = True):
        super().train(mode)
        self.local.eval()
        return self

    def logits_and_geometry(self, context: Tensor) -> tuple[Tensor, Tensor]:
        if context.ndim != 4 or context.shape[1:] != (3, 65, 65):
            raise ValueError("Expected NCHW runtime-channel patches of shape [N,3,65,65]")
        patch = context[:, :, 16:49, 16:49]
        spatial = frozen_spatial_features(self.local.base, patch)
        raw = self.local.base.head[3](self.local.base.head[2](self.local.base.head[1](spatial)))
        logit = self.local.confidence(spatial).unsqueeze(-1) + self.context(context)
        geometry = torch.cat((torch.tanh(raw[:, 1:3]) * .75,
                              2.5 + torch.sigmoid(raw[:, 3:4]) * 5.5), dim=1)
        return logit.squeeze(-1), geometry

    def forward(self, context: Tensor) -> Tensor:
        logit, geometry = self.logits_and_geometry(context)
        return torch.cat((torch.sigmoid(logit).unsqueeze(-1), geometry), dim=1)
