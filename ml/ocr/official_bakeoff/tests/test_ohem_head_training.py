# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import numpy as np
import pytest
import torch
import torch.nn.functional as F

from ml.ocr.official_bakeoff import frozen_head_training as frozen
from ml.ocr.official_bakeoff import frozen_trunk_head
from ml.ocr.official_bakeoff import ohem_head_training as training


def _immutable(value: np.ndarray) -> np.ndarray:
    contiguous = np.ascontiguousarray(value, dtype=np.float32)
    return np.frombuffer(contiguous.tobytes(), dtype=np.float32).reshape(contiguous.shape)


def _head() -> frozen_trunk_head.FrozenDbHead:
    rng = np.random.default_rng(393)
    constants = {
        name: rng.normal(0, 0.02, size=shape).astype(np.float32)
        for name, shape in frozen_trunk_head.TRAINABLE_CONSTANT_SHAPES.items()
    }
    for name, shape in frozen_trunk_head.FROZEN_BATCH_NORM_SHAPES.items():
        constants[name] = (
            np.ones(shape, dtype=np.float32)
            if name.endswith("w_2")
            else np.zeros(shape, dtype=np.float32)
        )
    return frozen_trunk_head.FrozenDbHead(constants)


def _panel(panel_id: str, *, positive: bool, split: str = "train") -> training.TrainingPanel:
    target = np.zeros((1, 4, 4), dtype=np.float32)
    if positive:
        target[0, 1:3, 1:3] = 1
    return training.TrainingPanel(
        panel_id,
        split,
        _immutable(np.full((1, 96, 1, 1), 0.25, dtype=np.float32)),
        _immutable(target),
        _immutable(np.ones((1, 4, 4), dtype=np.float32)),
    )


def _parameter_state(head: frozen_trunk_head.FrozenDbHead) -> dict[str, torch.Tensor]:
    return {name: value.detach().clone() for name, value in head.named_parameters()}


def test_positive_loss_uses_all_positives_and_three_hardest_negatives() -> None:
    logits = torch.tensor([[[[-2.0, 4.0, 2.0, 0.0, -4.0]]]], requires_grad=True)
    target = torch.tensor([[[[1.0, 0.0, 0.0, 0.0, 0.0]]]])
    mask = torch.ones_like(target)

    loss, category = training.masked_balanced_ohem_bce_loss(logits, target, mask)
    per_pixel = F.binary_cross_entropy_with_logits(logits, target, reduction="none")
    expected = (per_pixel[0, 0, 0, 0] + per_pixel[0, 0, 0, 1:4].sum()) / 4
    loss.backward()

    assert category == "positive_ohem_bce"
    assert torch.equal(loss.detach(), expected.detach())
    assert torch.all(logits.grad[0, 0, 0, :4] != 0)
    assert logits.grad[0, 0, 0, 4] == 0


def test_mask_controls_ohem_inventory() -> None:
    logits = torch.zeros((1, 1, 1, 4), requires_grad=True)
    target = torch.tensor([[[[1.0, 1.0, 0.0, 0.0]]]])
    mask = torch.tensor([[[[1.0, 0.0, 1.0, 0.0]]]])

    loss, category = training.masked_balanced_ohem_bce_loss(logits, target, mask)
    loss.backward()

    assert category == "positive_ohem_bce"
    assert float(loss.detach()) == pytest.approx(float(F.softplus(torch.tensor(0.0))))
    assert logits.grad[0, 0, 0, 0] != 0
    assert logits.grad[0, 0, 0, 1] == 0
    assert logits.grad[0, 0, 0, 2] != 0
    assert logits.grad[0, 0, 0, 3] == 0


def test_empty_target_preserves_masked_mean_probability() -> None:
    logits = torch.zeros((1, 1, 2, 2), requires_grad=True)
    target = torch.zeros_like(logits)
    mask = torch.tensor([[[[1.0, 1.0], [0.0, 0.0]]]])

    loss, category = training.masked_balanced_ohem_bce_loss(logits, target, mask)
    loss.backward()

    assert category == "empty_negative"
    assert float(loss.detach()) == 0.5
    assert torch.all(logits.grad[mask.bool()] > 0)
    assert torch.all(logits.grad[~mask.bool()] == 0)


@pytest.mark.parametrize("split", ["dev", "validation"])
def test_non_train_panel_is_rejected_before_optimizer(monkeypatch, split: str) -> None:
    constructed = False

    def forbidden(*args, **kwargs):
        nonlocal constructed
        constructed = True
        raise AssertionError("optimizer constructed")

    monkeypatch.setattr(torch.optim, "AdamW", forbidden)
    with pytest.raises(training.FrozenHeadTrainingError, match="cannot enter optimization"):
        training.train_ohem_head(_head(), [_panel("foreign", positive=True, split=split)])
    assert constructed is False


def test_training_preserves_frozen_engine_semantics(monkeypatch) -> None:
    panels = [_panel("panel-b", positive=False), _panel("panel-a", positive=True)]
    first = _head()
    second = _head()
    observed_backends: list[bool] = []
    original_forward = frozen_trunk_head.FrozenDbHead.forward_logits

    def observed_forward(self, features):
        observed_backends.append(torch.backends.mkldnn.enabled)
        return original_forward(self, features)

    monkeypatch.setattr(frozen_trunk_head.FrozenDbHead, "forward_logits", observed_forward)
    rng_before = torch.random.get_rng_state().clone()
    with torch.backends.mkldnn.flags(enabled=True):
        first_result = training.train_ohem_head(first, panels)
        assert torch.backends.mkldnn.enabled is True
        second_result = training.train_ohem_head(second, list(reversed(panels)))
        assert torch.backends.mkldnn.enabled is True

    assert torch.equal(torch.random.get_rng_state(), rng_before)
    assert first_result == second_result
    assert first_result.optimizer_steps == 120
    assert first_result.selected_epoch == min(
        first_result.epoch_losses, key=lambda item: item.full_train_loss
    ).epoch
    assert first_result.positive_panel_count == 1
    assert first_result.empty_positive_panels == 1
    assert first_result.negative_ratio == 3
    assert first_result.objective_identity == training.OBJECTIVE_IDENTITY
    assert first_result.seed == frozen.SEED
    assert first_result.batch_size == frozen.BATCH_SIZE
    assert first_result.learning_rate == frozen.LEARNING_RATE
    assert first_result.weight_decay == frozen.WEIGHT_DECAY
    assert first_result.torch_backend == frozen.TORCH_BACKEND
    assert first_result.frozen_batch_norm_sha256_before == first_result.frozen_batch_norm_sha256_after
    assert first_result.trainable_parameter_names == frozen.TRAINABLE_PARAMETER_NAMES
    assert observed_backends and not any(observed_backends)
    for left, right in zip(first.parameters(), second.parameters(), strict=True):
        assert torch.equal(left, right)


def test_cancellation_restores_head_mode_parameters_and_global_state() -> None:
    head = _head()
    head.train()
    before = _parameter_state(head)
    rng_before = torch.random.get_rng_state().clone()
    previous_threads = torch.get_num_threads()
    previous_deterministic = torch.are_deterministic_algorithms_enabled()
    calls = 0

    def cancel() -> None:
        nonlocal calls
        calls += 1
        if calls == 4:
            raise training.FrozenHeadTrainingCancelledError("cancelled")

    with pytest.raises(training.FrozenHeadTrainingCancelledError):
        training.train_ohem_head(head, [_panel("cancel", positive=True)], cancellation_check=cancel)

    assert head.training is True
    assert torch.equal(torch.random.get_rng_state(), rng_before)
    assert torch.get_num_threads() == previous_threads
    assert torch.are_deterministic_algorithms_enabled() == previous_deterministic
    for name, value in head.named_parameters():
        assert torch.equal(value, before[name])
