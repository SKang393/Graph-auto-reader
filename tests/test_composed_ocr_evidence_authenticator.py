# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Identity and tamper checks for the owned-development replay boundary."""
from copy import deepcopy
from dataclasses import replace
import base64
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).parents[1] / 'tools/GraphReader.SyntheticRuntimeEvidence'))
import authenticate_composed_ocr_evidence as adapter
from ml.markers.gate_seal import sha256_file
from ml.policy import ocr_sealed_evaluation as parent
import test_composed_ocr_sealed_evaluation as composed
import test_ocr_sealed_evaluation as legacy


def _fixture(tmp_path, monkeypatch):
    root, _, old_evidence, score = composed.fixture(tmp_path)
    folder = root/'artifacts/goal22-runs/check'
    runtime = folder/'runtime'
    runtime.mkdir(parents=True)
    reference = lambda p: {'path': p.relative_to(root).as_posix(), 'sha256': sha256_file(p)}
    def write(path, value):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value), encoding='utf-8')
        return reference(path)
    assemblies = []
    for index in range(4):
        assembly = runtime/f'assembly-{index}.dll'
        assembly.write_bytes(f'assembly-{index}'.encode())
        assemblies.append({'name': f'Assembly{index}', **reference(assembly)})
    apphost = runtime/'GraphReader.SyntheticRuntimeEvidence.exe'
    apphost.write_bytes(b'handwritten fixture, never executed')
    candidate = folder/'candidate.json'
    write(candidate, {'schema': 'graphreader.frozen-composed-ocr-candidate.v1',
                      'execution_assemblies': assemblies})
    sources = []
    for index in range(23):
        image = folder/f'source-{index}/image.png'
        image.parent.mkdir()
        image.write_bytes(f'fixture image {index}'.encode())
        sources.append({'split': 'train' if index < 20 else 'dev', 'image': reference(image),
                        'annotation': write(image.parent/'annotation.json', {'fixture': index}),
                        'scene': write(image.parent/'scene.json', {'fixture': index})})
    generator = write(folder/'generator.json', {'sources': sources, 'source_count': 23,
        'private_reads': 0, 'sealed_reads': 0, 'production_approved': False})
    monkeypatch.setattr(adapter, 'GENERATOR_SHA256', generator['sha256'])
    request = folder/'request.json'
    write(request, {'schema': 'graphreader.composed-ocr-memory-dev-check.v1',
        'scope': 'owned-synthetic-development', 'generator': generator,
        'source_count': 23, 'private_reads': 0, 'sealed_reads': 0})
    score.update(request_sha256=sha256_file(request), candidate_sha256=sha256_file(candidate))
    score_path = folder/'score.json'
    write(score_path, score)
    monkeypatch.setattr(parent, '_canonical_bars', lambda _: legacy._bars())
    authenticator = adapter.ComposedOcrEvidenceAuthenticator(
        score_path, sha256_file(score_path), apphost, request, sha256_file(request))
    calls = []
    def replay(*args):
        calls.append(args)
        value = deepcopy(score)
        value['elapsed_milliseconds'] = 7
        return value
    monkeypatch.setattr(adapter, '_replay', replay)
    return root, candidate, authenticator, score, calls


def test_replay_binds_every_runtime_file_and_all_development_sources(tmp_path, monkeypatch):
    root, candidate, authenticator, _, calls = _fixture(tmp_path, monkeypatch)
    evidence = authenticator.authenticate(repository_root=root, candidate_path=candidate,
                                           candidate_sha256=sha256_file(candidate))
    assert len(calls) == 1 and len(evidence.runtime_files) == 5
    assert evidence.schema == parent.COMPOSED_EVIDENCE_SCHEMA
    assert evidence.runtime_identity_sha256 == parent._runtime_identity(evidence, root, candidate)


@pytest.mark.parametrize('change', ['runtime', 'input', 'score', 'candidate', 'recomputed'])
def test_changes_during_replay_cannot_authenticate(tmp_path, monkeypatch, change):
    root, candidate, authenticator, score, _ = _fixture(tmp_path, monkeypatch)
    def replay(*args):
        if change == 'runtime':
            (authenticator.apphost_path.parent/'assembly-1.dll').write_bytes(b'changed')
        elif change == 'input':
            (candidate.parent/'source-0/image.png').write_bytes(b'changed')
        elif change == 'score':
            authenticator.score_path.write_bytes(b'changed')
        elif change == 'candidate':
            candidate.write_bytes(b'changed')
        value = deepcopy(score)
        if change == 'recomputed':
            value['metrics']['validation']['panel_count'] -= 1
        return value
    monkeypatch.setattr(adapter, '_replay', replay)
    with pytest.raises(parent.OcrSealedEvaluationError):
        authenticator.authenticate(repository_root=root, candidate_path=candidate,
                                  candidate_sha256=sha256_file(candidate))


@pytest.mark.parametrize('field,value', [('scope', 'private'), ('sealed_reads', 1),
    ('private_reads', True), ('source_count', 22)])
def test_invalid_development_scope_is_rejected_before_inference(tmp_path, monkeypatch, field, value):
    root, candidate, authenticator, _, calls = _fixture(tmp_path, monkeypatch)
    request = json.loads(authenticator.development_request_path.read_text())
    request[field] = value
    authenticator.development_request_path.write_text(json.dumps(request))
    with pytest.raises(parent.OcrSealedEvaluationError):
        adapter._development_inputs(root, authenticator.development_request_path,
                                    sha256_file(authenticator.development_request_path))
    assert calls == []


def test_unbound_generator_is_rejected_before_opening_its_payload(tmp_path, monkeypatch):
    root, _, authenticator, _, calls = _fixture(tmp_path, monkeypatch)
    request = json.loads(authenticator.development_request_path.read_text())
    request['generator'] = {'path': 'artifacts/private/never-open.json', 'sha256': '0'*64}
    authenticator.development_request_path.write_text(json.dumps(request))
    with pytest.raises(parent.OcrSealedEvaluationError, match='PREFLIGHT_INVALID'):
        adapter._development_inputs(root, authenticator.development_request_path,
                                    sha256_file(authenticator.development_request_path))
    assert calls == []


def _v2_fixture(tmp_path, monkeypatch):
    root, candidate, authenticator, score, _ = _fixture(tmp_path, monkeypatch)
    runtime = authenticator.apphost_path.parent
    hashes = {}
    for name in sorted(parent._COMPOSED_SHARED_FILES):
        path = runtime / name
        path.write_bytes(name.encode())
        hashes[name] = sha256_file(path)
    model_hashes = ['a' * 64, 'b' * 64, 'c' * 64, 'd' * 64]
    content = json.loads(candidate.read_text())
    content.update(composition_version='composition', native_sha256=hashes['OpenCvSharpExtern.dll'],
                   detector={'model_sha256': model_hashes[0], 'manifest_sha256': model_hashes[1]},
                   recognizer={'model_sha256': model_hashes[2], 'manifest_sha256': model_hashes[3]})
    candidate.write_text(json.dumps(content))
    validator = candidate.parent / 'validator' / 'GraphReader.RealAcceptance.Ocr.exe'
    validator.parent.mkdir()
    validator.write_bytes(b'synthetic native validator')
    workflow = candidate.parent / 'workflow.json'
    workflow.write_text(json.dumps({
        'managed_files': [],
        'native_files': [{'file': validator.relative_to(root).as_posix(), 'sha256': sha256_file(validator)}],
        'algorithms': {'ocr_composition_version': 'composition', 'axis_stage_version': 'axis'},
        'runtime': {'execution_provider': 'cpu', 'graph_optimization': 'disabled',
                    'intra_operation_threads': 1, 'inter_operation_threads': 1, 'worker_count': 1, 'queue_capacity': 1},
        'ocr_detection': {'payload': {'sha256': model_hashes[0]}, 'manifest': {'sha256': model_hashes[1]}},
        'ocr_recognition': {'payload': {'sha256': model_hashes[2]}, 'manifest': {'sha256': model_hashes[3]}}}))
    descriptor = {'domain': 'graphreader.composed-ocr-execution.v1', 'composition': 'composition',
                  'axisStage': 'axis', 'detector': model_hashes[0], 'detectorManifest': model_hashes[1],
                  'recognizer': model_hashes[2], 'recognizerManifest': model_hashes[3], 'provider': 'cpu',
                  'optimization': 'disabled', 'intraThreads': 1, 'interThreads': 1, 'workers': 1, 'queue': 1,
                  'files': hashes, 'framework': '10.0.0', 'architecture': 'X64'}
    raw = json.dumps(descriptor, separators=(',', ':')).encode()
    score.update(schema=parent.COMPOSED_SCORE_SCHEMA_V2, candidate_sha256=sha256_file(candidate),
                 execution_sha256=hashlib.sha256(raw).hexdigest())
    authenticator.score_path.write_text(json.dumps(score))
    authenticator = replace(authenticator, score_sha256=sha256_file(authenticator.score_path),
                            workflow_candidate_path=workflow, workflow_candidate_sha256=sha256_file(workflow),
                            native_validator_path=validator, native_validator_sha256=sha256_file(validator))
    response = {'schema': 'graphreader.composed-ocr-dev-compatible-descriptor.v1',
                'status': 'compatible_development_evidence', 'scope': 'owned-synthetic-development-runtime-compatibility',
                'execution_sha256': score['execution_sha256'], 'execution_descriptor_base64': base64.b64encode(raw).decode(),
                'development': score['metrics']['validation'], 'accuracy_gate_evaluated': False,
                'stage_admission_granted': False, 'production_approved': False, 'model_inference': False,
                'private_reads': 0, 'sealed_reads': 0}
    class Result:
        returncode = 0
        stderr = b''
        @property
        def stdout(self):
            return json.dumps(response).encode()
    monkeypatch.setattr(parent.subprocess, 'run', lambda *a, **kw: Result())
    monkeypatch.setattr(adapter, '_replay', lambda *a, **kw: deepcopy(score))
    return root, candidate, authenticator, response


def test_v2_authenticates_native_descriptor_and_complete_worker(tmp_path, monkeypatch):
    root, candidate, authenticator, response = _v2_fixture(tmp_path, monkeypatch)
    evidence = authenticator.authenticate(repository_root=root, candidate_path=candidate,
                                           candidate_sha256=sha256_file(candidate))
    assert evidence.native_execution.execution_sha256 == response['execution_sha256']
    assert len(evidence.runtime_files) == len(parent._COMPOSED_SHARED_FILES) + 5
    binding_hash, binding = parent._preflight_binding(
        evidence, root=root, registry_sha256='1' * 64, set_id='2' * 64,
        gate_seal=SimpleNamespace(key='3' * 64, binding={'gate': 'fixture'}),
        training_authorization=SimpleNamespace(binding={'training': 'fixture'}))
    assert binding['schema'] == parent.COMPOSED_PREFLIGHT_SCHEMA_V2
    assert binding['native_execution']['execution_sha256'] == response['execution_sha256']
    assert binding['native_execution']['sealed_command_mode'] == '--evaluate-composed-ocr-sealed'
    assert len(binding['native_execution']['worker_files']) == len(evidence.runtime_files)
    outcome = parent._outcome(status='void', read_status='unread',
        admission=SimpleNamespace(admission_id='4' * 64), attempt_id=binding_hash,
        preflight_binding_sha256=binding_hash, preflight_binding=binding,
        request_relative=None, request_sha256=None, aggregate=None, verdicts=None,
        transport=None, failure_code=None)
    assert outcome['schema'] == parent.COMPOSED_OUTCOME_SCHEMA_V2
    assert outcome['native_execution'] == binding['native_execution']


@pytest.mark.parametrize('change', ['missing', 'extra', 'hash', 'development', 'flag', 'worker'])
def test_v2_rejects_unbound_or_malformed_native_identity(tmp_path, monkeypatch, change):
    root, candidate, authenticator, response = _v2_fixture(tmp_path, monkeypatch)
    if change == 'missing':
        del response['execution_descriptor_base64']
    elif change == 'extra':
        response['unexpected'] = True
    elif change == 'hash':
        response['execution_sha256'] = '0' * 64
    elif change == 'development':
        response['development'] = {}
    elif change == 'flag':
        response['sealed_reads'] = True
    else:
        (authenticator.apphost_path.parent / 'OpenCvSharp.dll').write_bytes(b'drift')
    with pytest.raises(parent.OcrSealedEvaluationError):
        authenticator.authenticate(repository_root=root, candidate_path=candidate,
                                   candidate_sha256=sha256_file(candidate))


@pytest.mark.parametrize('change', ['missing_field', 'extra_field', 'model_mismatch', 'file_mismatch'])
def test_v2_rejects_descriptor_that_is_self_consistent_but_not_bound(tmp_path, monkeypatch, change):
    root, candidate, authenticator, response = _v2_fixture(tmp_path, monkeypatch)
    descriptor = json.loads(base64.b64decode(response['execution_descriptor_base64']))
    if change == 'missing_field':
        del descriptor['framework']
    elif change == 'extra_field':
        descriptor['extra'] = 'value'
    elif change == 'model_mismatch':
        descriptor['detector'] = 'f' * 64
    else:
        descriptor['files']['OpenCvSharp.dll'] = 'f' * 64
    raw = json.dumps(descriptor, separators=(',', ':')).encode()
    response['execution_descriptor_base64'] = base64.b64encode(raw).decode()
    response['execution_sha256'] = hashlib.sha256(raw).hexdigest()
    score = json.loads(authenticator.score_path.read_text())
    score['execution_sha256'] = response['execution_sha256']
    authenticator.score_path.write_text(json.dumps(score))
    authenticator = replace(authenticator, score_sha256=sha256_file(authenticator.score_path))
    with pytest.raises(parent.OcrSealedEvaluationError):
        authenticator.authenticate(repository_root=root, candidate_path=candidate,
                                   candidate_sha256=sha256_file(candidate))


def test_v2_requires_native_binding_before_replay(tmp_path, monkeypatch):
    root, candidate, authenticator, _ = _v2_fixture(tmp_path, monkeypatch)
    authenticator = replace(authenticator, workflow_candidate_sha256=None)
    with pytest.raises(parent.OcrSealedEvaluationError, match='NATIVE_IDENTITY_INVALID'):
        authenticator.authenticate(repository_root=root, candidate_path=candidate,
                                   candidate_sha256=sha256_file(candidate))


def test_child_environment_excludes_dotnet_loader_injection(monkeypatch):
    for key in ('DOTNET_STARTUP_HOOKS', 'DOTNET_ADDITIONAL_DEPS', 'CORECLR_PROFILER', 'COMPlus_ReadyToRun'):
        monkeypatch.setenv(key, 'injected')
    environment = parent._sanitized_child_environment()
    assert not any(key.upper().startswith(('DOTNET_', 'CORECLR_', 'COR_', 'COMPLUS_')) for key in environment)
    assert environment['OMP_WAIT_POLICY'] == 'PASSIVE'


@pytest.mark.skipif(os.name != 'nt', reason='Windows sharing semantics')
def test_runtime_lock_prevents_writes_and_path_replacement(tmp_path):
    path = tmp_path / 'runtime.dll'
    path.write_bytes(b'original')
    replacement = tmp_path / 'replacement.dll'
    replacement.write_bytes(b'replacement')
    with parent._locked_runtime_files({path: sha256_file(path)}):
        with pytest.raises(OSError):
            path.write_bytes(b'changed')
        with pytest.raises(OSError):
            os.replace(replacement, path)
        assert path.read_bytes() == b'original'
    path.write_bytes(b'after release')


@pytest.mark.skipif(os.name != 'nt', reason='Windows sharing semantics')
def test_runtime_lock_rejects_an_existing_writer(tmp_path):
    import ctypes
    from ctypes import wintypes
    path = tmp_path / 'runtime.dll'
    path.write_bytes(b'original')
    digest = sha256_file(path)
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    create = kernel.CreateFileW
    create.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID,
                       wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    create.restype = wintypes.HANDLE
    close = kernel.CloseHandle
    close.argtypes = [wintypes.HANDLE]
    handle = create(str(path), 0x40000000, 3, None, 3, 0x80, None)
    assert handle != ctypes.c_void_p(-1).value
    try:
        with pytest.raises(OSError):
            with parent._locked_runtime_files({path: digest}):
                pytest.fail('existing writer was accepted')
    finally:
        close(handle)


@pytest.mark.skipif(os.name != 'nt', reason='Windows sharing semantics')
def test_validator_is_locked_until_native_check_returns(tmp_path, monkeypatch):
    root, candidate, authenticator, _ = _v2_fixture(tmp_path, monkeypatch)
    run = parent.subprocess.run
    calls = []
    def check(*args, **kwargs):
        calls.append(True)
        with pytest.raises(OSError):
            authenticator.native_validator_path.write_bytes(b'swapped validator')
        assert 'DOTNET_STARTUP_HOOKS' not in kwargs['env']
        return run(*args, **kwargs)
    monkeypatch.setenv('DOTNET_STARTUP_HOOKS', 'injected')
    monkeypatch.setattr(parent.subprocess, 'run', check)
    authenticator.authenticate(repository_root=root, candidate_path=candidate,
                               candidate_sha256=sha256_file(candidate))
    assert calls


def test_worker_inventory_rejects_added_file_before_or_during_child(tmp_path):
    host = tmp_path / 'GraphReader.SyntheticRuntimeEvidence.exe'
    host.write_bytes(b'host')
    inventory = {host: sha256_file(host)}
    added = tmp_path / 'unexpected.dll'
    added.write_bytes(b'unexpected')
    with pytest.raises(OSError, match='inventory changed'):
        with parent._locked_runtime_files(inventory):
            pytest.fail('unrecorded file was accepted')
    added.unlink()
    with pytest.raises(OSError, match='inventory changed'):
        with parent._locked_runtime_files(inventory):
            added.write_bytes(b'unexpected')


def test_legacy_non_windows_hash_fallback_and_v2_fail_closed(tmp_path, monkeypatch):
    from ml.policy import ocr_sealed_transport as transport
    path = tmp_path / 'runtime.dll'
    path.write_bytes(b'original')
    digest = sha256_file(path)
    with monkeypatch.context() as context:
        context.setattr(transport.os, 'name', 'posix')
        with transport._locked_runtime_files({path: digest}):
            assert path.read_bytes() == b'original'
        with pytest.raises(OSError, match='Windows runtime file locking'):
            with transport._locked_runtime_files({path: digest}, require_windows=True):
                pytest.fail('v2 accepted unsupported host')
        with pytest.raises(OSError, match='identity mismatch'):
            with transport._locked_runtime_files({path: digest}):
                path.write_bytes(b'changed')
