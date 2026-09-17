# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Exercise candidate admission without weights, private data, or sealed reads."""
import json
from pathlib import Path

import pytest

from ml.ocr.text_extent_db_head_v44 import prepare_candidate as candidate
from ml.ocr.text_extent_db_head_v44.tests.test_verify_export import _result


def _fixture(tmp_path, monkeypatch):
    def write(name, value):
        path = tmp_path / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(value if isinstance(value, bytes) else candidate.canonical_json_bytes(value))
        return {'path': name, 'sha256': candidate.sha256_file(path)}

    config = write(candidate.runner.CONFIG_PATH.as_posix(), {})
    capture = write('artifacts/capture.json', {'panels': [
        {'panel_id': str(i), 'split': 'train' if i < 28 else 'dev',
         'tensor': {'sha256': f'{i:064x}'}} for i in range(37)]})
    monkeypatch.setattr(candidate.runner, '_config', lambda root: {'bound_files': {'capture_report': capture}})
    # Snapshot validation has dedicated canonical-policy tests. This fixture
    # isolates the candidate handoff and never opens a registered reserve.
    monkeypatch.setattr(candidate, 'verify_bound_source_snapshot', lambda *args: None)
    model = write('artifacts/run/detector-text-extent-v44-p1.onnx', b'fixture model')
    checkpoint = write('artifacts/run/selected-head.pt', b'fixture checkpoint')
    stage = {
        'schema': 'graphreader.ocr-text-extent-v44-training-stage.v1', 'task': candidate.runner.TASK,
        'status': 'trained_pending_export_parity_and_dev', 'revision': candidate.runner.REVISION,
        'candidate_id': 'P1', 'production_approved': False, 'private_reads': 0, 'sealed_reads': 0,
        'training_authorization': {'candidate_config_sha256': config['sha256'],
            'source_snapshot_path': 'artifacts/snapshot.json', 'source_snapshot_sha256': 'a'*64},
        'training': _result(), 'model_sha256': model['sha256'],
        'checkpoint_sha256': checkpoint['sha256'], 'feature_inventory_sha256': 'b'*64}
    stage_file = write('artifacts/run/stage.json', stage)
    rows = [{'panel_id': str(i), 'split': 'train' if i < 28 else 'dev',
             'input_sha256': f'{i:064x}', 'maximum_absolute_error': 0.0} for i in range(37)]
    parity = {'schema': 'graphreader.ocr-text-extent-v44-trained-export-parity.v1',
        'stage': stage_file, 'source_sha256': candidate.sha256_file(Path(candidate.verify_export.__file__)),
        'model_sha256': model['sha256'], 'checkpoint_sha256': checkpoint['sha256'],
        'feature_inventory_sha256': 'b'*64, 'panel_count': 37, 'tolerance': 1e-5,
        'torch_threads': 12, 'onnx_threads': 1, 'graph_optimization': 'ORT_ENABLE_ALL',
        'optimizer_steps': 0, 'private_reads': 0, 'sealed_reads': 0,
        'production_approved': False, 'passed': True, 'panels': rows, 'maximum_absolute_error': 0.0}
    manifest = write('artifacts/base/manifest.json', {'preprocessing': {'unchanged': True},
        'postprocessing': {'unchanged': True}, 'license': {'spdx': 'Apache-2.0'}})
    native = write('artifacts/base/native.dll', b'fixture native')
    assembly = write('artifacts/base/assembly.dll', b'fixture assembly')
    license_file = write('LICENSES/fixture.txt', b'fixture license')
    role = {'model_path': model['path'], 'model_sha256': model['sha256'],
            'manifest_path': manifest['path'], 'manifest_sha256': manifest['sha256']}
    base = write(candidate.BASE, {'scope': 'synthetic-fixture', 'production_approved': False,
        'training_input_ready': False, 'native_path': native['path'], 'native_sha256': native['sha256'],
        'execution_assemblies': [assembly], 'license_inputs': [license_file],
        'detector': role, 'recognizer': role})
    monkeypatch.setattr(candidate, 'BASE_SHA', base['sha256'])
    return write, stage_file, parity, native


def _prepare(tmp_path, write, stage, parity):
    bound = write('artifacts/parity.json', parity)
    return candidate.prepare(stage['path'], stage['sha256'], bound['path'], bound['sha256'],
                             'artifacts/candidate', tmp_path)


def test_candidate_preserves_runtime_processing_and_unapproved_status(tmp_path, monkeypatch):
    write, stage, parity, _ = _fixture(tmp_path, monkeypatch)
    result = _prepare(tmp_path, write, stage, parity)
    path = tmp_path / result['path']
    assert candidate.sha256_file(path) == result['sha256']
    value = json.loads(path.read_text())
    manifest = json.loads((tmp_path/value['detector']['manifest_path']).read_text())
    assert value['production_approved'] is False and value['training_input_ready'] is False
    assert manifest['preprocessing'] == {'unchanged': True}
    assert manifest['postprocessing'] == {'unchanged': True}
    assert manifest['files'] == [Path(value['detector']['model_path']).name]
    assert value['detector']['model_id'] == candidate.MODEL_ID
    assert len(value['license_inputs']) == 2


@pytest.mark.parametrize('defect', ['missing', 'duplicate', 'input', 'error', 'nonfinite', 'boolean', 'maximum', 'passed', 'source'])
def test_candidate_rejects_incomplete_or_invalid_parity(tmp_path, monkeypatch, defect):
    write, stage, parity, _ = _fixture(tmp_path, monkeypatch)
    if defect == 'missing': parity['panels'].pop()
    elif defect == 'duplicate': parity['panels'][-1] = parity['panels'][0]
    elif defect == 'input': parity['panels'][0]['input_sha256'] = 'c'*64
    elif defect == 'error': parity['panels'][0]['maximum_absolute_error'] = .01
    elif defect == 'nonfinite': parity['panels'][0]['maximum_absolute_error'] = float('inf')
    elif defect == 'boolean': parity['panels'][0]['maximum_absolute_error'] = False
    elif defect == 'maximum': parity['maximum_absolute_error'] = False
    elif defect == 'passed': parity['passed'] = 1
    else: parity['source_sha256'] = 'd'*64
    with pytest.raises(ValueError):
        _prepare(tmp_path, write, stage, parity)
    assert not (tmp_path/'artifacts/candidate').exists()


def test_candidate_rejects_changed_runtime_before_copy(tmp_path, monkeypatch):
    write, stage, parity, native = _fixture(tmp_path, monkeypatch)
    (tmp_path/native['path']).write_bytes(b'changed')
    with pytest.raises(ValueError):
        _prepare(tmp_path, write, stage, parity)
    assert not (tmp_path/'artifacts/candidate').exists()


@pytest.mark.parametrize('key,value', [('schema', 'other'), ('task', 'other'),
    ('production_approved', True), ('sealed_reads', False), ('private_reads', 1)])
def test_candidate_rejects_wrong_training_scope(tmp_path, monkeypatch, key, value):
    write, stage, parity, _ = _fixture(tmp_path, monkeypatch)
    document = json.loads((tmp_path/stage['path']).read_text())
    document[key] = value
    stage = write(stage['path'], document)
    parity['stage'] = stage
    with pytest.raises(ValueError):
        _prepare(tmp_path, write, stage, parity)
    assert not (tmp_path/'artifacts/candidate').exists()
