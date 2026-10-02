# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from contextlib import nullcontext
import copy
import math
from types import SimpleNamespace

import pytest
import torch

from ml.markers.center.confidence_head_v32 import runner as focal
from ml.markers.center.confidence_head_v32.adaptation import (
    freeze_for_confidence_adaptation, install_confidence_head, verify_frozen_geometry,
)
from ml.markers.center.confidence_bce_v33 import runner
from ml.markers.center.confidence_bce_v33.objective import confidence_loss
from ml.markers.center.scale_classifier_v16.model import ScaleClassifierNet

BUDGET = SimpleNamespace(work_block=nullcontext)


@pytest.mark.parametrize("positive_count", [1, 3, 5, 9])
def test_expected_gradient_is_zero_at_empirical_positive_fraction(positive_count):
    probability = positive_count / 10
    logit = torch.tensor(math.log(probability / (1-probability)), dtype=torch.float64, requires_grad=True)
    labels = torch.tensor([1.] * positive_count + [0.] * (10-positive_count), dtype=torch.float64)
    confidence_loss(logit.expand_as(labels), labels).backward()
    assert abs(logit.grad.item()) < 1e-14


def test_negative_and_positive_rows_have_equal_log_loss_weight():
    logits = torch.tensor([-2., 2., -2., 2.], requires_grad=True)
    labels = torch.tensor([0., 1., 1., 0.])
    confidence_loss(logits, labels).backward()
    torch.testing.assert_close(logits.grad, (logits.detach().sigmoid()-labels)/4)
    torch.testing.assert_close(logits.grad[0], -logits.grad[1])
    torch.testing.assert_close(logits.grad[2], -logits.grad[3])


def test_extreme_finite_scores_keep_loss_and_gradients_finite():
    logits = torch.tensor([-10000., 10000., -10000., 10000.], requires_grad=True)
    loss = confidence_loss(logits, torch.tensor([0., 1., 1., 0.]))
    loss.backward()
    assert loss == 5000
    torch.testing.assert_close(logits.grad, torch.tensor([0., 0., -.25, .25]))


@pytest.mark.parametrize("logits,labels", [
    (torch.tensor([float('nan')]), torch.tensor([0.])),
    (torch.tensor([float('inf')]), torch.tensor([1.])),
    (torch.zeros(1), torch.tensor([float('nan')])),
    (torch.zeros(1), torch.tensor([.5])),
    (torch.zeros(1), torch.tensor([2.])),
    (torch.zeros(1), torch.ones(1, dtype=torch.float64)),
    (torch.zeros(1), torch.ones(2)),
    (torch.zeros(1, 1), torch.ones(1, 1)),
    (torch.zeros(0), torch.zeros(0)),
])
def test_malformed_loss_inputs_are_rejected(logits, labels):
    with pytest.raises(ValueError, match="finite equal-length"):
        confidence_loss(logits, labels)


def test_candidate_changes_only_the_registered_objective():
    removed = {"focal_alpha", "focal_gamma", "positive_loss_weight", "hard_negative_loss_weight"}
    assert {k: v for k, v in runner.RECIPE.items() if k != "confidence_objective"} == {
        k: v for k, v in focal.RECIPE.items() if k not in removed}
    assert runner.TRAINING_ROWS == focal.TRAINING_ROWS
    assert runner.MODEL_SHA256 == focal.MODEL_SHA256


def test_completed_recovery_matches_uninterrupted_training_and_preserves_geometry(tmp_path, monkeypatch):
    torch.manual_seed(1907)
    base = ScaleClassifierNet()
    original = {k: v.clone() for k, v in base.state_dict().items()}
    head = freeze_for_confidence_adaptation(base)
    fresh_head = copy.deepcopy(head)
    features = torch.randn(17, 64)
    labels = torch.tensor([0., 1.] * 8 + [0.])
    training = (features, labels, torch.zeros(17))
    recipe = dict(runner.RECIPE, epochs=3, batch_size=4)
    binding = {'test_population': 'deterministic_disposable_fixture', 'recipe': recipe}
    full, partial = tmp_path/'full', tmp_path/'partial'
    full.mkdir()
    partial.mkdir()
    optimizer = lambda h: torch.optim.AdamW(h.parameters(), lr=recipe['learning_rate'], weight_decay=recipe['weight_decay'])
    result = runner.train_epochs(head, optimizer(head), training, recipe, binding, full, BUDGET)
    save = runner._atomic_torch_save

    def save_then_interrupt(path, state):
        save(path, state)
        raise InterruptedError('interrupt after first completed epoch')

    monkeypatch.setattr(runner, '_atomic_torch_save', save_then_interrupt)
    with pytest.raises(InterruptedError):
        runner.train_epochs(fresh_head, optimizer(fresh_head), training, recipe, binding, partial, BUDGET)
    monkeypatch.setattr(runner, '_atomic_torch_save', save)
    resumed_head = copy.deepcopy(head)
    resumed = runner.train_epochs(resumed_head, optimizer(resumed_head), training, recipe, binding, partial, BUDGET,
                                  resume=partial/'recovery.pt')
    assert resumed['resumed_from_epoch'] == 1
    assert result['history'] == resumed['history']
    assert result['optimizer_steps'] == resumed['optimizer_steps'] == 15
    for key, value in head.state_dict().items():
        assert torch.equal(value, resumed_head.state_dict()[key])
    install_confidence_head(base, resumed_head)
    verify_frozen_geometry(base, original)
    completed = runner.train_epochs(resumed_head, optimizer(resumed_head), training, recipe, binding, partial, BUDGET,
                                    resume=partial/'recovery.pt', require_completed=True)
    assert completed['resumed_from_epoch'] == completed['completed_epochs'] == 3
    assert completed['history'] == resumed['history']


def test_hard_negative_flags_cannot_reweight_or_resample_training_rows(tmp_path):
    torch.manual_seed(311)
    model = ScaleClassifierNet()
    first = freeze_for_confidence_adaptation(model)
    second = copy.deepcopy(first)
    features, labels = torch.randn(9, 64), torch.tensor([1., 0., 0.] * 3)
    recipe = dict(runner.RECIPE, epochs=1, batch_size=4)
    results = []
    for index, head in enumerate((first, second)):
        output = tmp_path/str(index)
        output.mkdir()
        optimizer = torch.optim.AdamW(head.parameters(), lr=recipe['learning_rate'], weight_decay=recipe['weight_decay'])
        results.append(runner.train_epochs(head, optimizer, (features, labels, torch.full((9,), float(index))),
                                           recipe, {'test': True}, output, BUDGET))
    assert results[0] == results[1]
    for key, value in first.state_dict().items():
        assert torch.equal(value, second.state_dict()[key])
