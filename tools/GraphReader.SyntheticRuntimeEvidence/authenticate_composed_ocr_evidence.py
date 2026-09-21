# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Authenticate composed OCR by replaying owned development on the worker bytes."""
from __future__ import annotations

from dataclasses import dataclass, replace
import json
import os
from pathlib import Path
import subprocess

from ml.markers.gate_seal import canonical_json_bytes, sha256_file
from ml.policy import ocr_sealed_evaluation as parent


# Fixed owned development inventory, including the completed 39-panel coverage.
GENERATOR_SHA256 = "2891886457562d2d49fb96ef3b3b635463299b2013b1eddac03332dc10c31598"
MAXIMUM_JSON_BYTES = 4 * 1024 * 1024


def _path(root, value):
    path = (root / value).resolve()
    if not path.is_relative_to(root / "artifacts" / "goal22-runs") or not path.is_file():
        raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PATH_INVALID")
    return path


def _read(root, value, expected):
    path = _path(root, value)
    if path.stat().st_size > MAXIMUM_JSON_BYTES or sha256_file(path) != parent._sha256(expected, "dev file"):
        raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_IDENTITY_INVALID")
    return path, parent._read_json_object(path)


def _development_inputs(root, request_path, request_sha256):
    path, request = _read(root, request_path, request_sha256)
    if (set(request) != {"schema", "scope", "generator", "source_count", "private_reads", "sealed_reads"}
            or request["schema"] != "graphreader.composed-ocr-memory-dev-check.v1"
            or request["scope"] != "owned-synthetic-development"
            or type(request["source_count"]) is not int or request["source_count"] != 23
            or any(type(request[k]) is not int or request[k] != 0 for k in ("private_reads", "sealed_reads"))
            or type(request["generator"]) is not dict
            or set(request["generator"]) != {"path", "sha256"}
            or request["generator"]["sha256"] != GENERATOR_SHA256):
        raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_INVALID")
    generator_path, generator = _read(root, request["generator"]["path"], GENERATOR_SHA256)
    sources = generator.get("sources")
    if (type(sources) is not list or len(sources) != 23 or generator.get("source_count") != 23
            or generator.get("private_reads") != 0 or generator.get("sealed_reads") != 0
            or generator.get("production_approved") is not False):
        raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_INVALID")
    bound = {path: request_sha256, generator_path: GENERATOR_SHA256}
    images, splits = set(), {"train": 0, "dev": 0}
    for source in sources:
        if source.get("split") not in splits:
            raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_INVALID")
        splits[source["split"]] += 1
        for key in ("image", "annotation", "scene"):
            record = source[key]
            if type(record) is not dict or set(record) != {"path", "sha256"}:
                raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_INVALID")
            item = _path(root, record["path"])
            digest = parent._sha256(record["sha256"], "synthetic source")
            if item in bound or sha256_file(item) != digest:
                raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_IDENTITY_INVALID")
            bound[item] = digest
            if key == "image":
                if digest in images:
                    raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_INVALID")
                images.add(digest)
    if splits != {"train": 20, "dev": 3}:
        raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_INVALID")
    return path, bound


def _replay(apphost, root, request, request_sha256, candidate, candidate_sha256):
    environment = dict(os.environ, OMP_WAIT_POLICY="PASSIVE", KMP_BLOCKTIME="0")
    flags = (subprocess.CREATE_NO_WINDOW | subprocess.IDLE_PRIORITY_CLASS) if os.name == "nt" else 0
    result = subprocess.run(
        [str(apphost), "--check-composed-ocr-memory-dev", request.relative_to(root).as_posix(),
         request_sha256, candidate.relative_to(root).as_posix(), candidate_sha256],
        cwd=root, env=environment, stdin=subprocess.DEVNULL, capture_output=True,
        timeout=300, creationflags=flags, check=False,
    )
    if result.returncode != 0 or result.stderr or not 0 < len(result.stdout) <= MAXIMUM_JSON_BYTES:
        raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_DEV_REPLAY_FAILED")
    def unique(pairs):
        value = {}
        for key, item in pairs:
            if key in value:
                raise ValueError("duplicate aggregate field")
            value[key] = item
        return value
    return json.loads(result.stdout.decode("utf-8-sig"), object_pairs_hook=unique,
                      parse_constant=lambda _: (_ for _ in ()).throw(ValueError("non-finite metric")))


@dataclass(frozen=True)
class ComposedOcrEvidenceAuthenticator:
    score_path: Path
    score_sha256: str
    apphost_path: Path
    development_request_path: Path
    development_request_sha256: str

    def authenticate(self, *, repository_root, candidate_path, candidate_sha256):
        root = Path(repository_root).resolve()
        score_path, saved = _read(root, self.score_path, self.score_sha256)
        candidate_path, candidate = _read(root, candidate_path, candidate_sha256)
        request, bound_inputs = _development_inputs(
            root, self.development_request_path, self.development_request_sha256)
        apphost = _path(root, self.apphost_path)
        if (apphost.name != "GraphReader.SyntheticRuntimeEvidence.exe"
                or candidate.get("schema") != "graphreader.frozen-composed-ocr-candidate.v1"
                or saved.get("request_sha256") != self.development_request_sha256
                or saved.get("candidate_sha256") != candidate_sha256):
            raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_INVALID")
        inventory = parent._candidate_execution_inventory(root, candidate_path)
        if any(path.parent != apphost.parent for path in inventory):
            raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
        runtime_paths = tuple(sorted((path.resolve() for path in apphost.parent.rglob("*")
            if path.is_file() and path.suffix.lower() in {".dll", ".exe", ".json"}), key=str))
        if not runtime_paths or any(not path.is_relative_to(apphost.parent) for path in runtime_paths):
            raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
        evidence = parent.AuthenticatedFullOcrEvidence(
            schema=parent.COMPOSED_EVIDENCE_SCHEMA, full_ocr_score_path=score_path,
            full_ocr_score_sha256=self.score_sha256, candidate_path=candidate_path,
            candidate_sha256=candidate_sha256, runtime_command_prefix=(str(apphost),),
            runtime_files=tuple(parent.AuthenticatedRuntimeFile(path, sha256_file(path)) for path in runtime_paths),
            runtime_identity_sha256="0" * 64, acceptance_bars_sha256=parent.ACCEPTANCE_BARS_SHA256,
            metric_reference_sha256=parent.METRIC_REFERENCE_SHA256, acceptance_scope=parent.ACCEPTANCE_SCOPE,
            coverage_protocol_sha256=parent.COVERAGE_PROTOCOL_SHA256,
        )
        evidence = replace(evidence, runtime_identity_sha256=parent._runtime_identity(evidence, root, candidate_path))
        # Reject invalid/failed development before spending another inference run.
        parent._composed_dev_evidence(evidence, parent._canonical_bars(root))
        recomputed = _replay(apphost, root, request, self.development_request_sha256, candidate_path, candidate_sha256)
        left, right = dict(saved), dict(recomputed)
        for value in (left, right):
            value.pop("elapsed_milliseconds", None)
        if canonical_json_bytes(left) != canonical_json_bytes(right):
            raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_SCORE_RECOMPUTATION_MISMATCH")
        for path, digest in bound_inputs.items():
            if sha256_file(path) != digest:
                raise parent.OcrSealedEvaluationError("OCR_SEALED_EVALUATION_IDENTITY_INVALID")
        parent._revalidate_evidence_files(evidence, root, candidate_path)
        return evidence
