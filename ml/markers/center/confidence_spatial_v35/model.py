# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Adapt confidence before spatial compression without moving retained geometry."""
from __future__ import annotations

from collections.abc import Mapping
import copy

import torch
from torch import Tensor, nn

from ..scale_classifier_v16.model import ScaleClassifierNet

SPATIAL_FEATURES = 768
HIDDEN_FEATURES = 64
TRAINABLE_PARAMETERS = 49281


def frozen_spatial_features(base: ScaleClassifierNet, patches: Tensor) -> Tensor:
    if patches.ndim != 4 or patches.shape[1:] != (3, 33, 33):
        raise ValueError("SpatialCenterNet requires NCHW [candidate_count,3,33,33] patches")
    return base.head[0](torch.cat((base.ink_tower(patches[:, 0:1]), base.mask_tower(patches[:, 1:3])), dim=1))


class ConfidenceProjection(nn.Module):
    """Independent copy of the retained projection and confidence output row."""

    def __init__(self, base: ScaleClassifierNet) -> None:
        super().__init__()
        projection, output = base.head[1], base.head[3]
        if (not isinstance(projection, nn.Linear) or projection.weight.shape != (64, 768) or
                not isinstance(output, nn.Linear) or output.weight.shape != (4, 64) or output.bias.shape != (4,)):
            raise ValueError("The retained spatial projection contract changed")
        self.projection = copy.deepcopy(projection).requires_grad_(True)
        self.activation = nn.SiLU()
        self.output = nn.Linear(64, 1, device=output.weight.device, dtype=output.weight.dtype)
        with torch.no_grad():
            self.output.weight.copy_(output.weight[0:1])
            self.output.bias.copy_(output.bias[0:1])

    def forward(self, spatial: Tensor) -> Tensor:
        return self.output(self.activation(self.projection(spatial))).squeeze(-1)


class SpatialCenterNet(nn.Module):
    def __init__(self, base: ScaleClassifierNet, confidence: ConfidenceProjection | None = None) -> None:
        super().__init__()
        self.base = base.eval().requires_grad_(False)
        self.confidence = confidence if confidence is not None else ConfidenceProjection(base)
        self.train(False)
        self.verify_frozen_state({name: value.detach().clone() for name, value in base.state_dict().items()})

    def train(self, mode: bool = True):
        super().train(mode)
        self.base.eval()
        return self

    def forward(self, patches: Tensor) -> Tensor:
        # Both branches share one convolution pass, but only confidence adapts.
        spatial = frozen_spatial_features(self.base, patches)
        raw = self.base.head[3](self.base.head[2](self.base.head[1](spatial)))
        confidence = self.confidence(spatial).unsqueeze(-1)
        return torch.cat((torch.sigmoid(confidence), torch.tanh(raw[:, 1:3]) * .75,
                          2.5 + torch.sigmoid(raw[:, 3:4]) * 5.5), dim=1)

    def verify_frozen_state(self, original: Mapping[str, Tensor]) -> None:
        current = self.base.state_dict()
        if current.keys() != original.keys():
            raise ValueError("Retained state membership changed")
        if any(module.training for module in self.base.modules()) or any(p.requires_grad for p in self.base.parameters()):
            raise ValueError("All retained layers must remain frozen")
        for name, value in current.items():
            if value.dtype != original[name].dtype or not torch.equal(value, original[name]):
                raise ValueError(f"Retained state changed: {name}")
        if (sum(p.numel() for p in self.confidence.parameters()) != TRAINABLE_PARAMETERS or
                not all(p.requires_grad for p in self.confidence.parameters())):
            raise ValueError("Confidence projection parameter budget changed")
        if any(not torch.isfinite(value).all() for value in self.confidence.state_dict().values()):
            raise ValueError("Confidence projection contains non-finite state")

    def export_contract(self) -> dict[str, object]:
        contract = self.base.export_contract()
        contract["architecture"] = "frozen-scale-separated-cnn-with-confidence-projection-v35"
        contract["confidence_projection"] = {"input_features": SPATIAL_FEATURES, "hidden_features": HIDDEN_FEATURES,
            "activation": "SiLU", "trainable_parameters": TRAINABLE_PARAMETERS,
            "retained_model_frozen": True, "initializer": "retained_projection_and_confidence_row"}
        return contract
