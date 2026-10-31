# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Unregistered, intentionally non-executable GPU-only training foundation.

Source-only candidate export, per-panel feature parity and independently bound
training admission are pending. This module never constructs a model or runs an
optimizer. Checkpoint and run-journal integration must be added with admission.
"""

def require_rx6600xt(*tensors):
    """Use the existing HIP device guard and reject nonzero active device."""
    import torch
    from ml.ocr.gpu_db_head_probe_v1.probe import require_gpu
    require_gpu(*tensors)
    if torch.cuda.device_count() < 1 or torch.cuda.current_device() != 0:
        raise RuntimeError('RX 6600 XT must be active cuda:0')

def verify_gpu_state(model, features, targets, logits, loss, optimizer, *, gradients_required):
    """Validate supplied state; this function does not run an optimizer."""
    import torch
    if not isinstance(model, torch.nn.Module) or type(optimizer) is not torch.optim.AdamW:
        raise ValueError('Expected model and AdamW')
    parameters = tuple(model.parameters())
    if not parameters or len(optimizer.param_groups) != 1:
        raise ValueError('Invalid model or optimizer groups')
    group = optimizer.param_groups[0]
    if group.get('capturable') is not True or group.get('foreach') is not False or group.get('fused') is not False:
        raise ValueError('Expected GPU-capturable AdamW')
    expected = [id(p) for p in parameters if p.requires_grad]
    actual = [id(p) for p in group['params']]
    if not expected or len(actual) != len(expected) or set(actual) != set(expected):
        raise ValueError('Optimizer parameter membership differs')
    gradients = [p.grad for p in parameters if p.requires_grad]
    if gradients_required and any(g is None for g in gradients):
        raise ValueError('Missing gradient')
    state = [v for item in optimizer.state.values() for v in item.values() if isinstance(v, torch.Tensor)]
    values = [features, targets, logits, loss, *parameters, *model.buffers(), *state, *(g for g in gradients if g is not None)]
    require_rx6600xt(*values)
    if loss.numel() != 1 or any(not bool(torch.isfinite(v).all().item()) for v in values):
        raise ValueError('GPU value is not finite')

def run_training(*args, **kwargs):
    """Refuse before model creation until an actual reviewed admission exists."""
    raise RuntimeError('BLOCKED: source-bound production panel export, per-panel feature parity, and canonical training admission are not implemented')
