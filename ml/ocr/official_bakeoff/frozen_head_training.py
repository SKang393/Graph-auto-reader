# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Deterministic train-only optimization for the reviewed frozen DB head.

Authorization, feature-cache authentication, data loading, ONNX patching, and
evaluation belong to the caller. This module accepts immutable train tensors,
updates only the nine reviewed head parameters, and leaves the head at the
minimum full-train-loss epoch.
"""

from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import math
from typing import Callable, Sequence

import numpy as np
import torch
from torch import Tensor

from ml.ocr.official_bakeoff import frozen_trunk_head


SEED = 20260939
EPOCHS = 60
BATCH_SIZE = 1
LEARNING_RATE = 1e-4
WEIGHT_DECAY = 1e-4
DICE_EPSILON = 1e-6

TRAINABLE_PARAMETER_NAMES = (
    "conv_weight", "bn0_weight", "bn0_bias", "deconv0_weight",
    "deconv0_bias", "bn1_weight", "bn1_bias", "deconv1_weight",
    "deconv1_bias",
)
FROZEN_BUFFER_NAMES = (
    "bn0_running_mean", "bn0_running_variance",
    "bn1_running_mean", "bn1_running_variance",
)


class FrozenHeadTrainingError(RuntimeError):
    """The supplied head or train tensors violate the fixed contract."""


class FrozenHeadTrainingCancelledError(FrozenHeadTrainingError):
    """A cancellation callback stopped training at a safe boundary."""


@dataclass(frozen=True)
class TrainingPanel:
    panel_id: str
    split: str
    features: np.ndarray
    target: np.ndarray
    mask: np.ndarray


@dataclass(frozen=True)
class EpochLoss:
    epoch: int
    optimizer_steps: int
    training_step_mean_loss: float
    full_train_loss: float
    full_positive_dice_loss: float | None
    full_empty_negative_loss: float | None
    draw_order_sha256: str


@dataclass(frozen=True)
class TrainingResult:
    optimizer_steps: int
    selected_epoch: int
    epochs: int
    epoch_losses: tuple[EpochLoss, ...]
    panel_count: int
    positive_panel_count: int
    empty_positive_panels: int
    seed: int
    batch_size: int
    learning_rate: float
    weight_decay: float
    dice_epsilon: float
    trainable_parameter_names: tuple[str, ...]
    frozen_batch_norm_sha256_before: str
    frozen_batch_norm_sha256_after: str
    selected_trainable_state_sha256: str


@dataclass(frozen=True)
class _Sample:
    panel_id: str
    features: Tensor
    target: Tensor
    mask: Tensor
    has_positive: bool


def masked_db_dice_loss(
    logits: Tensor,
    target: Tensor,
    mask: Tensor,
    *,
    epsilon: float = DICE_EPSILON,
) -> tuple[Tensor, str]:
    """Return the fixed objective and its positive or empty-target category."""

    _validate_loss_tensors(logits, target, mask)
    if not math.isfinite(epsilon) or epsilon <= 0:
        raise FrozenHeadTrainingError("Dice epsilon must be finite and positive")
    valid = mask.sum()
    if float(valid.detach()) <= 0:
        raise FrozenHeadTrainingError("An all-zero supervision mask cannot contribute")
    probabilities = torch.sigmoid(logits)
    masked_target = target * mask
    if float(masked_target.sum().detach()) == 0:
        # Epsilon-only Dice has negligible gradients for a large empty raster.
        return (probabilities * mask).sum() / valid, "empty_negative"
    intersection = (probabilities * masked_target).sum()
    denominator = (probabilities * mask).sum() + masked_target.sum()
    loss = 1.0 - ((2.0 * intersection + epsilon) / (denominator + epsilon))
    return loss, "positive_dice"


def train_frozen_head(
    head: frozen_trunk_head.FrozenDbHead,
    panels: Sequence[TrainingPanel],
    *,
    cancellation_check: Callable[[], None] | None = None,
) -> TrainingResult:
    """Train on canonicalized train-only inputs and restore the selected epoch."""

    if not isinstance(head, frozen_trunk_head.FrozenDbHead):
        raise FrozenHeadTrainingError("Training requires the reviewed FrozenDbHead")
    original_state = _clone_state(head)
    original_training = head.training
    previous_threads = torch.get_num_threads()
    previous_deterministic = torch.are_deterministic_algorithms_enabled()
    try:
        _validate_head(head)
        samples = _prepare_samples(panels)
        frozen_before = _frozen_bn_sha256(head)
        _check_cancellation(cancellation_check)
        torch.set_num_threads(1)
        torch.use_deterministic_algorithms(True)
        head.eval()
        optimizer = torch.optim.AdamW(
            tuple(head.parameters()),
            lr=LEARNING_RATE,
            weight_decay=WEIGHT_DECAY,
            foreach=False,
        )
        draw_generator = torch.Generator(device="cpu").manual_seed(SEED)
        epoch_losses: list[EpochLoss] = []
        best_state: dict[str, Tensor] | None = None
        best_loss = math.inf
        selected_epoch = 0
        optimizer_steps = 0

        for epoch_index in range(EPOCHS):
            _check_cancellation(cancellation_check)
            order = torch.randperm(len(samples), generator=draw_generator).tolist()
            step_losses: list[float] = []
            for sample_index in order:
                _check_cancellation(cancellation_check)
                sample = samples[sample_index]
                optimizer.zero_grad(set_to_none=True)
                loss, _ = masked_db_dice_loss(
                    head.forward_logits(sample.features), sample.target, sample.mask
                )
                _require_finite_scalar(loss, "Training loss")
                loss.backward()
                _validate_gradients(head)
                optimizer.step()
                _validate_parameters(head)
                optimizer_steps += 1
                step_losses.append(float(loss.detach()))

            full_loss, positive_loss, empty_loss = _full_train_losses(
                head, samples, cancellation_check
            )
            epoch_losses.append(
                EpochLoss(
                    epoch=epoch_index + 1,
                    optimizer_steps=optimizer_steps,
                    training_step_mean_loss=_mean(step_losses),
                    full_train_loss=full_loss,
                    full_positive_dice_loss=positive_loss,
                    full_empty_negative_loss=empty_loss,
                    draw_order_sha256=_ordered_ids_sha256(
                        [samples[index].panel_id for index in order]
                    ),
                )
            )
            if full_loss < best_loss:
                best_loss = full_loss
                selected_epoch = epoch_index + 1
                best_state = {
                    name: value.detach().clone()
                    for name, value in head.named_parameters()
                }

        if best_state is None:
            raise FrozenHeadTrainingError("Training produced no finite checkpoint")
        with torch.no_grad():
            for name, value in head.named_parameters():
                value.copy_(best_state[name])
        head.eval()
        frozen_after = _frozen_bn_sha256(head)
        if frozen_after != frozen_before:
            raise FrozenHeadTrainingError("Frozen batch-normalization state changed")
        _validate_head(head)
        return TrainingResult(
            optimizer_steps=optimizer_steps,
            selected_epoch=selected_epoch,
            epochs=EPOCHS,
            epoch_losses=tuple(epoch_losses),
            panel_count=len(samples),
            positive_panel_count=sum(sample.has_positive for sample in samples),
            empty_positive_panels=sum(not sample.has_positive for sample in samples),
            seed=SEED,
            batch_size=BATCH_SIZE,
            learning_rate=LEARNING_RATE,
            weight_decay=WEIGHT_DECAY,
            dice_epsilon=DICE_EPSILON,
            trainable_parameter_names=TRAINABLE_PARAMETER_NAMES,
            frozen_batch_norm_sha256_before=frozen_before,
            frozen_batch_norm_sha256_after=frozen_after,
            selected_trainable_state_sha256=_trainable_state_sha256(head),
        )
    except Exception:
        head.load_state_dict(original_state, strict=True)
        head.train(original_training)
        raise
    finally:
        torch.use_deterministic_algorithms(previous_deterministic)
        torch.set_num_threads(previous_threads)


def _prepare_samples(panels: Sequence[TrainingPanel]) -> tuple[_Sample, ...]:
    if not panels:
        raise FrozenHeadTrainingError("At least one train panel is required")
    if any(not isinstance(panel, TrainingPanel) for panel in panels):
        raise FrozenHeadTrainingError("Every input must be a TrainingPanel")
    if any(panel.split != "train" for panel in panels):
        raise FrozenHeadTrainingError("Development or validation panels cannot enter optimization")
    panel_ids = [panel.panel_id for panel in panels]
    if any(not isinstance(value, str) or not value for value in panel_ids):
        raise FrozenHeadTrainingError("Every train panel requires a non-empty identity")
    if len(panel_ids) != len(set(panel_ids)):
        raise FrozenHeadTrainingError("Train panel identities must be unique")

    samples: list[_Sample] = []
    for panel in sorted(panels, key=lambda item: item.panel_id):
        features = _immutable_array(panel.features, "features", binary=False)
        target = _immutable_array(panel.target, "target", binary=True)
        mask = _immutable_array(panel.mask, "mask", binary=True)
        if features.ndim != 4 or features.shape[0] != 1 or features.shape[1] != frozen_trunk_head.FEATURE_CHANNELS:
            raise FrozenHeadTrainingError("Features must be batch-one NCHW with 96 channels")
        if features.shape[2] <= 0 or features.shape[3] <= 0:
            raise FrozenHeadTrainingError("Feature spatial dimensions must be positive")
        expected = (1, features.shape[2] * 4, features.shape[3] * 4)
        if target.shape != expected or mask.shape != expected:
            raise FrozenHeadTrainingError("Target and mask must match the DB head output shape")
        if np.any((target > 0) & (mask <= 0)):
            raise FrozenHeadTrainingError("Positive targets cannot lie outside supervision")
        if not np.any(mask > 0):
            raise FrozenHeadTrainingError("An all-zero supervision mask cannot enter optimization")
        samples.append(
            _Sample(
                panel.panel_id,
                torch.from_numpy(np.array(features, copy=True)),
                torch.from_numpy(np.array(target, copy=True)).unsqueeze(0),
                torch.from_numpy(np.array(mask, copy=True)).unsqueeze(0),
                bool(np.any(target > 0)),
            )
        )
    return tuple(samples)


def _immutable_array(value: np.ndarray, label: str, *, binary: bool) -> np.ndarray:
    valid = (
        isinstance(value, np.ndarray)
        and value.dtype == np.float32
        and value.flags.c_contiguous
        and not value.flags.writeable
        and np.isfinite(value).all()
    )
    if not valid or (binary and np.any((value != 0) & (value != 1))):
        kind = "immutable binary" if binary else "immutable finite"
        raise FrozenHeadTrainingError(f"Panel {label} must be {kind} contiguous float32")
    return value


def _validate_loss_tensors(logits: Tensor, target: Tensor, mask: Tensor) -> None:
    if logits.shape != target.shape or target.shape != mask.shape or logits.ndim != 4:
        raise FrozenHeadTrainingError("Logits, target, and mask must have matching NCHW shapes")
    if any(value.dtype != torch.float32 for value in (logits, target, mask)):
        raise FrozenHeadTrainingError("DB loss requires float32 tensors")
    if not all(bool(torch.isfinite(value).all()) for value in (logits, target, mask)):
        raise FrozenHeadTrainingError("DB loss tensors must be finite")
    if not bool(torch.all((target == 0) | (target == 1))) or not bool(torch.all((mask == 0) | (mask == 1))):
        raise FrozenHeadTrainingError("DB targets and masks must be binary")
    if bool(torch.any((target > 0) & (mask <= 0))):
        raise FrozenHeadTrainingError("Positive targets cannot lie outside supervision")


def _validate_head(head: frozen_trunk_head.FrozenDbHead) -> None:
    parameters = dict(head.named_parameters())
    if tuple(parameters) != TRAINABLE_PARAMETER_NAMES:
        raise FrozenHeadTrainingError("Trainable DB head parameter inventory changed")
    for (name, parameter), shape in zip(
        parameters.items(), frozen_trunk_head.TRAINABLE_CONSTANT_SHAPES.values(), strict=True
    ):
        if tuple(parameter.shape) != shape or parameter.dtype != torch.float32 or parameter.device.type != "cpu" or not parameter.requires_grad or not bool(torch.isfinite(parameter).all()):
            raise FrozenHeadTrainingError(f"Trainable DB head parameter is invalid: {name}")
    buffers = dict(head.named_buffers())
    if tuple(buffers) != FROZEN_BUFFER_NAMES:
        raise FrozenHeadTrainingError("Frozen batch-normalization buffer inventory changed")
    for name, value in buffers.items():
        if value.dtype != torch.float32 or value.device.type != "cpu" or not bool(torch.isfinite(value).all()):
            raise FrozenHeadTrainingError(f"Frozen DB head buffer is invalid: {name}")


def _full_train_losses(
    head: frozen_trunk_head.FrozenDbHead,
    samples: Sequence[_Sample],
    cancellation_check: Callable[[], None] | None,
) -> tuple[float, float | None, float | None]:
    all_losses: list[float] = []
    positive: list[float] = []
    empty: list[float] = []
    with torch.no_grad():
        for sample in samples:
            _check_cancellation(cancellation_check)
            loss, category = masked_db_dice_loss(
                head.forward_logits(sample.features), sample.target, sample.mask
            )
            _require_finite_scalar(loss, "Full-train loss")
            scalar = float(loss)
            all_losses.append(scalar)
            (positive if category == "positive_dice" else empty).append(scalar)
    return _mean(all_losses), _optional_mean(positive), _optional_mean(empty)


def _validate_gradients(head: frozen_trunk_head.FrozenDbHead) -> None:
    for name, value in head.named_parameters():
        if value.grad is None or not bool(torch.isfinite(value.grad).all()):
            raise FrozenHeadTrainingError(f"DB head gradient is missing or non-finite: {name}")


def _validate_parameters(head: frozen_trunk_head.FrozenDbHead) -> None:
    if any(not bool(torch.isfinite(value).all()) for value in head.parameters()):
        raise FrozenHeadTrainingError("DB head parameter became non-finite")


def _check_cancellation(callback: Callable[[], None] | None) -> None:
    if callback is not None:
        callback()


def _clone_state(head: frozen_trunk_head.FrozenDbHead) -> dict[str, Tensor]:
    return {name: value.detach().clone() for name, value in head.state_dict().items()}


def _tensor_inventory_sha256(values: Sequence[tuple[str, Tensor]]) -> str:
    digest = sha256()
    for name, value in values:
        digest.update(name.encode("utf-8") + b"\0")
        digest.update(value.detach().cpu().contiguous().numpy().astype("<f4", copy=False).tobytes())
    return digest.hexdigest()


def _frozen_bn_sha256(head: frozen_trunk_head.FrozenDbHead) -> str:
    return _tensor_inventory_sha256(tuple(head.named_buffers()))


def _trainable_state_sha256(head: frozen_trunk_head.FrozenDbHead) -> str:
    return _tensor_inventory_sha256(tuple(head.named_parameters()))


def _ordered_ids_sha256(panel_ids: Sequence[str]) -> str:
    return sha256(("\n".join(panel_ids) + "\n").encode("utf-8")).hexdigest()


def _require_finite_scalar(value: Tensor, label: str) -> None:
    if value.ndim != 0 or not bool(torch.isfinite(value)):
        raise FrozenHeadTrainingError(f"{label} must be a finite scalar")


def _mean(values: Sequence[float]) -> float:
    if not values:
        raise FrozenHeadTrainingError("Loss aggregation received no samples")
    result = math.fsum(values) / len(values)
    if not math.isfinite(result):
        raise FrozenHeadTrainingError("Loss aggregation became non-finite")
    return result


def _optional_mean(values: Sequence[float]) -> float | None:
    return _mean(values) if values else None


__all__ = [
    "BATCH_SIZE", "DICE_EPSILON", "EPOCHS", "EpochLoss",
    "FrozenHeadTrainingCancelledError", "FrozenHeadTrainingError",
    "LEARNING_RATE", "SEED", "TRAINABLE_PARAMETER_NAMES", "TrainingPanel",
    "TrainingResult", "WEIGHT_DECAY", "masked_db_dice_loss", "train_frozen_head",
]
