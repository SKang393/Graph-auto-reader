# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from contextlib import nullcontext
import copy
from types import SimpleNamespace

import pytest
import torch

from ml.markers.center.confidence_bce_v33 import runner as linear
from ml.markers.center.confidence_spatial_v35 import runner
from ml.markers.center.confidence_spatial_v35.model import SpatialCenterNet
from ml.markers.center.scale_classifier_v16.model import ScaleClassifierNet

BUDGET = SimpleNamespace(work_block=nullcontext)


def test_only_confidence_capacity_changes_from_the_unweighted_trial():
    added = {'trainable_parameters', 'confidence_mapping', 'frozen_base_confidence', 'frozen_features', 'frozen_spatial_features'}
    assert {k: v for k, v in runner.RECIPE.items() if k not in added} == {
        k: v for k, v in linear.RECIPE.items() if k not in added}
    assert runner.TRAINING_ROWS == linear.TRAINING_ROWS
    assert runner.MODEL_SHA256 == linear.MODEL_SHA256


@pytest.mark.parametrize('defect', ['recipe', 'task', 'model', 'reads'])
def test_modified_candidate_identity_or_recipe_is_rejected(defect):
    config = {'task': runner.TASK, 'revision': runner.REVISION, 'candidate_id': 'P1',
        'recipe': dict(runner.RECIPE), 'training_group_rows': runner.TRAINING_ROWS,
        'source_checkpoint_sha256': runner.MODEL_SHA256, 'private_reads': 0,
        'sealed_runs_authorized': 0, 'production_approval': False}
    runner.validate_config(config)
    if defect == 'recipe':
        config['recipe']['confidence_threshold'] = .2
    elif defect == 'task':
        config['task'] = 'different-task'
    elif defect == 'model':
        config['source_checkpoint_sha256'] = 'changed'
    else:
        config['private_reads'] = 1
    with pytest.raises(ValueError):
        runner.validate_config(config)


def test_spatial_head_recovery_restores_buffers_optimizer_and_exact_epoch_order(tmp_path, monkeypatch):
    torch.manual_seed(12308)
    model = SpatialCenterNet(ScaleClassifierNet())
    original = copy.deepcopy(model.base.state_dict())
    head = model.confidence
    interrupted_head = copy.deepcopy(head)
    training = (torch.randn(17, 768), torch.tensor([0., 1.] * 8 + [0.]), torch.zeros(17))
    recipe = dict(runner.RECIPE, epochs=3, batch_size=4)
    binding = {'test_population': 'owned_disposable_spatial_fixture', 'recipe': recipe}
    full, partial = tmp_path/'full', tmp_path/'partial'
    full.mkdir()
    partial.mkdir()
    optimizer = lambda h: torch.optim.AdamW(h.parameters(), lr=recipe['learning_rate'], weight_decay=recipe['weight_decay'])
    uninterrupted = runner.train_epochs(head, optimizer(head), training, recipe, binding, full, BUDGET)
    save = linear._atomic_torch_save

    def save_then_interrupt(path, state):
        save(path, state)
        raise InterruptedError('interrupt after a durable epoch')

    monkeypatch.setattr(linear, '_atomic_torch_save', save_then_interrupt)
    with pytest.raises(InterruptedError):
        runner.train_epochs(interrupted_head, optimizer(interrupted_head), training, recipe, binding, partial, BUDGET)
    monkeypatch.setattr(linear, '_atomic_torch_save', save)
    resumed_head = copy.deepcopy(head)
    resumed = runner.train_epochs(resumed_head, optimizer(resumed_head), training, recipe, binding, partial, BUDGET,
                                  resume=partial/'recovery.pt')
    assert resumed['resumed_from_epoch'] == 1
    assert resumed['history'] == uninterrupted['history']
    assert resumed['optimizer_steps'] == uninterrupted['optimizer_steps'] == 15
    for name, value in head.state_dict().items():
        assert torch.equal(value, resumed_head.state_dict()[name])
    model.confidence = resumed_head
    model.verify_frozen_state(original)
