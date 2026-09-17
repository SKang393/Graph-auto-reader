# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from types import SimpleNamespace
from pathlib import Path
import numpy as np
import pytest
from ml.ocr.text_extent_db_head_v44 import train_p1 as runner


@pytest.mark.parametrize('observation', [
    (12,5,334,0x40,4095), (12,1,333,0x40,4095),
    (12,5,333,0x20,4095), (12,5,333,0x40,1),
    (16,5,333,0x40,4095), (12,5,0,0x40,4095),
])
def test_cpu_guard_rejects_an_unbound_or_excessive_budget(observation):
    with pytest.raises(runner.TextExtentTrainingError):
        runner._validate_cpu_observation(*observation)


def test_cpu_guard_accepts_the_recorded_replacement_budget():
    assert runner._validate_cpu_observation(12,5,333,0x40,4095)['job_cpu_rate']==333


def _inputs():
    panels=[]
    features={}
    for index in range(28):
        identity=f'panel-{index:02}'
        projections=[SimpleNamespace(panel_box=(4,4,16,12)) for _ in range(25 if index<27 else 34)]
        panels.append(SimpleNamespace(split='train',panel_id=identity,width=32,height=32,
            tensor_shape=(1,3,32,32),tensor_sha256='a'*64,projections=projections))
        features[identity]=(np.zeros((1,runner.frozen_trunk_head.FEATURE_CHANNELS,8,8),dtype=np.float32),'a'*64)
    def split(sources,panel_count,truths,rows):
        return SimpleNamespace(source_count=sources,panel_count=panel_count,full_source_truth_count=truths,
            projected_source_truth_count=truths,outside_runtime_crop_truth_count=0,
            partial_source_truth_count=0,overlapping_source_truth_count=0,panels=rows)
    return SimpleNamespace(train=split(20,28,709,panels),dev=split(3,9,183,())),features


def test_inverse_targets_preserve_all_train_labels_and_full_masks():
    inputs,features=_inputs()
    panels,digest=runner._training_panels(inputs,features)
    assert len(panels)==28 and len(digest)==64
    assert all(p.split=='train' and np.all(p.mask==1) and np.any(p.target==1) for p in panels)
    assert all(set(np.unique(p.target))=={0,1} for p in panels)
    assert all(not p.mask.flags.writeable and not p.target.flags.writeable for p in panels)


@pytest.mark.parametrize('defect',['foreign_split','changed_input','lost_truth'])
def test_target_join_rejects_cross_split_or_incomplete_evidence(defect):
    inputs,features=_inputs()
    if defect=='foreign_split': inputs.train.panels[0].split='validation'
    elif defect=='changed_input': inputs.train.panels[0].tensor_sha256='b'*64
    else: inputs.train.panels[0].projections.pop()
    with pytest.raises(runner.TextExtentTrainingError):
        runner._training_panels(inputs,features)


def test_training_checks_cpu_guard_before_authorization(tmp_path,monkeypatch):
    def fail(): raise runner.TextExtentTrainingError('budget unavailable')
    def forbidden(*args,**kwargs): pytest.fail('authorization opened without CPU protection')
    monkeypatch.setattr(runner,'_cpu_budget',fail)
    monkeypatch.setattr(runner.training_budget,'acquire_training_candidate',forbidden)
    with pytest.raises(runner.TextExtentTrainingError,match='budget unavailable'):
        runner.train_candidate('artifacts/run',tmp_path)
    assert not (tmp_path/'artifacts/run').exists()


def test_presealed_failure_is_voided_even_when_failure_report_cannot_be_written(tmp_path, monkeypatch):
    authorization = object()
    original = runner.TextExtentTrainingError('preparation failed')
    observed = []
    monkeypatch.setattr(runner, '_cpu_budget', lambda: {})
    monkeypatch.setattr(runner, '_config', lambda root: {})
    monkeypatch.setattr(runner.training_budget, 'acquire_training_candidate', lambda *a, **k: authorization)
    def fail_prepare(root): raise original
    def fail_write(self, payload): raise OSError('disk full')
    monkeypatch.setattr(runner, 'prepare_training', fail_prepare)
    monkeypatch.setattr(Path, 'write_bytes', fail_write)
    monkeypatch.setattr(runner.training_budget, 'void_candidate', lambda auth, error: observed.append((auth,error)))
    with pytest.raises(OSError, match='disk full'):
        runner.train_candidate('artifacts/run', tmp_path)
    assert observed == [(authorization, original)]


def test_resume_source_requires_the_exact_local_checkpoint(tmp_path):
    directory = tmp_path / 'artifacts'
    directory.mkdir()
    checkpoint = directory / 'recovery.pt'
    checkpoint.write_bytes(b'saved training state')
    digest = runner.sha256_file(checkpoint)
    assert runner._resume_source(tmp_path, checkpoint, digest) == checkpoint
    assert runner._resume_source(tmp_path, None, None) is None
    for path, checksum in [(checkpoint, None), (None, digest), (checkpoint, '0'*64),
                           (directory/'missing.pt', digest)]:
        with pytest.raises(runner.TextExtentTrainingError):
            runner._resume_source(tmp_path, path, checksum)
    outside = tmp_path / 'outside.pt'
    outside.write_bytes(checkpoint.read_bytes())
    with pytest.raises(runner.TextExtentTrainingError):
        runner._resume_source(tmp_path, outside, digest)


def test_training_resume_copies_and_binds_recovery_without_mutating_source(tmp_path, monkeypatch):
    artifacts = tmp_path / 'artifacts'
    artifacts.mkdir()
    prior = artifacts / 'prior.pt'
    prior.write_bytes(b'prior recovery state')
    digest = runner.sha256_file(prior)
    output = artifacts / 'next'
    binding = {'candidate_config_sha256':'a'*64, 'source_snapshot_sha256':'s'*64}
    authorization = SimpleNamespace(binding=binding, snapshot_path=artifacts/'snapshot.json')
    prepared = runner.PreparedTraining({'bound_files':{'parent_model':{}}}, 'a'*64, (), 'b'*64, 'c'*64, 0.0)
    monkeypatch.setattr(runner, '_cpu_budget', lambda: {})
    monkeypatch.setattr(runner, '_config', lambda root: {})
    monkeypatch.setattr(runner, 'prepare_training', lambda root: prepared)
    monkeypatch.setattr(runner.training_budget, 'acquire_training_candidate', lambda *a, **k: authorization)
    monkeypatch.setattr(runner, 'verify_bound_source_snapshot', lambda *a: None)
    monkeypatch.setattr(runner, '_bound', lambda *a: artifacts/'parent.onnx')
    monkeypatch.setattr(runner.frozen_trunk_head, 'extract_frozen_trunk_head', lambda *a: SimpleNamespace(head=object()))
    def train(head, panels, **options):
        assert options['checkpoint_path'] == output/'recovery.pt'
        assert options['checkpoint_path'].read_bytes() == prior.read_bytes()
        assert options['checkpoint_binding'] == {'candidate_config_sha256':'a'*64,
            'feature_inventory_sha256':'b'*64, 'target_inventory_sha256':'c'*64}
        options['checkpoint_path'].write_bytes(b'new recovery state')
        return SimpleNamespace(optimizer_steps=6720, epochs=240, intraop_threads=12,
            frozen_batch_norm_sha256_before='d'*64, frozen_batch_norm_sha256_after='d'*64)
    monkeypatch.setattr(runner.engine, 'train_resource_limited_head', train)
    monkeypatch.setattr(runner.v39, '_write_checkpoint', lambda path, head: path.write_bytes(b'selected'))
    monkeypatch.setattr(runner.frozen_trunk_head, 'patch_head_constants', lambda parent, path, head: path.write_bytes(b'model'))
    monkeypatch.setattr(runner.v40, '_validate_patch', lambda *a: None)
    monkeypatch.setattr(runner, 'asdict', vars)
    report = runner.train_candidate(output, tmp_path, resume_checkpoint=prior, resume_checkpoint_sha256=digest)
    assert prior.read_bytes() == b'prior recovery state'
    assert report['resume_checkpoint'] == {'path':'artifacts/prior.pt', 'sha256':digest}
    assert report['status'] == 'trained_pending_export_parity_and_dev'
