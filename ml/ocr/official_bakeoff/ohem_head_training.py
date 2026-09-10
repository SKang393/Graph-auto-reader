# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Isolated deterministic OHEM-BCE training for the reviewed frozen DB head.

The pinned PP-OCRv5 configuration selects DiceLoss for its shrink-map branch.
This experimental objective instead uses the BCELoss mode supported by
PaddleOCR's BalanceLoss with its documented 3:1 hard-negative ratio. It is not
an exact reproduction of the official PP-OCRv5 loss. The feature tensors,
head, optimizer, draw order, checkpoint selection, and empty-target behavior
remain those of :mod:`frozen_head_training`.

This module does not authorize training or production approval.
"""

from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Callable, Sequence

import torch
from torch import Tensor
import torch.nn.functional as F

from ml.ocr.official_bakeoff import frozen_head_training as base
from ml.ocr.official_bakeoff import frozen_trunk_head


SEED = base.SEED
EPOCHS = base.EPOCHS
BATCH_SIZE = base.BATCH_SIZE
LEARNING_RATE = base.LEARNING_RATE
WEIGHT_DECAY = base.WEIGHT_DECAY
TORCH_BACKEND = base.TORCH_BACKEND
TRAINABLE_PARAMETER_NAMES = base.TRAINABLE_PARAMETER_NAMES
NEGATIVE_RATIO = 3
OBJECTIVE_IDENTITY = "frozen-db-head-balanced-ohem-bce-v1"
OFFICIAL_PADDLEOCR_REVISION = "33cbdd9deb2e00f61e7966db70669b249c005a37"
OFFICIAL_DB_LOSS_PATH = "ppocr/losses/det_db_loss.py"
OFFICIAL_BASIC_LOSS_PATH = "ppocr/losses/det_basic_loss.py"

FrozenHeadTrainingError = base.FrozenHeadTrainingError
FrozenHeadTrainingCancelledError = base.FrozenHeadTrainingCancelledError
TrainingPanel = base.TrainingPanel


@dataclass(frozen=True)
class EpochLoss:
    epoch: int
    optimizer_steps: int
    training_step_mean_loss: float
    full_train_loss: float
    full_positive_ohem_bce_loss: float | None
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
    negative_ratio: int
    objective_identity: str
    torch_backend: str
    trainable_parameter_names: tuple[str, ...]
    frozen_batch_norm_sha256_before: str
    frozen_batch_norm_sha256_after: str
    selected_trainable_state_sha256: str


def masked_balanced_ohem_bce_loss(
    logits: Tensor,
    target: Tensor,
    mask: Tensor,
) -> tuple[Tensor, str]:
    """Return stable per-pixel OHEM BCE or the retained empty-target loss."""

    base._validate_loss_tensors(logits, target, mask)
    valid = mask.bool()
    valid_count = int(valid.sum().detach())
    if valid_count <= 0:
        raise FrozenHeadTrainingError("An all-zero supervision mask cannot contribute")
    positive = valid & target.bool()
    positive_count = int(positive.sum().detach())
    if positive_count == 0:
        probabilities = torch.sigmoid(logits)
        return probabilities[valid].mean(), "empty_negative"

    per_pixel = F.binary_cross_entropy_with_logits(logits, target, reduction="none")
    positive_loss = per_pixel[positive]
    negative_loss = per_pixel[valid & ~target.bool()]
    negative_count = min(int(negative_loss.numel()), positive_count * NEGATIVE_RATIO)
    if negative_count:
        # PaddleOCR sorts the complete negative inventory. Stable order makes
        # equal-loss CPU ties reproducible without changing the selected sum.
        selected_negative = torch.sort(
            negative_loss, descending=True, stable=True
        ).values[:negative_count]
        total = positive_loss.sum() + selected_negative.sum()
    else:
        total = positive_loss.sum()
    return total / (positive_count + negative_count), "positive_ohem_bce"


def train_ohem_head(
    head: frozen_trunk_head.FrozenDbHead,
    panels: Sequence[TrainingPanel],
    *,
    cancellation_check: Callable[[], None] | None = None,
) -> TrainingResult:
    """Train only the reviewed DB head with the isolated OHEM objective."""

    if not isinstance(head, frozen_trunk_head.FrozenDbHead):
        raise FrozenHeadTrainingError("Training requires the reviewed FrozenDbHead")
    original_state = base._clone_state(head)
    original_training = head.training
    previous_threads = torch.get_num_threads()
    previous_deterministic = torch.are_deterministic_algorithms_enabled()
    try:
        base._validate_head(head)
        samples = base._prepare_samples(panels)
        frozen_before = base._frozen_bn_sha256(head)
        base._check_cancellation(cancellation_check)
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
            base._check_cancellation(cancellation_check)
            order = torch.randperm(len(samples), generator=draw_generator).tolist()
            step_losses: list[float] = []
            for sample_index in order:
                base._check_cancellation(cancellation_check)
                sample = samples[sample_index]
                with torch.backends.mkldnn.flags(enabled=False):
                    optimizer.zero_grad(set_to_none=True)
                    loss, _ = masked_balanced_ohem_bce_loss(
                        head.forward_logits(sample.features), sample.target, sample.mask
                    )
                    base._require_finite_scalar(loss, "Training loss")
                    loss.backward()
                    base._validate_gradients(head)
                    optimizer.step()
                    base._validate_parameters(head)
                optimizer_steps += 1
                step_losses.append(float(loss.detach()))

            full_loss, positive_loss, empty_loss = _full_train_losses(
                head, samples, cancellation_check
            )
            epoch_losses.append(EpochLoss(
                epoch=epoch_index + 1,
                optimizer_steps=optimizer_steps,
                training_step_mean_loss=base._mean(step_losses),
                full_train_loss=full_loss,
                full_positive_ohem_bce_loss=positive_loss,
                full_empty_negative_loss=empty_loss,
                draw_order_sha256=base._ordered_ids_sha256(
                    [samples[index].panel_id for index in order]
                ),
            ))
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
        frozen_after = base._frozen_bn_sha256(head)
        if frozen_after != frozen_before:
            raise FrozenHeadTrainingError("Frozen batch-normalization state changed")
        base._validate_head(head)
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
            negative_ratio=NEGATIVE_RATIO,
            objective_identity=OBJECTIVE_IDENTITY,
            torch_backend=TORCH_BACKEND,
            trainable_parameter_names=TRAINABLE_PARAMETER_NAMES,
            frozen_batch_norm_sha256_before=frozen_before,
            frozen_batch_norm_sha256_after=frozen_after,
            selected_trainable_state_sha256=base._trainable_state_sha256(head),
        )
    except Exception:
        head.load_state_dict(original_state, strict=True)
        head.train(original_training)
        raise
    finally:
        torch.use_deterministic_algorithms(previous_deterministic)
        torch.set_num_threads(previous_threads)


def _full_train_losses(
    head: frozen_trunk_head.FrozenDbHead,
    samples: Sequence[base._Sample],
    cancellation_check: Callable[[], None] | None,
) -> tuple[float, float | None, float | None]:
    all_losses: list[float] = []
    positive: list[float] = []
    empty: list[float] = []
    with torch.no_grad():
        for sample in samples:
            base._check_cancellation(cancellation_check)
            with torch.backends.mkldnn.flags(enabled=False):
                loss, category = masked_balanced_ohem_bce_loss(
                    head.forward_logits(sample.features), sample.target, sample.mask
                )
            base._require_finite_scalar(loss, "Full-train loss")
            scalar = float(loss)
            all_losses.append(scalar)
            (positive if category == "positive_ohem_bce" else empty).append(scalar)
    return base._mean(all_losses), base._optional_mean(positive), base._optional_mean(empty)


__all__ = [
    "BATCH_SIZE", "EPOCHS", "EpochLoss", "FrozenHeadTrainingCancelledError",
    "FrozenHeadTrainingError", "LEARNING_RATE", "NEGATIVE_RATIO", "OBJECTIVE_IDENTITY",
    "OFFICIAL_BASIC_LOSS_PATH", "OFFICIAL_DB_LOSS_PATH", "OFFICIAL_PADDLEOCR_REVISION",
    "SEED", "TORCH_BACKEND", "TRAINABLE_PARAMETER_NAMES", "TrainingPanel", "TrainingResult",
    "WEIGHT_DECAY", "masked_balanced_ohem_bce_loss", "train_ohem_head",
]
