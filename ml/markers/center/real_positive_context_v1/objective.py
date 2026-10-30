# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Explicit synthetic binary supervision plus positive-only real supervision."""
from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import math

import torch
from torch import Tensor
from torch.nn import functional as F


def _require_training_gpu(*tensors: Tensor) -> None:
    """Reject CPU and non-6600-XT training objectives before computing loss."""
    device = torch.device("cuda:0")
    if (not torch.cuda.is_available() or not torch.version.hip or
            any(tensor.device != device for tensor in tensors)):
        raise RuntimeError("The RX 6600 XT on cuda:0 is required; CPU training is forbidden")
    properties = torch.cuda.get_device_properties(device)
    if properties.name != "AMD Radeon RX 6600 XT" or properties.gcnArchName.split(":")[0] != "gfx1032":
        raise RuntimeError("The required RX 6600 XT gfx1032 is not cuda:0")


@dataclass(frozen=True)
class SyntheticLabels:
    """Immutable binary bytes bound to an expected SHA-256.

    The caller must independently authenticate the expected hash and source
    manifest; this validates byte integrity, not dataset provenance.
    """

    values: bytes
    sha256_hex: str

    def __post_init__(self) -> None:
        if (type(self.values) is not bytes or not self.values or
                any(value not in (0, 1) for value in self.values)):
            raise ValueError("Synthetic labels must be nonempty immutable binary bytes")
        if (not isinstance(self.sha256_hex, str) or len(self.sha256_hex) != 64 or
                any(char not in "0123456789abcdef" for char in self.sha256_hex) or
                sha256(self.values).hexdigest() != self.sha256_hex):
            raise ValueError("Synthetic label hash does not match")


def mixed_confidence_loss(synthetic_logits: Tensor, synthetic_labels: SyntheticLabels,
                          real_center_logits: Tensor, *, real_weight: float) -> Tensor:
    """Return synthetic BCE plus a weighted positive-only real-center loss.

    No real target or unlabeled-real input is accepted. The caller must ensure
    every real logit belongs to an authenticated labeled center, and every
    synthetic label/hash comes from the authorized frozen training manifest.
    """
    if not isinstance(synthetic_labels, SyntheticLabels):
        raise TypeError("Synthetic labels must have a verified byte hash")
    _require_training_gpu(synthetic_logits, real_center_logits)
    for logits in (synthetic_logits, real_center_logits):
        if logits.ndim != 1 or logits.numel() == 0 or not logits.is_floating_point():
            raise ValueError("Confidence logits must be nonempty floating-point vectors")
        if not torch.isfinite(logits).all():
            raise ValueError("Confidence logits must be finite")
    if synthetic_logits.shape[0] != len(synthetic_labels.values):
        raise ValueError("Synthetic label count does not match logits")
    if synthetic_logits.device != real_center_logits.device:
        raise ValueError("Synthetic and real logits must use the same device")
    if isinstance(real_weight, bool) or not math.isfinite(real_weight) or real_weight <= 0:
        raise ValueError("Real positive weight must be finite and positive")
    targets = torch.tensor(list(synthetic_labels.values), device=synthetic_logits.device,
                           dtype=synthetic_logits.dtype)
    synthetic = F.binary_cross_entropy_with_logits(synthetic_logits, targets)
    real_positive = F.softplus(-real_center_logits).mean()
    return synthetic + real_weight * real_positive
