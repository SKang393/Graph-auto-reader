# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import numpy as np
import pytest
import torch

from ml.ocr.official_bakeoff import frozen_head_training as training
from ml.ocr.official_bakeoff import frozen_trunk_head


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


def test_positive_dice_matches_fixed_formula() -> None:
    logits = torch.tensor([[[[-1.0, 0.0], [1.0, 2.0]]]], requires_grad=True)
    target = torch.tensor([[[[0.0, 1.0], [1.0, 0.0]]]])
    mask = torch.tensor([[[[1.0, 1.0], [1.0, 0.0]]]])

    loss, category = training.masked_db_dice_loss(logits, target, mask)

    probabilities = torch.sigmoid(logits)
    expected = 1 - (2 * (probabilities * target * mask).sum() + training.DICE_EPSILON) / (
        (probabilities * mask).sum() + (target * mask).sum() + training.DICE_EPSILON
    )
    assert category == "positive_dice"
    assert torch.equal(loss, expected)


def test_empty_target_uses_useful_masked_mean_probability() -> None:
    logits = torch.zeros((1, 1, 2, 2), requires_grad=True)
    target = torch.zeros_like(logits)
    mask = torch.tensor([[[[1.0, 1.0], [0.0, 0.0]]]])

    loss, category = training.masked_db_dice_loss(logits, target, mask)
    loss.backward()

    assert category == "empty_negative"
    assert float(loss.detach()) == 0.5
    assert torch.all(logits.grad[mask.bool()] > 0)
    assert torch.all(logits.grad[~mask.bool()] == 0)


def test_masked_positive_pixel_contributes_no_loss_or_gradient() -> None:
    logits = torch.zeros((1, 1, 1, 2), requires_grad=True)
    target = torch.ones_like(logits)
    mask = torch.tensor([[[[1.0, 0.0]]]])

    loss, category = training.masked_db_dice_loss(logits, target, mask)
    loss.backward()

    assert category == "positive_dice"
    assert logits.grad[0, 0, 0, 0] != 0
    assert logits.grad[0, 0, 0, 1] == 0


@pytest.mark.parametrize("split", ["dev", "validation"])
def test_non_train_panel_is_rejected_before_optimizer(monkeypatch, split: str) -> None:
    constructed = False

    def forbidden(*args, **kwargs):
        nonlocal constructed
        constructed = True
        raise AssertionError("optimizer constructed")

    monkeypatch.setattr(torch.optim, "AdamW", forbidden)
    with pytest.raises(training.FrozenHeadTrainingError, match="cannot enter optimization"):
        training.train_frozen_head(_head(), [_panel("foreign", positive=True, split=split)])
    assert constructed is False


def test_invalid_supervision_fails_before_optimizer(monkeypatch) -> None:
    constructed = False

    def forbidden(*args, **kwargs):
        nonlocal constructed
        constructed = True
        raise AssertionError("optimizer constructed")

    monkeypatch.setattr(torch.optim, "AdamW", forbidden)
    base = _panel("zero-mask", positive=False)
    panel = training.TrainingPanel(
        base.panel_id, base.split, base.features, base.target,
        _immutable(np.zeros_like(base.mask)),
    )
    with pytest.raises(training.FrozenHeadTrainingError, match="all-zero supervision mask"):
        training.train_frozen_head(_head(), [panel])
    assert constructed is False


def test_arrays_must_be_immutable_and_parameter_inventory_exact(monkeypatch) -> None:
    base = _panel("mutable", positive=True)
    mutable = np.array(base.features, copy=True)
    with pytest.raises(training.FrozenHeadTrainingError, match="immutable finite"):
        training.train_frozen_head(
            _head(),
            [training.TrainingPanel(base.panel_id, base.split, mutable, base.target, base.mask)],
        )

    constructed = False

    def forbidden(*args, **kwargs):
        nonlocal constructed
        constructed = True
        raise AssertionError("optimizer constructed")

    monkeypatch.setattr(torch.optim, "AdamW", forbidden)
    head = _head()
    head.conv_weight.requires_grad_(False)
    with pytest.raises(training.FrozenHeadTrainingError, match="conv_weight"):
        training.train_frozen_head(head, [base])
    assert constructed is False


def test_training_is_deterministic_selects_full_train_epoch_and_preserves_bn(monkeypatch) -> None:
    panels = [_panel("panel-b", positive=False), _panel("panel-a", positive=True)]
    first = _head()
    second = _head()
    observed_backends: list[bool] = []
    original_forward = frozen_trunk_head.FrozenDbHead.forward_logits

    def observed_forward(self, features):
        observed_backends.append(torch.backends.mkldnn.enabled)
        return original_forward(self, features)

    monkeypatch.setattr(frozen_trunk_head.FrozenDbHead, "forward_logits", observed_forward)

    with torch.backends.mkldnn.flags(enabled=True):
        first_result = training.train_frozen_head(first, panels)
        assert torch.backends.mkldnn.enabled is True
        second_result = training.train_frozen_head(second, list(reversed(panels)))
        assert torch.backends.mkldnn.enabled is True

    assert first_result == second_result
    assert first_result.optimizer_steps == 120
    assert first_result.selected_epoch == min(
        first_result.epoch_losses, key=lambda item: item.full_train_loss
    ).epoch
    assert len(first_result.epoch_losses) == 60
    assert first_result.positive_panel_count == 1
    assert first_result.empty_positive_panels == 1
    assert all(item.full_positive_dice_loss is not None for item in first_result.epoch_losses)
    assert all(item.full_empty_negative_loss is not None for item in first_result.epoch_losses)
    assert first_result.frozen_batch_norm_sha256_before == first_result.frozen_batch_norm_sha256_after
    assert first_result.trainable_parameter_names == training.TRAINABLE_PARAMETER_NAMES
    assert first_result.torch_backend == "cpu-mkldnn-disabled-float64-conv0"
    assert observed_backends and not any(observed_backends)
    for left, right in zip(first.parameters(), second.parameters(), strict=True):
        assert torch.equal(left, right)


def test_cancellation_restores_original_head_and_does_not_change_global_rng() -> None:
    head = _head()
    head.train()
    before = _parameter_state(head)
    rng_before = torch.random.get_rng_state().clone()
    calls = 0

    def cancel() -> None:
        nonlocal calls
        calls += 1
        if calls == 4:
            raise training.FrozenHeadTrainingCancelledError("cancelled")

    with torch.backends.mkldnn.flags(enabled=True):
        with pytest.raises(training.FrozenHeadTrainingCancelledError):
            training.train_frozen_head(head, [_panel("cancel", positive=True)], cancellation_check=cancel)
        assert torch.backends.mkldnn.enabled is True

    assert torch.equal(torch.random.get_rng_state(), rng_before)
    assert head.training is True
    for name, value in head.named_parameters():
        assert torch.equal(value, before[name])
