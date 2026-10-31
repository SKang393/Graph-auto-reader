# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bounded synthetic RX 6600 XT DB-head forward/backward compatibility probe.

No optimizer, training corpus, candidate, checkpoint, or approval is created.
"""
from __future__ import annotations

import json
import numpy as np
import torch

from ml.ocr.official_bakeoff.frozen_trunk_head import (
    FEATURE_CHANNELS, FROZEN_BATCH_NORM_SHAPES, TRAINABLE_CONSTANT_SHAPES, FrozenDbHead,
)


def require_gpu(*tensors: torch.Tensor) -> None:
    device = torch.device("cuda:0")
    if not torch.cuda.is_available() or not torch.version.hip:
        raise RuntimeError("RX 6600 XT HIP cuda:0 is required; CPU fallback forbidden")
    props = torch.cuda.get_device_properties(device)
    if (props.name != "AMD Radeon RX 6600 XT" or props.gcnArchName.split(":")[0] != "gfx1032" or
            any(not isinstance(tensor, torch.Tensor) or tensor.device != device for tensor in tensors)):
        raise RuntimeError("Probe tensors and model must reside on RX 6600 XT cuda:0")


def synthetic_head() -> FrozenDbHead:
    """Construct shape-correct synthetic constants, without reading a model."""
    shapes = {**TRAINABLE_CONSTANT_SHAPES, **FROZEN_BATCH_NORM_SHAPES}
    constants = {}
    for name, shape in shapes.items():
        value = 1.0 if name.endswith(".w_0") and "batch_norm" in name else 0.01
        if name.endswith(".w_2"):
            value = 1.0
        constants[name] = np.full(shape, value, dtype=np.float32)
    return FrozenDbHead(constants)


def run_probe() -> dict:
    require_gpu()
    head = synthetic_head().to("cuda:0").eval()
    features = torch.full((1, FEATURE_CHANNELS, 4, 4), 0.25,
                          dtype=torch.float32, device="cuda:0", requires_grad=True)
    parameters, buffers = tuple(head.parameters()), tuple(head.buffers())
    require_gpu(features, *parameters, *buffers)
    if len(parameters) != 9 or len(buffers) != 4:
        raise RuntimeError("DB head membership differs")
    before = [item.detach().clone() for item in parameters]
    torch.cuda.synchronize(torch.device("cuda:0"))
    baseline = torch.cuda.memory_allocated(torch.device("cuda:0"))
    torch.cuda.reset_peak_memory_stats(torch.device("cuda:0"))
    output = head.forward_logits(features)
    require_gpu(output)
    if tuple(output.shape) != (1, 1, 16, 16) or not torch.isfinite(output).all():
        raise RuntimeError("Synthetic DB logits are invalid")
    output.mean().backward()
    gradients = tuple(item.grad for item in parameters)
    if features.grad is None or any(item is None for item in gradients):
        raise RuntimeError("Synthetic backward did not produce all gradients")
    require_gpu(features.grad, *gradients)
    if (not all(torch.isfinite(item).all().item() for item in (features.grad, *gradients)) or
            not any(torch.count_nonzero(item).item() for item in gradients) or
            any(not torch.equal(item, original) for item, original in zip(parameters, before))):
        raise RuntimeError("Synthetic gradients or unchanged parameters failed validation")
    torch.cuda.synchronize(torch.device("cuda:0"))
    return {"status": "pass", "device": "AMD Radeon RX 6600 XT", "architecture": "gfx1032",
            "torch_version": str(torch.__version__), "hip_version": str(torch.version.hip),
            "feature_shape": list(features.shape), "logit_shape": list(output.shape),
            "head_parameters": len(parameters), "frozen_buffers": len(buffers),
            "peak_additional_allocated_bytes": max(0, torch.cuda.max_memory_allocated(torch.device("cuda:0")) - baseline),
            "optimizer_steps": 0, "training_inputs_read": False, "production_approved": False}


def main() -> None:
    print(json.dumps(run_probe(), sort_keys=True))


if __name__ == "__main__":
    main()
