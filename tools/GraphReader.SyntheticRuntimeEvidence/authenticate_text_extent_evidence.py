# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Recompute synthetic full-OCR evidence before the sealed parent may admit it."""
from __future__ import annotations
from dataclasses import dataclass, replace
from pathlib import Path
from uuid import uuid4

import score_text_extent_candidate as scorer
from ml.markers.gate_seal import sha256_file, canonical_json_bytes
from ml.policy import ocr_sealed_evaluation as parent


def _same_evidence(saved, recomputed):
    # Timing is observational; every scientific value and input identity is exact.
    left, right = dict(saved), dict(recomputed)
    for value in (left,right):
        value.pop('elapsed_milliseconds', None)
    if canonical_json_bytes(left) != canonical_json_bytes(right):
        raise parent.OcrSealedEvaluationError('OCR_SEALED_EVALUATION_SCORE_RECOMPUTATION_MISMATCH')


@dataclass(frozen=True)
class TextExtentFullOcrAuthenticator:
    score_path: Path
    score_sha256: str
    apphost_path: Path

    def authenticate(self, *, repository_root, candidate_path, candidate_sha256):
        root = Path(repository_root).resolve()
        path = (root/self.score_path).resolve()
        candidate_path = Path(candidate_path).resolve()
        apphost = (root/self.apphost_path).resolve()
        if (not path.is_relative_to(root/'artifacts') or not candidate_path.is_relative_to(root/'artifacts')
                or not apphost.is_relative_to(root) or apphost.name != 'GraphReader.SyntheticRuntimeEvidence.exe'
                or not apphost.is_file()):
            raise parent.OcrSealedEvaluationError('OCR_SEALED_EVALUATION_PREFLIGHT_INVALID')
        saved = scorer._read(root, {'path': str(path), 'sha256': self.score_sha256})
        inputs = saved['inputs']
        if (inputs['candidate'] != {'path': candidate_path.relative_to(root).as_posix(), 'sha256': candidate_sha256}
                or inputs['input_adapter'] != {'path': Path(scorer.__file__).resolve().relative_to(root).as_posix(),
                                              'sha256': sha256_file(Path(scorer.__file__))}):
            raise parent.OcrSealedEvaluationError('OCR_SEALED_EVALUATION_PREFLIGHT_INVALID')
        inventory = parent._candidate_execution_inventory(root,candidate_path)
        if any(p.parent != apphost.parent for p in inventory):
            raise parent.OcrSealedEvaluationError('OCR_SEALED_EVALUATION_RUNTIME_INVALID')
        runtime_paths = tuple(sorted((p.resolve() for p in apphost.parent.rglob('*')
            if p.is_file() and p.suffix.lower() in {'.dll','.exe','.json'}), key=str))
        if not runtime_paths or any(not p.is_relative_to(apphost.parent) for p in runtime_paths):
            raise parent.OcrSealedEvaluationError('OCR_SEALED_EVALUATION_RUNTIME_INVALID')
        runtime_files = tuple(parent.AuthenticatedRuntimeFile(p,sha256_file(p)) for p in runtime_paths)
        summary, evaluation = inputs['input_summary'], inputs['evaluation_report']
        recomputed = scorer.score(root/summary['path'],summary['sha256'], candidate_path,candidate_sha256,
            root/evaluation['path'],evaluation['sha256'],
            root/'artifacts/ocr-dev-authentication'/f'{uuid4().hex}.json',
            source_sha=inputs['input_adapter']['sha256'], repository_root=root)
        _same_evidence(saved,recomputed)
        if sha256_file(path) != self.score_sha256:
            raise parent.OcrSealedEvaluationError('OCR_SEALED_EVALUATION_IDENTITY_INVALID')
        evidence = parent.AuthenticatedFullOcrEvidence(
            schema=parent.FULL_OCR_EVIDENCE_SCHEMA, full_ocr_score_path=path,
            full_ocr_score_sha256=self.score_sha256, candidate_path=candidate_path,
            candidate_sha256=candidate_sha256, runtime_command_prefix=(str(apphost),),
            runtime_files=runtime_files, runtime_identity_sha256='0'*64,
            acceptance_bars_sha256=parent.ACCEPTANCE_BARS_SHA256,
            metric_reference_sha256=parent.METRIC_REFERENCE_SHA256,
            acceptance_scope=parent.ACCEPTANCE_SCOPE,
            coverage_protocol_sha256=parent.COVERAGE_PROTOCOL_SHA256)
        return replace(evidence, runtime_identity_sha256=parent._runtime_identity(evidence,root,candidate_path))
