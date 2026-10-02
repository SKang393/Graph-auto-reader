# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from contextlib import nullcontext
import copy
import math
from types import SimpleNamespace

import pytest
import torch

from ml.markers.center.confidence_duration_v36 import runner
from ml.markers.center.confidence_spatial_v35 import runner as previous
from ml.markers.center.confidence_spatial_v35.model import SpatialCenterNet
from ml.markers.center.scale_classifier_v16.model import ScaleClassifierNet

BUDGET = SimpleNamespace(work_block=nullcontext)


def test_only_fixed_training_duration_changes():
    assert runner.RECIPE == dict(previous.RECIPE, epochs=24)
    assert runner.TRAINING_ROWS == previous.TRAINING_ROWS
    assert runner.MODEL_SHA256 == previous.MODEL_SHA256
    assert runner.SpatialCenterNet is previous.SpatialCenterNet
    assert runner.load_training is previous.load_training
    assert runner.train_epochs is previous.train_epochs
    assert math.ceil(sum(runner.TRAINING_ROWS.values()) / runner.RECIPE['batch_size']) * 24 == 51888


@pytest.mark.parametrize('defect', ['epochs', 'threshold', 'objective', 'model', 'population', 'private', 'sealed', 'approval'])
def test_duration_trial_rejects_changed_recipe_or_authorization(defect):
    config = {'task': runner.TASK, 'revision': runner.REVISION, 'candidate_id': 'P1',
        'recipe': dict(runner.RECIPE), 'training_group_rows': dict(runner.TRAINING_ROWS),
        'source_checkpoint_sha256': runner.MODEL_SHA256, 'private_reads': 0,
        'sealed_runs_authorized': 0, 'production_approval': False}
    runner.validate_config(config)
    if defect in ('epochs', 'threshold', 'objective'):
        key = {'epochs': 'epochs', 'threshold': 'confidence_threshold', 'objective': 'confidence_objective'}[defect]
        config['recipe'][key] = {'epochs': 8, 'threshold': .2, 'objective': 'different'}[defect]
    elif defect == 'model':
        config['source_checkpoint_sha256'] = 'different'
    elif defect == 'population':
        config['training_group_rows']['component'] -= 1
    else:
        key = {'private': 'private_reads', 'sealed': 'sealed_runs_authorized', 'approval': 'production_approval'}[defect]
        config[key] = True
    with pytest.raises(ValueError):
        runner.validate_config(config)


def test_longer_fit_preserves_seeded_prefix_and_exact_recovery(tmp_path):
    torch.manual_seed(111)
    model = SpatialCenterNet(ScaleClassifierNet())
    original = copy.deepcopy(model.base.state_dict())
    full_head = model.confidence
    short_head = copy.deepcopy(full_head)
    resumed_head = copy.deepcopy(full_head)
    training = (torch.randn(17, 768), torch.tensor([0., 1.] * 8 + [0.]), torch.zeros(17))
    recipe = dict(runner.RECIPE, batch_size=4)
    binding = {'fixture': 'owned_disposable_duration_recovery', 'recipe': recipe}
    full, short, resumed = [tmp_path/name for name in ('full', 'short', 'resumed')]
    for path in (full, short, resumed):
        path.mkdir()
    optimizer = lambda head: torch.optim.AdamW(head.parameters(), lr=recipe['learning_rate'], weight_decay=recipe['weight_decay'])
    all_epochs = runner.train_epochs(full_head, optimizer(full_head), training, recipe, binding, full, BUDGET)
    prefix = runner.train_epochs(short_head, optimizer(short_head), training, dict(recipe, epochs=8), binding, short, BUDGET)
    recovery = runner.train_epochs(resumed_head, optimizer(resumed_head), training, recipe, binding, resumed, BUDGET,
                                   resume=short/'recovery.pt')
    assert prefix['history'] == all_epochs['history'][:8]
    assert prefix['optimizer_steps'] == 40
    assert recovery['resumed_from_epoch'] == 8
    assert all_epochs['optimizer_steps'] == recovery['optimizer_steps'] == 120
    assert recovery['history'] == all_epochs['history']
    for key, value in full_head.state_dict().items():
        assert torch.equal(value, resumed_head.state_dict()[key])
    model.verify_frozen_state(original)
