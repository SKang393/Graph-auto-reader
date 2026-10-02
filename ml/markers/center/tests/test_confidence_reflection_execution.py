# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import math

import pytest

from ml.markers.center.confidence_reflection_v37 import runner
from ml.markers.center.confidence_spatial_v35 import runner as previous
from ml.markers.center.confidence_reflection_v37.augmentation import TRANSFORMS


def test_only_training_population_changes_relative_to_v35():
    assert runner.RECIPE == previous.RECIPE
    assert runner.MODEL_SHA256 == previous.MODEL_SHA256
    assert runner.SpatialCenterNet is previous.SpatialCenterNet
    assert runner.train_epochs is previous.train_epochs
    assert runner.TRAINING_ROWS == {f'{scope}:{transform}':count for scope,count in previous.TRAINING_ROWS.items() for transform in TRANSFORMS}
    assert sum(runner.TRAINING_ROWS.values()) == 1106472
    assert math.ceil(sum(runner.TRAINING_ROWS.values())/runner.RECIPE['batch_size'])*runner.RECIPE['epochs'] == 69160


@pytest.mark.parametrize('defect', ['epochs','threshold','objective','model','population','private','sealed','approval'])
def test_reflection_trial_rejects_changed_recipe_or_authorization(defect):
    config = {'task':runner.TASK,'revision':runner.REVISION,'candidate_id':'P1',
        'recipe':dict(runner.RECIPE),'training_group_rows':dict(runner.TRAINING_ROWS),
        'source_checkpoint_sha256':runner.MODEL_SHA256,'private_reads':0,
        'sealed_runs_authorized':0,'production_approval':False}
    runner.validate_config(config)
    if defect in ('epochs','threshold','objective'):
        key = {'epochs':'epochs','threshold':'confidence_threshold','objective':'confidence_objective'}[defect]
        config['recipe'][key] = {'epochs':24,'threshold':.2,'objective':'different'}[defect]
    elif defect=='model':
        config['source_checkpoint_sha256'] = 'different'
    elif defect=='population':
        config['training_group_rows']['component:identity'] -= 1
    else:
        config[{'private':'private_reads','sealed':'sealed_runs_authorized','approval':'production_approval'}[defect]] = True
    with pytest.raises(ValueError):
        runner.validate_config(config)
