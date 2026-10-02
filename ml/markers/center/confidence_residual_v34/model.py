# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Add confidence capacity while preserving every retained geometry parameter."""
from __future__ import annotations

from collections.abc import Mapping

import torch
from torch import Tensor, nn
from torch.nn import functional

from ..confidence_head_v32.adaptation import frozen_features
from ..scale_classifier_v16.model import ScaleClassifierNet

HIDDEN_FEATURES = 64
TRAINABLE_PARAMETERS = 4225


class ConfidenceResidual(nn.Module):
    """Frozen retained logit plus a zero-initialized 64-to-64-to-1 residual."""

    def __init__(self, base: ScaleClassifierNet) -> None:
        super().__init__()
        layer = base.head[3]
        if not isinstance(layer, nn.Linear) or layer.weight.shape != (4, 64) or layer.bias.shape != (4,):
            raise ValueError("The retained 64-feature, four-output head contract changed")
        self.register_buffer("base_weight", layer.weight[0:1].detach().clone())
        self.register_buffer("base_bias", layer.bias[0:1].detach().clone())
        self.residual = nn.Sequential(nn.Linear(64, HIDDEN_FEATURES), nn.SiLU(), nn.Linear(HIDDEN_FEATURES, 1))
        self.residual.to(device=layer.weight.device, dtype=layer.weight.dtype)
        nn.init.zeros_(self.residual[2].weight)
        nn.init.zeros_(self.residual[2].bias)

    def forward(self, features: Tensor) -> Tensor:
        baseline = functional.linear(features, self.base_weight, self.base_bias)
        return (baseline + self.residual(features)).squeeze(-1)


class ResidualCenterNet(nn.Module):
    def __init__(self, base: ScaleClassifierNet, confidence: ConfidenceResidual | None = None) -> None:
        super().__init__()
        self.base = base.eval().requires_grad_(False)
        self.confidence = confidence if confidence is not None else ConfidenceResidual(base)
        self.train(False)
        self.verify_frozen_state({name: value.detach().clone() for name, value in base.state_dict().items()})

    def train(self, mode: bool = True):
        super().train(mode)
        # Training the residual must never update retained normalization buffers.
        self.base.eval()
        return self

    def forward(self, patches: Tensor) -> Tensor:
        features = frozen_features(self.base, patches)
        raw = self.base.head[3](features)
        # Reuse the original four-column multiplication in deployed inference.
        # At initialization the added residual is exactly zero, preserving the
        # retained sigmoid result without a differently shaped multiplication.
        confidence = raw[:, 0:1] + self.confidence.residual(features)
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
        layer = self.base.head[3]
        if (not torch.equal(self.confidence.base_weight, layer.weight[0:1]) or
                not torch.equal(self.confidence.base_bias, layer.bias[0:1])):
            raise ValueError("Cached confidence source differs from retained model")
        if sum(p.numel() for p in self.confidence.parameters()) != TRAINABLE_PARAMETERS:
            raise ValueError("Confidence residual parameter count changed")
        if any(not torch.isfinite(value).all() for value in self.confidence.state_dict().values()):
            raise ValueError("Confidence residual contains non-finite state")

    def export_contract(self) -> dict[str, object]:
        contract = self.base.export_contract()
        contract["architecture"] = "frozen-scale-separated-cnn-with-confidence-residual-v34"
        contract["confidence_residual"] = {"input_features": 64, "hidden_features": HIDDEN_FEATURES,
            "activation": "SiLU", "trainable_parameters": TRAINABLE_PARAMETERS,
            "retained_model_frozen": True, "initial_residual_output": 0}
        return contract
