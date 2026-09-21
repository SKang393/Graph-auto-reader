# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Handwritten composed OCR evidence checks; never read a sealed corpus."""
from copy import deepcopy
from dataclasses import replace
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from ml.markers.gate_seal import sha256_file
from ml.policy import ocr_sealed_evaluation as evaluation
import test_ocr_sealed_evaluation as legacy


def fixture(tmp_path):
    root, candidate, evidence = legacy._runtime_fixture(tmp_path, composed=True)
    metrics = {}
    for split, sources, panels, count, characters in (
        ('train', 20, 30, 709, 3415), ('validation', 3, 9, 183, 1019),
    ):
        raw = legacy._geometry(count)
        raw.update(true_positives=0, false_positives=count, false_negatives=count, precision=0, recall=0)
        metrics[split] = {'source_count': sources, 'panel_count': panels, 'metrics': {
            'source_count': sources, 'raw_detector_geometry': raw,
            'assembled_geometry': legacy._geometry(count),
            'full_ocr_metrics': legacy._full_metrics(count, characters),
            'recognition_failed_region_count': 0,
        }}
    score = {'schema': evaluation.COMPOSED_SCORE_SCHEMA, 'status': 'completed', 'sources': 23,
             'metrics': metrics, 'elapsed_milliseconds': 1, 'request_sha256': '9'*64,
             'candidate_sha256': evidence.candidate_sha256, 'model_inference': True,
             'truth_consumed_by_inference': False, 'case_output': False, 'private_reads': 0,
             'sealed_reads': 0, 'stage_admission_granted': False, 'production_approved': False}
    evidence.full_ocr_score_path.write_text(json.dumps(score), encoding='utf-8')
    evidence = replace(evidence, full_ocr_score_sha256=sha256_file(evidence.full_ocr_score_path))
    return root, candidate, evidence, score


def test_assembled_boundary_passes_without_inventing_raw_only_gate(tmp_path):
    _, _, evidence, _ = fixture(tmp_path)
    evaluation._composed_dev_evidence(evidence, legacy._bars())


@pytest.mark.parametrize('mutation', [
    lambda s: s.update(schema=evaluation.FULL_OCR_SCORE_SCHEMA),
    lambda s: s.update(candidate_sha256='0'*64),
    lambda s: s.update(request_sha256='not-a-hash'),
    lambda s: s.update(sources=22),
    lambda s: s.update(private_reads=1),
    lambda s: s.update(sealed_reads=True),
    lambda s: s.update(case_output=True),
    lambda s: s.update(truth_consumed_by_inference=True),
    lambda s: s.update(model_inference=False),
    lambda s: s.update(production_approved=True),
    lambda s: s.update(elapsed_milliseconds=float('nan')),
    lambda s: s.update(case_name='forbidden output'),
    lambda s: s['metrics'].pop('train'),
    lambda s: s['metrics']['train'].update(panel_count=28),
    lambda s: s['metrics']['validation']['metrics'].update(source_count=2),
    lambda s: s['metrics']['validation']['metrics']['full_ocr_metrics'].update(recognition_exact_count=182),
])
def test_invalid_or_partial_development_cannot_be_admitted(tmp_path, mutation):
    _, _, evidence, score = fixture(tmp_path)
    mutation(score)
    evidence.full_ocr_score_path.write_text(json.dumps(score), encoding='utf-8')
    with pytest.raises(evaluation.OcrSealedEvaluationError):
        evaluation._composed_dev_evidence(evidence, legacy._bars())


def test_shared_accuracy_bars_still_apply(tmp_path):
    _, _, evidence, score = fixture(tmp_path)
    full = score['metrics']['validation']['metrics']['full_ocr_metrics']
    full.update(recognition_exact_count=170, recognition_exact_accuracy=170/183)
    evidence.full_ocr_score_path.write_text(json.dumps(score), encoding='utf-8')
    with pytest.raises(evaluation.OcrSealedEvaluationError, match='DEV_GATE_FAILED'):
        evaluation._composed_dev_evidence(evidence, legacy._bars())


@pytest.mark.parametrize('composed_candidate', [False, True])
def test_evidence_route_cannot_be_substituted(tmp_path, monkeypatch, composed_candidate):
    root, candidate, evidence, _ = fixture(tmp_path)
    if composed_candidate:
        evidence = replace(evidence, schema=evaluation.FULL_OCR_EVIDENCE_SCHEMA)
    else:
        value = json.loads(candidate.read_text())
        value['schema'] = 'graphreader.frozen-db-head-ocr-candidate.v1'
        candidate.write_text(json.dumps(value))
        evidence = replace(evidence, candidate_sha256=sha256_file(candidate))
    authenticator = SimpleNamespace(authenticate=lambda **_: evidence)
    with pytest.raises(evaluation.OcrSealedEvaluationError, match='PREFLIGHT_INVALID'):
        evaluation._authenticate_preflight(authenticator, root, candidate,
                                           evidence.candidate_sha256, evidence.runtime_command_prefix)


def test_authenticated_composed_preflight_uses_current_panel_coverage(tmp_path, monkeypatch):
    root, candidate, evidence, _ = fixture(tmp_path)
    monkeypatch.setattr(evaluation, '_read_exact_file', lambda *_: None)
    monkeypatch.setattr(evaluation, '_canonical_bars', lambda *_: legacy._bars())
    returned, _ = evaluation._authenticate_preflight(SimpleNamespace(authenticate=lambda **_: evidence),
        root, candidate, evidence.candidate_sha256, evidence.runtime_command_prefix)
    assert returned == evidence
