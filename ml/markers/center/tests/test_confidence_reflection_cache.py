# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import json

import numpy as np
import pytest
import torch

from ml.markers.gate_seal import sha256_file
from ml.markers.center.confidence_reflection_v37 import cache
from ml.markers.center.confidence_reflection_v37.augmentation import TRANSFORMS
from ml.markers.center.tests.test_confidence_spatial_cache import BUDGET, feature_cache


@pytest.fixture
def reflected_cache(feature_cache):
    root, parent_path, parent = feature_cache
    reference = lambda path: {'path': path.name, 'sha256': sha256_file(path)}
    groups = []
    for source in parent['groups']:
        for index, transform in enumerate(TRANSFORMS):
            group = dict(source, scope=f"{source['scope']}:{transform}", base_scope=source['scope'], transform=transform)
            if index:
                for name in ('features', 'raw'):
                    path = root/f"{source['scope']}-{transform}-{name}.npy"
                    np.save(path, np.load(root/source[name]['path'])+np.float32(index), allow_pickle=False)
                    group[name] = reference(path)
            groups.append(group)
    report = {'status':'fixed_reflection_training_features_measured', 'feature_width':768,
        'transforms':list(TRANSFORMS), 'original_training_rows':5, 'training_rows':20,
        'all_original_rows_and_labels_retained':True, 'model_parameters_and_buffers_unchanged':True,
        'raw_reconstruction_max_absolute_error':0, 'model':parent['model'],
        'parent_feature_report':reference(parent_path), 'groups':groups, 'evidence':parent['evidence'],
        'optimizer_steps':0, 'development_tensor_reads':0, 'private_reads':0, 'sealed_reads':0}
    path = root/'reflected-report.json'
    path.write_text(json.dumps(report))
    return root,path,report


def load_fixture(fixture):
    root,path,report = fixture
    expected = {f'{scope}:{transform}':count for scope,count in [('component',3),('family',2)] for transform in TRANSFORMS}
    return cache.load_training(root,path,sha256_file(path),expected,report['model']['sha256'],BUDGET)


def test_every_original_and_reflected_row_is_retained_in_fixed_order(reflected_cache):
    root,_,report = reflected_cache
    training,actual = load_fixture(reflected_cache)
    assert actual == report
    for value,name in zip(training,('features','labels','hard_negative'),strict=True):
        expected = np.concatenate([np.load(root/group[name]['path']) for group in report['groups']])
        assert value.dtype == torch.float32
        np.testing.assert_array_equal(value.numpy(),expected)
    assert len(training[1]) == 20 and int(training[1].sum()) == 8


@pytest.mark.parametrize('defect', ['drop_group','reorder','transform','row_count','labels','identity_features',
    'original_training_rows','training_rows','feature_width','optimizer_steps','private_reads','sealed_reads',
    'development_tensor_reads','all_original_rows_and_labels_retained','model_parameters_and_buffers_unchanged'])
def test_rebound_manifest_cannot_change_population_or_labels(reflected_cache,defect):
    root,path,report = reflected_cache
    if defect == 'drop_group':
        report['groups'].pop()
    elif defect == 'reorder':
        report['groups'].reverse()
    elif defect == 'transform':
        report['groups'][1]['transform'] = 'vertical'
    elif defect == 'row_count':
        report['groups'][1]['rows'] -= 1
    elif defect == 'labels':
        report['groups'][1]['labels'] = report['groups'][-1]['labels']
    elif defect == 'identity_features':
        report['groups'][0]['features'] = report['groups'][1]['features']
    else:
        report[defect] = False if isinstance(report[defect],bool) else -1
    path.write_text(json.dumps(report))
    with pytest.raises(ValueError,match='changed'):
        load_fixture(reflected_cache)


@pytest.mark.parametrize('target', ['features','raw','parent','evidence'])
def test_tampered_preparation_inputs_are_rejected(reflected_cache,target):
    root,_,report = reflected_cache
    item = report['parent_feature_report'] if target=='parent' else (
        report['evidence'][0] if target=='evidence' else report['groups'][1][target])
    with (root/item['path']).open('ab') as stream:
        stream.write(b'changed')
    with pytest.raises(ValueError,match='changed'):
        load_fixture(reflected_cache)


@pytest.mark.parametrize('name,defect', [('features','nan'),('features','shape'),('features','dtype'),('raw','nan'),('raw','shape'),('raw','dtype')])
def test_malformed_reflected_arrays_are_rejected_after_rebinding(reflected_cache,name,defect):
    root,path,report = reflected_cache
    item = report['groups'][1][name]
    payload = root/item['path']
    value = np.load(payload)
    if defect=='nan':
        value[0,0] = np.nan
    elif defect=='shape':
        value = value[:,:-1]
    else:
        value = value.astype(np.float64)
    np.save(payload,value,allow_pickle=False)
    item['sha256'] = sha256_file(payload)
    path.write_text(json.dumps(report))
    with pytest.raises(ValueError,match='contract changed'):
        load_fixture(reflected_cache)
