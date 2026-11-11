# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""RX 6600 XT backend for the fixed extended DB-head recipe.

The caller owns input provenance, ONNX extraction, parity and eventual export.
This module neither authorizes a training run nor performs one on import.
"""
from __future__ import annotations

import math
from typing import Callable, Sequence

import numpy as np
import torch

from ml.ocr.official_bakeoff import extended_head_training as extended
from ml.ocr.official_bakeoff import frozen_head_training as base
from ml.ocr.official_bakeoff import frozen_trunk_head

DEVICE = torch.device("cuda:0")
BACKEND = "rocm-rx6600xt-cuda0-float64-conv0"


def require_device() -> torch.device:
    if not torch.version.hip or not torch.cuda.is_available():
        raise base.FrozenHeadTrainingError("RX 6600 XT ROCm cuda:0 is required; CPU fallback is forbidden")
    if torch.cuda.get_device_name(0) != "AMD Radeon RX 6600 XT":
        raise base.FrozenHeadTrainingError("cuda:0 must be AMD Radeon RX 6600 XT")
    return DEVICE


def _samples(panels: Sequence[base.TrainingPanel]) -> tuple[base._Sample, ...]:
    if not panels or any(not isinstance(p, base.TrainingPanel) for p in panels):
        raise base.FrozenHeadTrainingError("At least one valid train panel is required")
    if any(p.split != "train" for p in panels):
        raise base.FrozenHeadTrainingError("Development or validation panels cannot enter optimization")
    ids = [p.panel_id for p in panels]
    if any(not isinstance(i, str) or not i for i in ids) or len(ids) != len(set(ids)):
        raise base.FrozenHeadTrainingError("Train panel identities must be non-empty and unique")
    samples = []
    for panel in sorted(panels, key=lambda p: p.panel_id):
        f = base._immutable_array(panel.features, "features", binary=False)
        t = base._immutable_array(panel.target, "target", binary=True)
        m = base._immutable_array(panel.mask, "mask", binary=True)
        if f.ndim != 4 or f.shape[:2] != (1, frozen_trunk_head.FEATURE_CHANNELS) or min(f.shape[2:]) <= 0:
            raise base.FrozenHeadTrainingError("Features must be batch-one NCHW with 96 channels")
        if t.shape != (1, f.shape[2] * 4, f.shape[3] * 4) or m.shape != t.shape:
            raise base.FrozenHeadTrainingError("Target and mask must match the DB head output shape")
        if not np.any(m):
            raise base.FrozenHeadTrainingError("An all-zero supervision mask cannot enter optimization")
        # Explicit copies create device-owned tensors without retaining CPU tensor aliases.
        samples.append(base._Sample(panel.panel_id, torch.tensor(f, device=DEVICE),
            torch.tensor(t, device=DEVICE).unsqueeze(0), torch.tensor(m, device=DEVICE).unsqueeze(0), bool(np.any(t))))
    return tuple(samples)


def _assert_residency(head, samples, optimizer=None, *, gradients=False):
    tensors = list(head.parameters()) + list(head.buffers())
    tensors += [v for sample in samples for v in (sample.features, sample.target, sample.mask)]
    if gradients:
        tensors += [p.grad for p in head.parameters()]
    if optimizer is not None:
        tensors += [v for state in optimizer.state.values() for v in state.values() if isinstance(v, torch.Tensor)]
    if any(v is None or v.device != DEVICE for v in tensors):
        raise base.FrozenHeadTrainingError("DB head, inputs, gradients and optimizer state must remain on cuda:0")


def _optimizer(head):
    optimizer = torch.optim.AdamW(tuple(head.parameters()), lr=base.LEARNING_RATE,
                                  weight_decay=base.WEIGHT_DECAY, foreach=False, capturable=True)
    # AdamW otherwise lazily creates its step counter on CPU on some backends.
    for p in head.parameters():
        state = optimizer.state[p]
        state["step"] = torch.zeros((), dtype=torch.float32, device=DEVICE)
        state["exp_avg"] = torch.zeros_like(p)
        state["exp_avg_sq"] = torch.zeros_like(p)
    return optimizer


def train_extended_head_gpu(head: frozen_trunk_head.FrozenDbHead, panels: Sequence[base.TrainingPanel], *,
                            cancellation_check: Callable[[], None] | None = None) -> extended.TrainingResult:
    """Run the existing 240-epoch, earliest minimum full-train-loss recipe on GPU."""
    require_device()
    if not isinstance(head, frozen_trunk_head.FrozenDbHead):
        raise base.FrozenHeadTrainingError("Training requires the reviewed FrozenDbHead")
    base._validate_head(head)
    samples = _samples(panels)
    head.to(DEVICE)
    original_state = base._clone_state(head)
    original_training = head.training
    previous_threads = torch.get_num_threads()
    previous_deterministic = torch.are_deterministic_algorithms_enabled()
    try:
        base._check_cancellation(cancellation_check)
        torch.set_num_threads(extended.INTRAOP_THREADS)
        torch.use_deterministic_algorithms(True)
        head.eval()
        before = base._frozen_bn_sha256(head)
        optimizer = _optimizer(head)
        _assert_residency(head, samples, optimizer)
        generator = torch.Generator(device="cpu").manual_seed(base.SEED)
        epochs = []
        best, best_state, selected, steps = math.inf, None, 0, 0
        for epoch in range(extended.EPOCHS):
            base._check_cancellation(cancellation_check)
            order = torch.randperm(len(samples), generator=generator).tolist()
            losses = []
            for index in order:
                base._check_cancellation(cancellation_check)
                sample = samples[index]
                optimizer.zero_grad(set_to_none=True)
                loss, _ = base.masked_db_dice_loss(head.forward_logits(sample.features), sample.target, sample.mask)
                base._require_finite_scalar(loss, "Training loss")
                loss.backward()
                base._validate_gradients(head)
                _assert_residency(head, samples, optimizer, gradients=True)
                optimizer.step()
                _assert_residency(head, samples, optimizer, gradients=True)
                base._validate_parameters(head)
                steps += 1
                losses.append(float(loss.detach()))
            full, positive, empty = base._full_train_losses(head, samples, cancellation_check)
            epochs.append(base.EpochLoss(epoch + 1, steps, base._mean(losses), full, positive, empty,
                base._ordered_ids_sha256([samples[i].panel_id for i in order])))
            if full < best:
                best, selected = full, epoch + 1
                best_state = {name: value.detach().clone() for name, value in head.named_parameters()}
        if best_state is None:
            raise base.FrozenHeadTrainingError("Training produced no finite checkpoint")
        with torch.no_grad():
            for name, value in head.named_parameters():
                value.copy_(best_state[name])
        after = base._frozen_bn_sha256(head)
        if before != after:
            raise base.FrozenHeadTrainingError("Frozen batch-normalization state changed")
        _assert_residency(head, samples, optimizer)
        return extended.TrainingResult(steps, selected, extended.EPOCHS, tuple(epochs), len(samples),
            sum(s.has_positive for s in samples), sum(not s.has_positive for s in samples), base.SEED,
            base.BATCH_SIZE, base.LEARNING_RATE, base.WEIGHT_DECAY, base.DICE_EPSILON, BACKEND,
            base.TRAINABLE_PARAMETER_NAMES, before, after, base._trainable_state_sha256(head))
    except Exception:
        head.load_state_dict(original_state, strict=True)
        head.train(original_training)
        raise
    finally:
        torch.use_deterministic_algorithms(previous_deterministic)
        torch.set_num_threads(previous_threads)
