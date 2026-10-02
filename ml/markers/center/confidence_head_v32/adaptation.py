# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Adapt confidence without changing any feature or geometry parameter."""
from __future__ import annotations

from collections.abc import Mapping

import torch
from torch import Tensor, nn
from torch.nn import functional

from ..scale_classifier_v16.model import ScaleClassifierNet


def _final_layer(model: ScaleClassifierNet) -> nn.Linear:
    layer = model.head[3]
    if not isinstance(layer, nn.Linear) or layer.weight.shape != (4, 64) or layer.bias.shape != (4,):
        raise ValueError("The retained four-output, 64-feature contract changed")
    return layer


class ConfidenceHead(nn.Module):
    def __init__(self, model: ScaleClassifierNet) -> None:
        super().__init__()
        layer = _final_layer(model)
        self.weight = nn.Parameter(layer.weight[0:1].detach().clone())
        self.bias = nn.Parameter(layer.bias[0:1].detach().clone())

    def forward(self, features: Tensor) -> Tensor:
        return functional.linear(features, self.weight, self.bias).squeeze(-1)


def freeze_for_confidence_adaptation(model: ScaleClassifierNet) -> ConfidenceHead:
    model.eval()
    model.requires_grad_(False)
    return ConfidenceHead(model)


def frozen_features(model: ScaleClassifierNet, patches: Tensor) -> Tensor:
    if any(module.training for module in model.modules()) or any(p.requires_grad for p in model.parameters()):
        raise ValueError("Feature layers and normalization buffers must remain frozen")
    if patches.ndim != 4 or patches.shape[1:] != (3, 33, 33):
        raise ValueError("Expected native three-channel 33-pixel patches")
    with torch.no_grad():
        combined = torch.cat((model.ink_tower(patches[:, 0:1]), model.mask_tower(patches[:, 1:3])), dim=1)
        return model.head[:3](combined).detach()


def install_confidence_head(model: ScaleClassifierNet, head: ConfidenceHead) -> None:
    layer = _final_layer(model)
    if (head.weight.shape != (1, 64) or head.bias.shape != (1,) or
            head.weight.dtype != layer.weight.dtype or head.bias.dtype != layer.bias.dtype or
            head.weight.device != layer.weight.device or head.bias.device != layer.bias.device or
            not torch.isfinite(head.weight).all() or not torch.isfinite(head.bias).all()):
        raise ValueError("Confidence parameters do not match the retained finite head contract")
    with torch.no_grad():
        layer.weight[0:1].copy_(head.weight)
        layer.bias[0:1].copy_(head.bias)


def verify_frozen_geometry(model: ScaleClassifierNet, original: Mapping[str, Tensor]) -> None:
    """Check every feature/normalization tensor and the three geometry rows."""
    current = model.state_dict()
    if current.keys() != original.keys():
        raise ValueError("Model state membership changed")
    for name, value in current.items():
        before = original[name]
        if value.shape != before.shape or value.dtype != before.dtype:
            raise ValueError(f"Frozen state contract changed: {name}")
        if name in ("head.3.weight", "head.3.bias"):
            value, before = value[1:], before[1:]
        if not torch.equal(value, before):
            raise ValueError(f"Frozen feature or geometry state changed: {name}")
