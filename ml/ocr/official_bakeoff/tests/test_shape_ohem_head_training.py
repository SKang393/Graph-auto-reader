# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import pytest
import torch
from ml.ocr.official_bakeoff import frozen_head_training as frozen
from ml.ocr.official_bakeoff import frozen_trunk_head
from ml.ocr.official_bakeoff import ohem_head_training as ohem
from ml.ocr.official_bakeoff import shape_ohem_head_training as training
from ml.ocr.official_bakeoff.tests.test_ohem_head_training import _head, _panel, _parameter_state


def test_positive_objective_is_exact_equal_mean_with_gradients():
    logits=torch.tensor([[[[-1., .2, 1., -2., 3., .5]]]],requires_grad=True)
    target=torch.tensor([[[[1.,1.,0.,0.,0.,0.]]]])
    mask=torch.tensor([[[[1.,1.,1.,1.,1.,0.]]]])
    value,category=training.masked_shape_ohem_loss(logits,target,mask)
    dice,_=frozen.masked_db_dice_loss(logits,target,mask)
    hard,_=ohem.masked_balanced_ohem_bce_loss(logits,target,mask)
    assert category=="positive_shape_ohem"
    assert torch.equal(value,(dice+hard)*.5)
    value.backward()
    assert torch.isfinite(logits.grad).all()
    assert torch.all(logits.grad[target.bool()]<0)
    assert logits.grad[0,0,0,5]==0


def test_empty_target_behavior_is_unchanged():
    logits=torch.zeros((1,1,2,2),requires_grad=True)
    target=torch.zeros_like(logits);mask=torch.ones_like(logits)
    value,category=training.masked_shape_ohem_loss(logits,target,mask)
    assert category=="empty_negative" and float(value.detach())==.5
    value.backward()
    assert torch.all(logits.grad>0)


def test_zero_mask_fails_before_contributing_loss():
    value=torch.zeros((1,1,2,2))
    with pytest.raises(training.FrozenHeadTrainingError,match="all-zero"):
        training.masked_shape_ohem_loss(value,torch.ones_like(value),value)


@pytest.mark.parametrize("split", ["dev", "validation"])
def test_non_train_panel_is_rejected_before_optimizer(monkeypatch, split: str) -> None:
    constructed = False

    def forbidden(*args, **kwargs):
        nonlocal constructed
        constructed = True
        raise AssertionError("optimizer constructed")

    monkeypatch.setattr(torch.optim, "AdamW", forbidden)
    with pytest.raises(training.FrozenHeadTrainingError, match="cannot enter optimization"):
        training.train_shape_ohem_head(_head(), [_panel("foreign", positive=True, split=split)])
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
        first_result = training.train_shape_ohem_head(first, panels)
        assert torch.backends.mkldnn.enabled is True
        second_result = training.train_shape_ohem_head(second, list(reversed(panels)))
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
        training.train_shape_ohem_head(head, [_panel("cancel", positive=True)], cancellation_check=cancel)

    assert head.training is True
    assert torch.equal(torch.random.get_rng_state(), rng_before)
    assert torch.get_num_threads() == previous_threads
    assert torch.are_deterministic_algorithms_enabled() == previous_deterministic
    for name, value in head.named_parameters():
        assert torch.equal(value, before[name])
