# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from contextlib import contextmanager

import numpy as np
import pytest
import torch

from ml.ocr.official_bakeoff import frozen_head_training as base
from ml.ocr.official_bakeoff import frozen_trunk_head as head_api
from ml.ocr.scale_coverage_db_head_v46 import inputs,runner,training

class Budget:
    def __init__(self,fail_at=None):
        self.calls=0
        self.fail_at=fail_at
    @contextmanager
    def work_block(self):
        self.calls+=1
        if self.calls==self.fail_at:
            raise RuntimeError('cancelled')
        yield

def fixture():
    rng=np.random.default_rng(73)
    constants={}
    for name,shape in head_api.TRAINABLE_CONSTANT_SHAPES.items():
        constants[name]=(rng.normal(0,0.04,shape).astype(np.float32) if len(shape)>1 else
            np.ones(shape,np.float32) if name.startswith('batch_norm') and name.endswith('w_0') else np.zeros(shape,np.float32))
    for name,shape in head_api.FROZEN_BATCH_NORM_SHAPES.items():
        constants[name]=np.ones(shape,np.float32) if name.endswith('w_2') else np.zeros(shape,np.float32)
    head=head_api.FrozenDbHead(constants)
    target=np.zeros((1,8,8),np.float32);target[:,2:6,2:6]=1
    panel=base.TrainingPanel('train','train',inputs.immutable(rng.normal(size=(1,96,2,2)).astype(np.float32)),
        inputs.immutable(target),inputs.immutable(np.ones_like(target)))
    recipe={**training.RECIPE,'epochs':2,'intraop_threads':1}
    return head,(panel,),recipe

def test_finite_adaptation_is_deterministic_and_keeps_frozen_buffers(tmp_path):
    heads=[]
    reports=[]
    for i in range(2):
        head,panels,recipe=fixture()
        before=base._frozen_bn_sha256(head)
        path=tmp_path/str(i);path.mkdir()
        reports.append(training.train(head,panels,recipe,path,Budget(),{'data':'fixed'}))
        assert base._frozen_bn_sha256(head)==before
        assert reports[-1]['optimizer_steps']==2
        assert reports[-1]['selected_epoch']==min(reports[-1]['epoch_losses'],key=lambda r:r['full_train_loss'])['epoch']
        checkpoint=torch.load(path/'recovery.pt',weights_only=True)
        for name,value in head.state_dict().items():
            assert torch.equal(value,checkpoint['best_state'][name])
        heads.append(head)
    assert reports[0]['epoch_losses']==reports[1]['epoch_losses']
    assert base._trainable_state_sha256(heads[0])==base._trainable_state_sha256(heads[1])

def test_cancelled_epoch_resumes_to_identical_parameters(tmp_path):
    baseline,panels,recipe=fixture()
    full=tmp_path/'full';full.mkdir()
    expected=training.train(baseline,panels,recipe,full,Budget(),{'data':'fixed'})
    interrupted,panels,recipe=fixture()
    partial=tmp_path/'partial';partial.mkdir()
    with pytest.raises(RuntimeError,match='cancelled'):
        training.train(interrupted,panels,recipe,partial,Budget(fail_at=3),{'data':'fixed'})
    recovery=torch.load(partial/'recovery.pt',weights_only=True)
    assert recovery['completed_epochs']==1 and recovery['optimizer_steps']==1
    resumed,panels,recipe=fixture()
    final=tmp_path/'resumed';final.mkdir()
    actual=training.train(resumed,panels,recipe,final,Budget(),{'data':'fixed'},resume=partial/'recovery.pt')
    assert actual['resumed_after_epoch']==1
    assert actual['epoch_losses']==expected['epoch_losses']
    assert base._trainable_state_sha256(resumed)==base._trainable_state_sha256(baseline)

def test_recovery_cannot_change_bound_input(tmp_path):
    head,panels,recipe=fixture()
    original=tmp_path/'original';original.mkdir()
    training.train(head,panels,recipe,original,Budget(),{'data':'fixed'})
    head,panels,recipe=fixture()
    before=base._trainable_state_sha256(head)
    final=tmp_path/'resumed';final.mkdir()
    with pytest.raises(ValueError,match='Recovery input'):
        training.train(head,panels,recipe,final,Budget(),{'data':'other'},resume=original/'recovery.pt')
    assert base._trainable_state_sha256(head)==before

def test_development_panel_never_reaches_optimizer(tmp_path):
    head,panels,recipe=fixture()
    p=panels[0]
    development=base.TrainingPanel(p.panel_id,'validation',p.features,p.target,p.mask)
    before=base._trainable_state_sha256(head)
    with pytest.raises(base.FrozenHeadTrainingError,match='cannot enter optimization'):
        training.train(head,(development,),recipe,tmp_path,Budget(),{})
    assert base._trainable_state_sha256(head)==before
    assert not (tmp_path/'recovery.pt').exists()

@pytest.mark.parametrize('key,value',[('sealed_runs_authorized',1),('private_reads',1),
    ('recipe',{**training.RECIPE,'epochs':25}),('initializer_sha256','0'*64),
    ('augmentation',{**inputs.RECIPE,'scale':3})])
def test_candidate_refuses_drift_in_fixed_recipe_or_authorization(key,value):
    config={'task':runner.TASK,'revision':runner.REVISION,'candidate_id':'P1',
        'recipe':training.RECIPE,'augmentation':inputs.RECIPE,'sealed_runs_authorized':0,
        'private_reads':0,'initializer_sha256':inputs.INITIALIZER_SHA}
    config[key]=value
    with pytest.raises(ValueError):
        runner.validate_config(config)
