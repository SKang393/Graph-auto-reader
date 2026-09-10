# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""V43 extends only the fixed Dice training duration; V40 sources stay immutable."""
from __future__ import annotations
import math
from dataclasses import dataclass
from typing import Callable, Sequence
import torch
from torch import Tensor
from ml.ocr.official_bakeoff import frozen_trunk_head
from ml.ocr.official_bakeoff.frozen_head_training import (
    BATCH_SIZE,
    DICE_EPSILON,
    EpochLoss,
    FrozenHeadTrainingError,
    LEARNING_RATE,
    SEED,
    TORCH_BACKEND,
    TRAINABLE_PARAMETER_NAMES,
    TrainingPanel,
    TrainingResult as BaseTrainingResult,
    WEIGHT_DECAY,
    _check_cancellation,
    _clone_state,
    _frozen_bn_sha256,
    _full_train_losses,
    _mean,
    _ordered_ids_sha256,
    _prepare_samples,
    _require_finite_scalar,
    _trainable_state_sha256,
    _validate_gradients,
    _validate_head,
    _validate_parameters,
    masked_db_dice_loss,
    FrozenHeadTrainingCancelledError,
)

EPOCHS = 240
INTRAOP_THREADS = 16

@dataclass(frozen=True)
class TrainingResult(BaseTrainingResult):
    intraop_threads: int = INTRAOP_THREADS


def train_extended_head(
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
        torch.set_num_threads(INTRAOP_THREADS)
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
                with torch.backends.mkldnn.flags(enabled=False):
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
            torch_backend=TORCH_BACKEND,
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

