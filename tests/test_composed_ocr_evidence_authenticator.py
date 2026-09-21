# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Identity and tamper checks for the owned-development replay boundary."""
from copy import deepcopy
import json
from pathlib import Path
import sys

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
