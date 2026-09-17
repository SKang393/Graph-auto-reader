# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Replacement-CPU Dice training; historical V43 source remains immutable."""
from __future__ import annotations
import json
import math
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Mapping, Sequence
from uuid import uuid4
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
INTRAOP_THREADS = 12
_CHECKPOINT_SCHEMA_VERSION = 1

@dataclass(frozen=True)
class TrainingResult(BaseTrainingResult):
    intraop_threads: int = INTRAOP_THREADS


def train_resource_limited_head(
    head: frozen_trunk_head.FrozenDbHead,
    panels: Sequence[TrainingPanel],
    *,
    cancellation_check: Callable[[], None] | None = None,
    progress_callback: Callable[[EpochLoss], None] | None = None,
    checkpoint_path: os.PathLike[str] | str | None = None,
    checkpoint_binding: Mapping[str, object] | None = None,
) -> TrainingResult:
    """Train on canonicalized train-only inputs and restore the selected epoch."""

    if not isinstance(head, frozen_trunk_head.FrozenDbHead):
        raise FrozenHeadTrainingError("Training requires the reviewed FrozenDbHead")
    original_state = _clone_state(head)
    original_training = head.training
    previous_threads = torch.get_num_threads()
    previous_deterministic = torch.are_deterministic_algorithms_enabled()
    previous_warn_only = torch.is_deterministic_algorithms_warn_only_enabled()
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
        expected_optimizer_recipe = _optimizer_recipe(optimizer)
        draw_generator = torch.Generator(device="cpu").manual_seed(SEED)
        epoch_losses: list[EpochLoss] = []
        best_state: dict[str, Tensor] | None = None
        best_loss = math.inf
        selected_epoch = 0
        optimizer_steps = 0
        completed_epochs = 0
        recovery_path, input_binding = _prepare_recovery(
            checkpoint_path, checkpoint_binding
        )
        recipe = _checkpoint_recipe(samples)
        if recovery_path is not None and recovery_path.exists():
            (
                completed_epochs,
                optimizer_steps,
                epoch_losses,
                best_state,
                best_loss,
                selected_epoch,
            ) = _restore_checkpoint(
                recovery_path,
                input_binding,
                recipe,
                head,
                optimizer,
                expected_optimizer_recipe,
                draw_generator,
                original_state,
                frozen_before,
                samples,
            )

        for epoch_index in range(completed_epochs, EPOCHS):
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
            if recovery_path is not None:
                _write_checkpoint(
                    recovery_path,
                    input_binding,
                    recipe,
                    head,
                    optimizer,
                    draw_generator,
                    best_state,
                    best_loss,
                    selected_epoch,
                    epoch_losses,
                    optimizer_steps,
                    frozen_before,
                )
            if progress_callback is not None:
                progress_callback(epoch_losses[-1])

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
        torch.use_deterministic_algorithms(previous_deterministic, warn_only=previous_warn_only)
        torch.set_num_threads(previous_threads)


def _prepare_recovery(
    checkpoint_path: os.PathLike[str] | str | None,
    checkpoint_binding: Mapping[str, object] | None,
) -> tuple[Path | None, dict[str, object]]:
    if checkpoint_path is None:
        if checkpoint_binding is not None:
            raise FrozenHeadTrainingError("Checkpoint binding requires a checkpoint path")
        return None, {}
    if checkpoint_binding is None:
        raise FrozenHeadTrainingError("Checkpoint path requires an input binding")
    try:
        path = Path(checkpoint_path)
    except TypeError as error:
        raise FrozenHeadTrainingError("Checkpoint path is invalid") from error
    if not path.name or (path.exists() and not path.is_file()):
        raise FrozenHeadTrainingError("Checkpoint path must identify a file")
    if not isinstance(checkpoint_binding, Mapping) or not checkpoint_binding:
        raise FrozenHeadTrainingError("Checkpoint binding must be a non-empty mapping")
    if any(not isinstance(key, str) or not key for key in checkpoint_binding):
        raise FrozenHeadTrainingError("Checkpoint binding keys must be non-empty strings")
    normalized = _json_mapping(checkpoint_binding, "Checkpoint binding")
    return path, normalized


def _json_mapping(value: object, label: str) -> dict[str, object]:
    try:
        encoded = json.dumps(
            value, sort_keys=True, separators=(",", ":"), allow_nan=False
        )
        normalized = json.loads(encoded)
    except (TypeError, ValueError) as error:
        raise FrozenHeadTrainingError(f"{label} must contain JSON values") from error
    if not isinstance(normalized, dict):
        raise FrozenHeadTrainingError(f"{label} must be a mapping")
    return normalized


def _checkpoint_recipe(samples: Sequence[object]) -> dict[str, object]:
    return {
        "epochs": EPOCHS,
        "intraop_threads": INTRAOP_THREADS,
        "seed": SEED,
        "batch_size": BATCH_SIZE,
        "learning_rate": LEARNING_RATE,
        "weight_decay": WEIGHT_DECAY,
        "dice_epsilon": DICE_EPSILON,
        "torch_backend": TORCH_BACKEND,
        "trainable_parameter_names": list(TRAINABLE_PARAMETER_NAMES),
        "panel_ids": [sample.panel_id for sample in samples],
    }


def _optimizer_recipe(optimizer: torch.optim.Optimizer) -> dict[str, object]:
    group = optimizer.param_groups[0]
    return {key: value for key, value in group.items() if key != "params"}


def _restore_checkpoint(
    path: Path,
    input_binding: dict[str, object],
    recipe: dict[str, object],
    head: frozen_trunk_head.FrozenDbHead,
    optimizer: torch.optim.Optimizer,
    expected_optimizer_recipe: dict[str, object],
    draw_generator: torch.Generator,
    original_state: Mapping[str, Tensor],
    frozen_before: str,
    samples: Sequence[object],
) -> tuple[int, int, list[EpochLoss], dict[str, Tensor], float, int]:
    try:
        checkpoint = torch.load(path, map_location="cpu", weights_only=True)
    except Exception as error:
        raise FrozenHeadTrainingError("Recovery checkpoint could not be loaded") from error
    required = {
        "schema_version", "input_binding", "recipe", "completed_epoch",
        "optimizer_steps", "epoch_losses", "head_state", "optimizer_state",
        "draw_generator_state", "best_state", "best_loss", "selected_epoch",
        "frozen_batch_norm_sha256",
    }
    if not isinstance(checkpoint, dict) or set(checkpoint) != required:
        raise FrozenHeadTrainingError("Recovery checkpoint structure is invalid")
    if (
        type(checkpoint["schema_version"]) is not int
        or checkpoint["schema_version"] != _CHECKPOINT_SCHEMA_VERSION
    ):
        raise FrozenHeadTrainingError("Recovery checkpoint schema is incompatible")
    recovered_binding = _json_mapping(
        checkpoint["input_binding"], "Recovery checkpoint input binding"
    )
    if recovered_binding != input_binding:
        raise FrozenHeadTrainingError("Recovery checkpoint input binding does not match")
    recovered_recipe = _json_mapping(
        checkpoint["recipe"], "Recovery checkpoint recipe"
    )
    if recovered_recipe != recipe:
        raise FrozenHeadTrainingError("Recovery checkpoint recipe does not match")
    if (
        not isinstance(checkpoint["frozen_batch_norm_sha256"], str)
        or checkpoint["frozen_batch_norm_sha256"] != frozen_before
    ):
        raise FrozenHeadTrainingError("Recovery checkpoint frozen state does not match")

    completed_epoch = checkpoint["completed_epoch"]
    optimizer_steps = checkpoint["optimizer_steps"]
    selected_epoch = checkpoint["selected_epoch"]
    best_loss = checkpoint["best_loss"]
    if type(completed_epoch) is not int or not 1 <= completed_epoch <= EPOCHS:
        raise FrozenHeadTrainingError("Recovery checkpoint epoch count is invalid")
    expected_steps = completed_epoch * len(samples)
    if type(optimizer_steps) is not int or optimizer_steps != expected_steps:
        raise FrozenHeadTrainingError("Recovery checkpoint optimizer step count is invalid")
    if type(selected_epoch) is not int or not 1 <= selected_epoch <= completed_epoch:
        raise FrozenHeadTrainingError("Recovery checkpoint selected epoch is invalid")
    if not isinstance(best_loss, float) or not math.isfinite(best_loss):
        raise FrozenHeadTrainingError("Recovery checkpoint best loss is invalid")

    epoch_losses = _restore_epoch_losses(
        checkpoint["epoch_losses"], completed_epoch, len(samples)
    )
    earliest = min(epoch_losses, key=lambda item: item.full_train_loss)
    if selected_epoch != earliest.epoch or best_loss != earliest.full_train_loss:
        raise FrozenHeadTrainingError("Recovery checkpoint best epoch is inconsistent")

    head_state = _validated_tensor_state(
        checkpoint["head_state"], original_state, "current head"
    )
    parameter_templates = dict(head.named_parameters())
    best_state = _validated_tensor_state(
        checkpoint["best_state"], parameter_templates, "best head"
    )
    for name, value in head.named_buffers():
        if not torch.equal(head_state[name], original_state[name]):
            raise FrozenHeadTrainingError("Recovery checkpoint changed frozen state")

    generator_state = checkpoint["draw_generator_state"]
    if (
        not isinstance(generator_state, Tensor)
        or generator_state.device.type != "cpu"
        or generator_state.dtype != torch.uint8
        or generator_state.ndim != 1
        or generator_state.numel() == 0
    ):
        raise FrozenHeadTrainingError("Recovery checkpoint draw state is invalid")
    expected_generator = torch.Generator(device="cpu").manual_seed(SEED)
    for epoch_loss in epoch_losses:
        order = torch.randperm(len(samples), generator=expected_generator).tolist()
        if epoch_loss.draw_order_sha256 != _ordered_ids_sha256(
            [samples[index].panel_id for index in order]
        ):
            raise FrozenHeadTrainingError("Recovery checkpoint draw history is invalid")
    if not torch.equal(generator_state, expected_generator.get_state()):
        raise FrozenHeadTrainingError("Recovery checkpoint draw state is inconsistent")

    optimizer_state = checkpoint["optimizer_state"]
    if not isinstance(optimizer_state, dict) or set(optimizer_state) != {"state", "param_groups"}:
        raise FrozenHeadTrainingError("Recovery checkpoint optimizer state is invalid")
    try:
        head.load_state_dict(head_state, strict=True)
        optimizer.load_state_dict(optimizer_state)
        draw_generator.set_state(generator_state)
    except Exception as error:
        raise FrozenHeadTrainingError("Recovery checkpoint state is invalid") from error
    _validate_head(head)
    if _frozen_bn_sha256(head) != frozen_before:
        raise FrozenHeadTrainingError("Recovery checkpoint changed frozen state")
    _validate_optimizer_state(
        optimizer, expected_optimizer_recipe, optimizer_steps
    )
    return (
        completed_epoch, optimizer_steps, epoch_losses, best_state,
        best_loss, selected_epoch,
    )


def _restore_epoch_losses(
    value: object, completed_epoch: int, panel_count: int
) -> list[EpochLoss]:
    fields = tuple(EpochLoss.__dataclass_fields__)
    if not isinstance(value, list) or len(value) != completed_epoch:
        raise FrozenHeadTrainingError("Recovery checkpoint loss history is invalid")
    restored: list[EpochLoss] = []
    for expected_epoch, item in enumerate(value, start=1):
        if not isinstance(item, dict) or tuple(item) != fields:
            raise FrozenHeadTrainingError("Recovery checkpoint loss entry is invalid")
        if type(item["epoch"]) is not int or item["epoch"] != expected_epoch:
            raise FrozenHeadTrainingError("Recovery checkpoint loss epochs are invalid")
        if (
            type(item["optimizer_steps"]) is not int
            or item["optimizer_steps"] != expected_epoch * panel_count
        ):
            raise FrozenHeadTrainingError("Recovery checkpoint loss steps are invalid")
        for field in (
            "training_step_mean_loss", "full_train_loss",
            "full_positive_dice_loss", "full_empty_negative_loss",
        ):
            scalar = item[field]
            if scalar is not None and (
                not isinstance(scalar, float) or not math.isfinite(scalar)
            ):
                raise FrozenHeadTrainingError("Recovery checkpoint loss is non-finite")
        digest = item["draw_order_sha256"]
        if (
            not isinstance(digest, str) or len(digest) != 64
            or any(character not in "0123456789abcdef" for character in digest)
        ):
            raise FrozenHeadTrainingError("Recovery checkpoint draw digest is invalid")
        restored.append(EpochLoss(**item))
    return restored


def _validated_tensor_state(
    value: object,
    templates: Mapping[str, Tensor],
    label: str,
) -> dict[str, Tensor]:
    if not isinstance(value, dict) or tuple(value) != tuple(templates):
        raise FrozenHeadTrainingError(f"Recovery checkpoint {label} inventory is invalid")
    restored: dict[str, Tensor] = {}
    for name, template in templates.items():
        tensor = value[name]
        if (
            not isinstance(tensor, Tensor)
            or tensor.device.type != "cpu"
            or tensor.dtype != template.dtype
            or tensor.shape != template.shape
            or not bool(torch.isfinite(tensor).all())
        ):
            raise FrozenHeadTrainingError(f"Recovery checkpoint {label} tensor is invalid")
        restored[name] = tensor.detach().clone()
    return restored


def _validate_optimizer_state(
    optimizer: torch.optim.Optimizer,
    expected_recipe: Mapping[str, object],
    optimizer_steps: int,
) -> None:
    if len(optimizer.param_groups) != 1:
        raise FrozenHeadTrainingError("Recovery checkpoint optimizer groups are invalid")
    group = optimizer.param_groups[0]
    if set(group) != set(expected_recipe) | {"params"} or any(
        group[key] != value for key, value in expected_recipe.items()
    ):
        raise FrozenHeadTrainingError("Recovery checkpoint optimizer recipe is invalid")
    parameters = list(group["params"])
    if len(parameters) != len(TRAINABLE_PARAMETER_NAMES) or len(optimizer.state) != len(parameters):
        raise FrozenHeadTrainingError("Recovery checkpoint optimizer inventory is invalid")
    for parameter in parameters:
        state = optimizer.state.get(parameter)
        if not isinstance(state, dict) or set(state) != {"step", "exp_avg", "exp_avg_sq"}:
            raise FrozenHeadTrainingError("Recovery checkpoint AdamW state is invalid")
        step = state["step"]
        if (
            not isinstance(step, Tensor) or step.device.type != "cpu"
            or step.dtype != torch.float32 or step.ndim != 0
            or not bool(torch.isfinite(step))
            or float(step) != optimizer_steps
        ):
            raise FrozenHeadTrainingError("Recovery checkpoint AdamW step is invalid")
        for name in ("exp_avg", "exp_avg_sq"):
            tensor = state[name]
            if (
                not isinstance(tensor, Tensor) or tensor.device.type != "cpu"
                or tensor.dtype != parameter.dtype or tensor.shape != parameter.shape
                or not bool(torch.isfinite(tensor).all())
            ):
                raise FrozenHeadTrainingError("Recovery checkpoint AdamW tensor is invalid")


def _write_checkpoint(
    path: Path,
    input_binding: dict[str, object],
    recipe: dict[str, object],
    head: frozen_trunk_head.FrozenDbHead,
    optimizer: torch.optim.Optimizer,
    draw_generator: torch.Generator,
    best_state: dict[str, Tensor] | None,
    best_loss: float,
    selected_epoch: int,
    epoch_losses: Sequence[EpochLoss],
    optimizer_steps: int,
    frozen_before: str,
) -> None:
    if best_state is None:
        raise FrozenHeadTrainingError("Training produced no finite checkpoint")
    payload = {
        "schema_version": _CHECKPOINT_SCHEMA_VERSION,
        "input_binding": input_binding,
        "recipe": recipe,
        "completed_epoch": len(epoch_losses),
        "optimizer_steps": optimizer_steps,
        "epoch_losses": [
            {name: getattr(item, name) for name in EpochLoss.__dataclass_fields__}
            for item in epoch_losses
        ],
        "head_state": _clone_state(head),
        "optimizer_state": optimizer.state_dict(),
        "draw_generator_state": draw_generator.get_state().clone(),
        "best_state": {name: value.detach().clone() for name, value in best_state.items()},
        "best_loss": best_loss,
        "selected_epoch": selected_epoch,
        "frozen_batch_norm_sha256": frozen_before,
    }
    if not path.parent.is_dir():
        raise FrozenHeadTrainingError("Checkpoint parent directory does not exist")
    temporary = path.with_name(f".{path.name}.{uuid4().hex}.partial")
    try:
        with temporary.open("xb") as stream:
            torch.save(payload, stream)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        if os.name != "nt":
            descriptor = os.open(path.parent, os.O_RDONLY)
            try:
                os.fsync(descriptor)
            finally:
                os.close(descriptor)
    except Exception as error:
        raise FrozenHeadTrainingError("Recovery checkpoint could not be written") from error
    finally:
        temporary.unlink(missing_ok=True)
