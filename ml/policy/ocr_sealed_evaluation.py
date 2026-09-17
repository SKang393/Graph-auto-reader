# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Parent orchestration for one aggregate-only Goal 22 OCR sealed evaluation.

Callers must provide a full OCR train/dev evidence authenticator; the text-extent
adapter recomputes that evidence from authenticated synthetic inputs. There is
no CLI or boolean bypass. This module independently rechecks the score
contract, returned files, runtime bytes, and frozen identities before it
prepares sealed admission.
"""

from __future__ import annotations

from collections.abc import Callable, Mapping, Sequence
from copy import deepcopy
from dataclasses import dataclass
import json
import math
import os
from pathlib import Path
import shutil
from typing import Protocol, runtime_checkable
from uuid import uuid4

from ml.markers.gate_seal import (
    GateSeal,
    canonical_json_bytes,
    complete_gate_seal,
    sha256_bytes,
    sha256_file,
)
from ml.markers.training_budget import (
    TrainingAuthorization,
    complete_training_candidate,
)
from ml.policy import sealed_reserve
from ml.policy.evidence_policy import evidence_policy_reference
from ml.policy.ocr_sealed_transport import (
    ACCEPTANCE_SCOPE,
    COVERAGE_PROTOCOL_SHA256,
    METRIC_REFERENCE_SHA256,
    OcrSealedDisclosureError,
    OcrSealedRequestIdentity,
    OcrSealedTransportError,
    OcrSealedTransportResult,
    OcrSealedUnclassifiedOutputError,
    run_ocr_sealed_worker,
    validate_result_envelope,
)
from ml.policy.sealed_first_read_admission import (
    AdmissionReceipt,
    SealedFirstReadAdmission,
    bound_reserve_metadata,
    complete_admission,
    fail_admission,
    prepare_admission,
    record_case_level_disclosure,
    record_unclassified_output,
    record_positive_read_receipt,
    recover_admission,
    send_ack,
    void_pre_ack,
)


REQUEST_SCHEMA = "graphreader.original-db-ocr-sealed-worker-request.v1"
OUTCOME_SCHEMA = "graphreader.original-db-ocr-sealed-evaluation.v1"
FULL_OCR_EVIDENCE_SCHEMA = "graphreader.authenticated-full-ocr-train-dev-evidence.v1"
FULL_OCR_SCORE_SCHEMA = "graphreader.full-ocr-candidate-score.v2"
ACCEPTANCE_BARS_PATH = Path("ml/policy/acceptance-bars.json")
ACCEPTANCE_BARS_SHA256 = "aab9f2ab60cf166828f0928b8496f537341870fd457d0408952e22549fc53a56"
COVERAGE_PROTOCOL_PATH = Path("ml/policy/goal22-ocr-sealed-coverage-v1.json")
METRIC_REFERENCE_PATH = Path(
    "tools/GraphReader.SyntheticRuntimeEvidence/score_full_ocr_candidate_v2.py"
)
_SHA256_CHARACTERS = frozenset("0123456789abcdef")


class OcrSealedEvaluationError(RuntimeError):
    """Sanitized parent-orchestration failure."""

    def __init__(self, code: str):
        self.code = code
        super().__init__(code)


@dataclass(frozen=True)
class AuthenticatedFullOcrEvidence:
    schema: str
    full_ocr_score_path: Path
    full_ocr_score_sha256: str
    candidate_path: Path
    candidate_sha256: str
    runtime_command_prefix: tuple[str, ...]
    runtime_files: tuple["AuthenticatedRuntimeFile", ...]
    runtime_identity_sha256: str
    acceptance_bars_sha256: str
    metric_reference_sha256: str
    acceptance_scope: str
    coverage_protocol_sha256: str


@dataclass(frozen=True)
class AuthenticatedRuntimeFile:
    path: Path
    sha256: str


@runtime_checkable
class FullOcrEvidenceAuthenticator(Protocol):
    """Required recomputation boundary for the full OCR dev contract."""

    def authenticate(
        self,
        *,
        repository_root: Path,
        candidate_path: Path,
        candidate_sha256: str,
    ) -> AuthenticatedFullOcrEvidence:
        ...


@dataclass(frozen=True)
class OcrSealedEvaluationResult:
    status: str
    read_status: str
    admission_id: str
    request_path: Path
    request_sha256: str | None
    outcome_path: Path
    outcome_sha256: str


TransportRunner = Callable[..., OcrSealedTransportResult]


@dataclass(frozen=True)
class _CanonicalOcrBars:
    detection_recall_minimum: float
    detection_precision_minimum: float
    recognition_exact_minimum: float
    character_error_rate_maximum: float
    role_accuracy_minimum: float


def _sha256(value: object, label: str) -> str:
    if (
        not isinstance(value, str)
        or len(value) != 64
        or value != value.casefold()
        or any(character not in _SHA256_CHARACTERS for character in value)
    ):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_IDENTITY_INVALID")
    return value


def _relative_artifact(root: Path, path: Path, label: str) -> str:
    try:
        relative = path.resolve().relative_to(root.resolve()).as_posix()
    except (OSError, ValueError) as error:
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PATH_INVALID") from error
    if not relative.startswith("artifacts/") or ".." in Path(relative).parts:
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PATH_INVALID")
    return relative


def _read_exact_file(root: Path, relative: Path, expected_sha256: str, label: str) -> None:
    path = (root / relative).resolve()
    if not path.is_file() or sha256_file(path) != _sha256(expected_sha256, label):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_IDENTITY_INVALID")


def _read_json_object(path: Path) -> dict[str, object]:
    def reject_duplicates(pairs: list[tuple[str, object]]) -> dict[str, object]:
        result: dict[str, object] = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate key")
            result[key] = value
        return result

    try:
        value = json.loads(
            path.read_text(encoding="utf-8"),
            object_pairs_hook=reject_duplicates,
            parse_constant=lambda _value: (_ for _ in ()).throw(ValueError("constant")),
        )
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError, RecursionError):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RECORD_INVALID") from None
    if type(value) is not dict:
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RECORD_INVALID")
    return value


def _canonical_bars(root: Path) -> _CanonicalOcrBars:
    _read_exact_file(root, ACCEPTANCE_BARS_PATH, ACCEPTANCE_BARS_SHA256, "acceptance bars")
    record = _read_json_object(root / ACCEPTANCE_BARS_PATH)
    tier = record.get("tier1_reviewable_error")
    if type(tier) is not dict:
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_BARS_INVALID")
    keys = (
        "text_region_detection_recall_minimum",
        "text_region_detection_precision_minimum",
        "recognition_exact_match_minimum",
        "character_error_rate_maximum",
        "role_accuracy_minimum",
    )
    values = [tier.get(key) for key in keys]
    if any(type(value) not in (int, float) or not math.isfinite(float(value)) for value in values):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_BARS_INVALID")
    return _CanonicalOcrBars(*(float(value) for value in values))


def _resolved_command_files(root: Path, command: Sequence[str]) -> tuple[Path, ...]:
    if len(command) not in (1, 2):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
    first = Path(command[0])
    executable = first if first.is_absolute() else root / first
    if not executable.is_file():
        located = shutil.which(command[0])
        if located is None:
            raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
        executable = Path(located)
    executable = executable.resolve()
    if len(command) == 1:
        return (executable,)
    if executable.name.casefold() not in {"dotnet", "dotnet.exe"}:
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
    worker = Path(command[1])
    worker = (worker if worker.is_absolute() else root / worker).resolve()
    if worker.suffix.casefold() != ".dll" or not worker.is_file():
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
    return executable, worker


def _candidate_execution_inventory(root: Path, candidate_path: Path) -> dict[Path, str]:
    candidate = _read_json_object(candidate_path)
    if candidate.get("schema") != "graphreader.frozen-db-head-ocr-candidate.v1":
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
    records = candidate.get("execution_assemblies")
    if type(records) is not list or len(records) != 4:
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
    inventory: dict[Path, str] = {}
    for record in records:
        if type(record) is not dict or set(record) != {"name", "path", "sha256"}:
            raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
        relative = record["path"]
        if not isinstance(relative, str):
            raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
        try:
            path = (root / relative).resolve()
            path.relative_to(root)
        except (OSError, ValueError):
            raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID") from None
        digest = _sha256(record["sha256"], "execution assembly")
        if path in inventory or not path.is_file() or sha256_file(path) != digest:
            raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
        inventory[path] = digest
    return inventory


def _runtime_identity(
    evidence: AuthenticatedFullOcrEvidence,
    root: Path,
    candidate_path: Path,
) -> str:
    if (
        not evidence.runtime_command_prefix
        or any(not isinstance(item, str) or not item or "\x00" in item
               for item in evidence.runtime_command_prefix)
        or not evidence.runtime_files
    ):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
    files: dict[Path, str] = {}
    for item in evidence.runtime_files:
        if not isinstance(item, AuthenticatedRuntimeFile):
            raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
        path = item.path.resolve()
        digest = _sha256(item.sha256, "runtime")
        if path in files or not path.is_file() or sha256_file(path) != digest:
            raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
        files[path] = digest
    command_files = _resolved_command_files(root, evidence.runtime_command_prefix)
    inventory = _candidate_execution_inventory(root, candidate_path)
    if any(files.get(path) != sha256_file(path) for path in command_files):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
    if any(files.get(path) != digest for path, digest in inventory.items()):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RUNTIME_INVALID")
    identity = {
        "schema": "graphreader.original-db-ocr-runtime-execution.v1",
        "command_prefix": list(evidence.runtime_command_prefix),
        "files": [
            {"path": str(path), "sha256": files[path]}
            for path in sorted(files, key=lambda value: str(value).casefold())
        ],
    }
    return sha256_bytes(canonical_json_bytes(identity))


def _revalidate_evidence_files(
    evidence: AuthenticatedFullOcrEvidence,
    root: Path,
    candidate_path: Path,
) -> None:
    if (
        sha256_file(candidate_path) != evidence.candidate_sha256
        or sha256_file(evidence.full_ocr_score_path.resolve()) != evidence.full_ocr_score_sha256
        or _runtime_identity(evidence, root, candidate_path) != evidence.runtime_identity_sha256
    ):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_IDENTITY_INVALID")


def _full_ocr_dev_evidence(
    root: Path,
    evidence: AuthenticatedFullOcrEvidence,
    candidate_path: Path,
    bars: _CanonicalOcrBars,
) -> None:
    score = _read_json_object(evidence.full_ocr_score_path.resolve())
    expected_flags = (
        score.get("schema") == FULL_OCR_SCORE_SCHEMA,
        score.get("status") == "diagnostic_only_unapproved",
        score.get("synthetic_only") is True,
        score.get("private_data") is False,
        score.get("sealed_data") is False,
        score.get("optimizer_steps") == 0,
        score.get("production_approval") is False,
        score.get("release_eligible") is False,
    )
    inputs = score.get("inputs")
    integrity = score.get("integrity")
    metrics = score.get("metrics")
    raw_geometry = score.get("raw_detector_geometry")
    recognized_geometry = score.get("successfully_recognized_region_geometry")
    recognition_failures = score.get("recognition_failures")
    truth_isolation = score.get("truth_isolation")
    acceptance = score.get("acceptance_bar_reference")
    if (
        not all(expected_flags)
        or type(inputs) is not dict
        or type(integrity) is not dict
        or type(metrics) is not dict
        or type(raw_geometry) is not dict
        or type(recognized_geometry) is not dict
        or type(recognition_failures) is not dict
        or type(truth_isolation) is not dict
        or type(acceptance) is not dict
    ):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_INVALID")
    candidate = inputs.get("candidate")
    expected_candidate = {
        "path": candidate_path.relative_to(root).as_posix(),
        "sha256": evidence.candidate_sha256,
    }
    if (
        candidate != expected_candidate
        or inputs.get("evaluator_sha256") != METRIC_REFERENCE_SHA256
        or acceptance.get("sha256") != ACCEPTANCE_BARS_SHA256
        or integrity.get("source_count") != 23
        or integrity.get("panel_count") != 37
        or integrity.get("full_source_truth_count") != 892
        or truth_isolation.get(
            "all_runtime_and_candidate_evidence_authenticated_before_truth_regeneration"
        ) is not True
        or truth_isolation.get("runtime_received_truth") is not False
    ):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_INVALID")

    validated: dict[str, dict[str, object]] = {}
    try:
        for split, source_count, panel_count, truth_count in (
            ("train", 20, 28, 709),
            ("validation", 3, 9, 183),
        ):
            failures = recognition_failures.get(split)
            failure_fields = {"failed_panel_count", "explicit_region_failure_count",
                              "raw_regions_on_failed_panels", "raw_regions_without_successful_recognition"}
            if (type(failures) is not dict or set(failures) != failure_fields
                    or any(type(value) is not int or value < 0 for value in failures.values())
                    or failures["failed_panel_count"] > panel_count
                    or (failures["failed_panel_count"] == 0 and failures["raw_regions_on_failed_panels"] != 0)
                    or failures["explicit_region_failure_count"] + failures["raw_regions_on_failed_panels"]
                    != failures["raw_regions_without_successful_recognition"]):
                raise OcrSealedTransportError("OCR_SEALED_TRANSPORT_RESULT_INVALID")
            expected = OcrSealedRequestIdentity(
                attempt_id=f"authenticated-full-ocr-{split}",
                admission_binding_sha256="0" * 64,
                set_id="1" * 64,
                candidate_sha256=evidence.candidate_sha256,
                archive_sha256="2" * 64,
                archive_manifest_sha256="3" * 64,
                coverage_protocol_sha256=COVERAGE_PROTOCOL_SHA256,
                request_sha256="4" * 64,
                source_count=source_count,
            )
            envelope = {
                "schema": "graphreader.original-db-ocr-sealed-worker-result.v1",
                "status": "completed",
                "split": "sealed",
                "acceptance_scope": ACCEPTANCE_SCOPE,
                "attempt_id": expected.attempt_id,
                "admission_binding_sha256": expected.admission_binding_sha256,
                "set_id": expected.set_id,
                "candidate_sha256": expected.candidate_sha256,
                "archive_sha256": expected.archive_sha256,
                "archive_manifest_sha256": expected.archive_manifest_sha256,
                "coverage_protocol_sha256": expected.coverage_protocol_sha256,
                "request_sha256": expected.request_sha256,
                "metric_reference_sha256": METRIC_REFERENCE_SHA256,
                "execution_provider": "CPUExecutionProvider",
                "cpu_threads": 1,
                "graph_optimization": "ORT_DISABLE_ALL",
                "model_inference": True,
                "case_output": False,
                "production_approved": False,
                "elapsed_ms": 0.0,
                "aggregate": {
                    "source_count": source_count,
                    "panel_count": panel_count,
                    "metrics": {
                        "raw_detector_geometry": raw_geometry.get(split),
                        "successfully_recognized_region_geometry": recognized_geometry.get(split),
                        "recognition_failures": {"raw_regions_without_successful_recognition":
                                                 failures["raw_regions_without_successful_recognition"]},
                        "full_ocr_metrics": metrics.get(split),
                    },
                },
            }
            validated[split] = validate_result_envelope(envelope, expected)
            if (
                validated[split]["aggregate"]["metrics"]["full_ocr_metrics"]
                ["truth_region_count"] != truth_count
            ):
                raise OcrSealedTransportError("OCR_SEALED_TRANSPORT_RESULT_INVALID")
        if (
            validated["validation"]["aggregate"]["metrics"]["full_ocr_metrics"]
            ["truth_character_count"] != 1019
        ):
            raise OcrSealedTransportError("OCR_SEALED_TRANSPORT_RESULT_INVALID")
        dev_status = _bar_result(validated["validation"], bars)[0]
    except (KeyError, TypeError, ValueError, OverflowError, OcrSealedTransportError):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_INVALID") from None
    if dev_status != "pass":
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_DEV_GATE_FAILED")


def _publish_exact(path: Path, payload: bytes, *, maximum_bytes: int | None = None) -> str:
    if not payload or (maximum_bytes is not None and len(payload) > maximum_bytes):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_OUTPUT_INVALID")
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        if path.is_file() and path.read_bytes() == payload:
            return sha256_bytes(payload)
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_OUTPUT_CONFLICT")
    temporary = path.with_name(f".{path.name}.{uuid4().hex}.partial")
    try:
        with temporary.open("xb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        os.link(temporary, path)
        if os.name != "nt":
            descriptor = os.open(path.parent, os.O_RDONLY)
            try:
                os.fsync(descriptor)
            finally:
                os.close(descriptor)
    except OSError as error:
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_OUTPUT_FAILED") from error
    finally:
        temporary.unlink(missing_ok=True)
    return sha256_bytes(payload)


def _authenticate_preflight(
    authenticator: FullOcrEvidenceAuthenticator,
    root: Path,
    candidate_path: Path,
    candidate_sha256: str,
    worker_command_prefix: Sequence[str],
) -> tuple[AuthenticatedFullOcrEvidence, _CanonicalOcrBars]:
    if not isinstance(authenticator, FullOcrEvidenceAuthenticator):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_AUTHENTICATOR_REQUIRED")
    try:
        evidence = authenticator.authenticate(
            repository_root=root,
            candidate_path=candidate_path,
            candidate_sha256=candidate_sha256,
        )
    except BaseException:
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_FAILED") from None
    if not isinstance(evidence, AuthenticatedFullOcrEvidence):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_INVALID")
    expected_scalars = (
        evidence.schema == FULL_OCR_EVIDENCE_SCHEMA,
        evidence.candidate_path.resolve() == candidate_path,
        evidence.candidate_sha256 == candidate_sha256,
        evidence.acceptance_bars_sha256 == ACCEPTANCE_BARS_SHA256,
        evidence.metric_reference_sha256 == METRIC_REFERENCE_SHA256,
        evidence.acceptance_scope == ACCEPTANCE_SCOPE,
        evidence.coverage_protocol_sha256 == COVERAGE_PROTOCOL_SHA256,
    )
    if not all(expected_scalars):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_INVALID")
    for value in (
        evidence.full_ocr_score_sha256,
        evidence.candidate_sha256,
        evidence.runtime_identity_sha256,
        evidence.acceptance_bars_sha256,
        evidence.metric_reference_sha256,
        evidence.coverage_protocol_sha256,
    ):
        _sha256(value, "authenticated full OCR evidence")
    if tuple(worker_command_prefix) != evidence.runtime_command_prefix:
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PREFLIGHT_INVALID")
    _revalidate_evidence_files(evidence, root, candidate_path)
    _relative_artifact(root, evidence.full_ocr_score_path, "full OCR score")
    bars = _canonical_bars(root)
    _read_exact_file(root, COVERAGE_PROTOCOL_PATH, COVERAGE_PROTOCOL_SHA256, "coverage protocol")
    _read_exact_file(root, METRIC_REFERENCE_PATH, METRIC_REFERENCE_SHA256, "metric reference")
    _full_ocr_dev_evidence(root, evidence, candidate_path, bars)
    return evidence, bars


def _preflight_binding(
    evidence: AuthenticatedFullOcrEvidence,
    *,
    registry_sha256: str,
    set_id: str,
    gate_seal: GateSeal,
    training_authorization: TrainingAuthorization,
) -> tuple[str, dict[str, object]]:
    binding = {
        "schema": "graphreader.original-db-ocr-sealed-preflight-binding.v1",
        "registry_sha256": _sha256(registry_sha256, "registry"),
        "set_id": _sha256(set_id, "set"),
        "full_ocr_score_sha256": evidence.full_ocr_score_sha256,
        "candidate_sha256": evidence.candidate_sha256,
        "runtime_identity_sha256": evidence.runtime_identity_sha256,
        "acceptance_bars_sha256": ACCEPTANCE_BARS_SHA256,
        "metric_reference_sha256": METRIC_REFERENCE_SHA256,
        "acceptance_scope": ACCEPTANCE_SCOPE,
        "coverage_protocol_sha256": COVERAGE_PROTOCOL_SHA256,
        "gate_identity_sha256": _sha256(gate_seal.key, "gate"),
        "gate_binding_sha256": sha256_bytes(canonical_json_bytes(gate_seal.binding)),
        "training_binding_sha256": sha256_bytes(
            canonical_json_bytes(training_authorization.binding)
        ),
        "evidence_policy": evidence_policy_reference(),
    }
    return sha256_bytes(canonical_json_bytes(binding)), binding


def _request(
    *,
    attempt_id: str,
    admission: SealedFirstReadAdmission,
    metadata: Mapping[str, object],
    candidate_relative: str,
    candidate_sha256: str,
) -> dict[str, object]:
    archive = metadata.get("archive")
    chain = metadata.get("chain")
    scope = metadata.get("scope")
    if not isinstance(archive, dict) or not isinstance(chain, dict) or not isinstance(scope, dict):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RESERVE_INVALID")
    request = {
        "schema": REQUEST_SCHEMA,
        "acceptance_scope": ACCEPTANCE_SCOPE,
        "split": "sealed",
        "attempt_id": attempt_id,
        "admission_binding_sha256": admission.admission_id,
        "set_id": metadata.get("set_id"),
        "candidate_path": candidate_relative,
        "candidate_sha256": candidate_sha256,
        "archive_path": archive.get("path"),
        "archive_sha256": archive.get("sha256"),
        "archive_manifest_sha256": chain.get("archive_manifest_sha256"),
        "source_count": chain.get("case_count"),
        "coverage_protocol_sha256": scope.get("coverage_protocol_sha256"),
    }
    if (
        request["acceptance_scope"] != scope.get("acceptance_scope")
        or request["coverage_protocol_sha256"] != COVERAGE_PROTOCOL_SHA256
        or request["set_id"] != metadata.get("set_id")
        or type(request["source_count"]) is not int
        or not 1 <= request["source_count"] <= 128
    ):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RESERVE_INVALID")
    for key in (
        "admission_binding_sha256", "set_id", "candidate_sha256", "archive_sha256",
        "archive_manifest_sha256", "coverage_protocol_sha256",
    ):
        _sha256(request[key], key)
    if not isinstance(request["archive_path"], str):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_RESERVE_INVALID")
    return request


def _bar_result(
    envelope: Mapping[str, object], bars: _CanonicalOcrBars
) -> tuple[str, dict[str, bool]]:
    aggregate = envelope["aggregate"]
    metrics = aggregate["metrics"]
    raw = metrics["raw_detector_geometry"]
    full = metrics["full_ocr_metrics"]
    verdicts = {
        "text_region_detection_precision": (
            float(raw["precision"]) >= bars.detection_precision_minimum
        ),
        "text_region_detection_recall": (
            float(raw["recall"]) >= bars.detection_recall_minimum
        ),
        "recognition_exact_match": (
            float(full["recognition_exact_accuracy"]) >= bars.recognition_exact_minimum
        ),
        "character_error_rate": (
            float(full["character_error_rate"]) <= bars.character_error_rate_maximum
        ),
        "role_accuracy": float(full["role_accuracy"]) >= bars.role_accuracy_minimum,
    }
    return ("pass" if all(verdicts.values()) else "fail"), verdicts


def _outcome(
    *,
    status: str,
    read_status: str,
    admission: SealedFirstReadAdmission,
    attempt_id: str,
    preflight_binding_sha256: str,
    preflight_binding: Mapping[str, object],
    request_relative: str | None,
    request_sha256: str | None,
    aggregate: object,
    verdicts: Mapping[str, bool] | None,
    transport: OcrSealedTransportResult | None,
    failure_code: str | None,
    disclosures: Sequence[str] = (),
) -> dict[str, object]:
    if sha256_bytes(canonical_json_bytes(dict(preflight_binding))) != attempt_id:
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_CLOSURE_CONFLICT")
    return {
        "schema": OUTCOME_SCHEMA,
        "status": status,
        "read_status": read_status,
        "split": "sealed",
        "acceptance_scope": ACCEPTANCE_SCOPE,
        "admission_binding_sha256": admission.admission_id,
        "attempt_id": attempt_id,
        "candidate_sha256": preflight_binding["candidate_sha256"],
        "runtime_identity_sha256": preflight_binding["runtime_identity_sha256"],
        "full_ocr_score_sha256": preflight_binding["full_ocr_score_sha256"],
        "acceptance_bars_sha256": ACCEPTANCE_BARS_SHA256,
        "metric_reference_sha256": METRIC_REFERENCE_SHA256,
        "coverage_protocol_sha256": COVERAGE_PROTOCOL_SHA256,
        "preflight_binding_sha256": preflight_binding_sha256,
        "request": (
            {"path": request_relative, "sha256": request_sha256}
            if request_relative is not None and request_sha256 is not None
            else None
        ),
        "aggregate": deepcopy(aggregate),
        "bar_verdicts": dict(verdicts) if verdicts is not None else None,
        "transport": (
            {
                "stderr_sha256": transport.stderr_sha256,
                "stderr_byte_count": transport.stderr_byte_count,
                "elapsed_seconds": transport.elapsed_seconds,
            }
            if transport is not None
            else None
        ),
        "failure_code": failure_code,
        "disclosures": list(disclosures),
        "aggregate_only": not disclosures,
        "case_output": bool(disclosures),
        "truth_rows_output": "truth" in disclosures,
        "prediction_output": "prediction" in disclosures,
        "pixel_output": "pixel" in disclosures,
        "production_approved": False,
    }


def _close_components(
    admission: SealedFirstReadAdmission,
    *,
    status: str,
    report_sha256: str,
) -> None:
    authorization = admission.training_authorization
    training_result = authorization.directory / "result.json"
    expected_training = {
        "schema_version": 1,
        "status": status,
        "opened_sha256": sha256_file(authorization.opened_path),
        "report_sha256": report_sha256,
        "budget_status": "consumed",
    }
    if training_result.exists():
        if _read_json_object(training_result) != expected_training:
            raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_CLOSURE_CONFLICT")
    else:
        complete_training_candidate(
            authorization, status=status, report_sha256=report_sha256
        )

    gate = admission.gate_seal
    gate_result = gate.directory / "result.json"
    expected_gate = {
        "schema_version": 1,
        "status": status,
        "evaluation_count": 1,
        "key": gate.key,
        "opened_sha256": sha256_file(gate.opened_path),
        "report_sha256": report_sha256,
        "budget_status": "consumed",
    }
    if gate_result.exists():
        if _read_json_object(gate_result) != expected_gate:
            raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_CLOSURE_CONFLICT")
    else:
        complete_gate_seal(gate, status=status, report_sha256=report_sha256)


def _sanitized_error(error: BaseException) -> OcrSealedEvaluationError:
    if isinstance(error, OcrSealedEvaluationError):
        return error
    if isinstance(error, OcrSealedTransportError):
        return OcrSealedEvaluationError(error.code)
    return OcrSealedEvaluationError("OCR_SEALED_EVALUATION_FAILED")


def _settle_failure(
    admission: SealedFirstReadAdmission,
    error: BaseException,
    *,
    ack_write_invoked: bool,
) -> AdmissionReceipt:
    sanitized = _sanitized_error(error)
    if ack_write_invoked:
        return fail_admission(admission, sanitized)
    return void_pre_ack(admission, sanitized)


def _matching_admission(
    root: Path,
    registry_path: Path,
    *,
    set_id: str,
    candidate_sha256: str,
    training_authorization: TrainingAuthorization,
    gate_seal: GateSeal,
    preflight_binding_sha256: str,
    preflight_binding: Mapping[str, object],
) -> dict[str, object] | None:
    base_matches = [
        item
        for item in sealed_reserve.first_read_admission_identities(registry_path, root)
        if item.get("set_id") == set_id
        and item.get("candidate_sha256") == candidate_sha256
        and item.get("gate_identity_sha256") == gate_seal.key
        and item.get("revision") == training_authorization.binding.get("revision")
        and item.get("candidate_id") == training_authorization.binding.get("candidate_id")
        and item.get("acceptance_scope") == ACCEPTANCE_SCOPE
        and item.get("coverage_protocol_sha256") == COVERAGE_PROTOCOL_SHA256
    ]
    expected_binding = dict(preflight_binding)
    matches = [
        item for item in base_matches
        if item.get("attempt_id") == preflight_binding_sha256
        and item.get("attempt_binding_sha256") == preflight_binding_sha256
        and item.get("attempt_binding") == expected_binding
    ]
    if len(matches) > 1:
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_ADMISSION_CONFLICT")
    if not matches and any(
        item.get("status") != "void"
        or item.get("quarantine_binding_sha256") is not None
        for item in base_matches
    ):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_ADMISSION_CONFLICT")
    return matches[0] if matches else None


def _admission_record(
    root: Path, registry_path: Path, admission_id: str
) -> dict[str, object]:
    records = [
        item
        for item in sealed_reserve.first_read_admission_identities(registry_path, root)
        if item.get("admission_id") == admission_id
    ]
    if len(records) != 1:
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_ADMISSION_CONFLICT")
    return records[0]


def _result(
    status: str,
    read_status: str,
    admission: SealedFirstReadAdmission,
    request_path: Path,
    request_sha256: str | None,
    outcome_path: Path,
    outcome_sha256: str,
) -> OcrSealedEvaluationResult:
    return OcrSealedEvaluationResult(
        status, read_status, admission.admission_id, request_path,
        request_sha256, outcome_path, outcome_sha256,
    )



def _validate_recovered_outcome(root, outcome, record, request_path, request_sha256):
    """Recompute a saved verdict before completing an interrupted close."""
    conflict = "OCR_SEALED_EVALUATION_CLOSURE_CONFLICT"
    status = outcome["status"]
    if (
        outcome.get("read_status") != record.get("read_status")
        or outcome.get("split") != "sealed"
        or outcome.get("acceptance_scope") != ACCEPTANCE_SCOPE
        or outcome.get("acceptance_bars_sha256") != ACCEPTANCE_BARS_SHA256
        or outcome.get("metric_reference_sha256") != METRIC_REFERENCE_SHA256
        or outcome.get("coverage_protocol_sha256") != COVERAGE_PROTOCOL_SHA256
        or outcome.get("preflight_binding_sha256") != outcome.get("attempt_id")
        or outcome.get("attempt_id") != record.get("attempt_id")
        or outcome.get("candidate_sha256") != record.get("attempt_binding", {}).get("candidate_sha256")
        or outcome.get("runtime_identity_sha256") != record.get("attempt_binding", {}).get("runtime_identity_sha256")
        or outcome.get("full_ocr_score_sha256") != record.get("attempt_binding", {}).get("full_ocr_score_sha256")
        or outcome.get("production_approved") is not False
        or (status == "void" and record.get("read_status") != "none")
        or (status == "possible_read" and record.get("read_status") != "possible")
        or (status == "failed" and record.get("read_status") != "confirmed")
    ):
        raise OcrSealedEvaluationError(conflict)
    request = None
    if request_sha256 is not None:
        request = _read_json_object(request_path)
        if (
            request.get("attempt_id") != record.get("attempt_id")
            or request.get("admission_binding_sha256") != record.get("admission_id")
            or request.get("candidate_sha256") != record.get("candidate_sha256")
        ):
            raise OcrSealedEvaluationError(conflict)
    if status not in {"pass", "fail"}:
        disclosures = outcome.get("disclosures")
        if (outcome.get("aggregate") is not None or outcome.get("bar_verdicts") is not None
                or outcome.get("transport") is not None
                or type(outcome.get("failure_code")) is not str
                or not outcome["failure_code"].startswith("OCR_SEALED_")
                or type(disclosures) is not list
                or any(type(item) is not str for item in disclosures)
                or disclosures != sorted(set(disclosures))
                or not set(disclosures).issubset(sealed_reserve.DISCLOSURE_KINDS)
                or (status == "case_data_disclosure") != bool(disclosures)
                or outcome.get("aggregate_only") is not (not disclosures)
                or outcome.get("case_output") is not bool(disclosures)
                or outcome.get("truth_rows_output") is not ("truth" in disclosures)
                or outcome.get("prediction_output") is not ("prediction" in disclosures)
                or outcome.get("pixel_output") is not ("pixel" in disclosures)):
            raise OcrSealedEvaluationError(conflict)
        if (
            bool(record.get("quarantine_channels"))
            != (outcome.get("failure_code") == record.get("quarantine_code"))
        ):
            raise OcrSealedEvaluationError(conflict)
        return
    if (
        record.get("status") not in {"confirmed_read", "completed"}
        or record.get("read_status") != "confirmed"
        or outcome.get("failure_code") is not None
        or outcome.get("disclosures") != []
        or outcome.get("aggregate_only") is not True
        or any(outcome.get(key) is not False for key in (
            "case_output", "truth_rows_output", "prediction_output", "pixel_output"
        ))
    ):
        raise OcrSealedEvaluationError(conflict)
    try:
        if request is None:
            raise OcrSealedEvaluationError(conflict)
        expected = OcrSealedRequestIdentity(
            attempt_id=record["attempt_id"],
            admission_binding_sha256=outcome["admission_binding_sha256"],
            set_id=request["set_id"], candidate_sha256=outcome["candidate_sha256"],
            archive_sha256=request["archive_sha256"],
            archive_manifest_sha256=request["archive_manifest_sha256"],
            coverage_protocol_sha256=COVERAGE_PROTOCOL_SHA256,
            request_sha256=request_sha256, source_count=request["source_count"],
        )
        envelope = {
            "schema": "graphreader.original-db-ocr-sealed-worker-result.v1",
            "status": "completed", "split": "sealed",
            "acceptance_scope": ACCEPTANCE_SCOPE,
            **{key: getattr(expected, key) for key in (
                "attempt_id", "admission_binding_sha256", "set_id", "candidate_sha256",
                "archive_sha256", "archive_manifest_sha256", "coverage_protocol_sha256",
                "request_sha256",
            )},
            "metric_reference_sha256": METRIC_REFERENCE_SHA256,
            "execution_provider": "CPUExecutionProvider", "cpu_threads": 1,
            "graph_optimization": "ORT_DISABLE_ALL", "model_inference": True,
            "case_output": False, "production_approved": False, "elapsed_ms": 0.0,
            "aggregate": outcome["aggregate"],
        }
        validated = validate_result_envelope(envelope, expected)
        verdict, bars = _bar_result(validated, _canonical_bars(root))
        if verdict != status or bars != outcome.get("bar_verdicts"):
            raise OcrSealedEvaluationError(conflict)
    except (KeyError, TypeError, ValueError, OcrSealedTransportError):
        raise OcrSealedEvaluationError(conflict) from None


def _resume_existing_evaluation(
    root: Path,
    registry_path: Path,
    *,
    existing: Mapping[str, object],
    evidence: AuthenticatedFullOcrEvidence,
    preflight_binding_sha256: str,
    preflight_binding: Mapping[str, object],
    training_authorization: TrainingAuthorization,
    gate_seal: GateSeal,
) -> OcrSealedEvaluationResult:
    if (
        existing.get("attempt_id") != preflight_binding_sha256
        or existing.get("attempt_binding_sha256") != preflight_binding_sha256
        or existing.get("attempt_binding") != dict(preflight_binding)
    ):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_ADMISSION_CONFLICT")
    admission_id = str(existing["admission_id"])
    admission = recover_admission(
        registry_path,
        root,
        admission_id=admission_id,
        training_authorization=training_authorization,
        gate_seal=gate_seal,
    )
    directory = root / "artifacts" / "ocr-sealed-evaluations" / admission_id
    request_path = directory / "request.json"
    outcome_path = directory / "aggregate-outcome.json"
    request_sha256 = sha256_file(request_path) if request_path.is_file() else None
    request_relative = (
        _relative_artifact(root, request_path, "request")
        if request_sha256 is not None
        else None
    )
    record = _admission_record(root, registry_path, admission_id)
    disclosures = record.get("disclosures")
    if type(disclosures) is not list:
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_CLOSURE_CONFLICT")
    if outcome_path.is_file():
        outcome = _read_json_object(outcome_path)
        outcome_sha256 = sha256_file(outcome_path)
        if (
            outcome_path.read_bytes() != canonical_json_bytes(outcome)
            or
            outcome.get("schema") != OUTCOME_SCHEMA
            or outcome.get("admission_binding_sha256") != admission_id
            or outcome.get("attempt_id") != record.get("attempt_id")
            or outcome.get("candidate_sha256") != record.get("attempt_binding", {}).get("candidate_sha256")
            or outcome.get("runtime_identity_sha256") != record.get("attempt_binding", {}).get("runtime_identity_sha256")
            or outcome.get("full_ocr_score_sha256") != record.get("attempt_binding", {}).get("full_ocr_score_sha256")
            or outcome.get("request")
            != (
                {"path": request_relative, "sha256": request_sha256}
                if request_sha256 is not None
                else None
            )
            or outcome.get("status")
            not in {"pass", "fail", "void", "possible_read", "failed", "case_data_disclosure"}
        ):
            raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_CLOSURE_CONFLICT")
        _validate_recovered_outcome(root, outcome, record, request_path, request_sha256)
        if ((outcome["status"] == "case_data_disclosure") != bool(disclosures)
                or (disclosures and outcome["disclosures"] != disclosures)):
            raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_CLOSURE_CONFLICT")
        if record.get("status") == "completed" and record.get("aggregate_result_sha256") != outcome_sha256:
            raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_CLOSURE_CONFLICT")
        status = str(outcome["status"])
        if record.get("status") == "confirmed_read":
            if status in {"pass", "fail"}:
                complete_admission(admission, aggregate_result_sha256=outcome_sha256)
            else:
                fail_admission(
                    admission,
                    OcrSealedEvaluationError(
                        str(outcome.get("failure_code") or "OCR_SEALED_EVALUATION_FAILED")
                    ),
                )
            record = _admission_record(root, registry_path, admission_id)
        if record.get("read_status") == "confirmed":
            _close_components(
                admission,
                status=(
                    "sealed_pass" if status == "pass" else
                    "sealed_fail" if status == "fail" else
                    "sealed_error"
                ),
                report_sha256=outcome_sha256,
            )
        return _result(
            status, str(record["read_status"]), admission, request_path,
            request_sha256, outcome_path, outcome_sha256,
        )

    interruption = OcrSealedEvaluationError("OCR_SEALED_EVALUATION_INTERRUPTED")
    if record.get("status") == "confirmed_read":
        receipt = fail_admission(admission, interruption)
    else:
        receipt = AdmissionReceipt(
            admission_id=admission_id,
            status=str(record["status"]),
            read_status=str(record["read_status"]),
            registry_sha256=sha256_file(admission.registry_path),
        )
    status = (
        "void" if receipt.read_status == "none" else
        "possible_read" if receipt.read_status == "possible" else
        "failed"
    )
    outcome = _outcome(
        status="case_data_disclosure" if disclosures else status,
        read_status=receipt.read_status,
        admission=admission,
        attempt_id=record["attempt_id"],
        preflight_binding_sha256=record["attempt_id"],
        preflight_binding=record["attempt_binding"],
        request_relative=request_relative,
        request_sha256=request_sha256,
        aggregate=None,
        verdicts=None,
        transport=None,
        failure_code=(
            "OCR_SEALED_TRANSPORT_CASE_DATA_DISCLOSURE" if disclosures
            else str(record.get("quarantine_code") or interruption.code)
        ),
        disclosures=disclosures,
    )
    outcome_sha256 = _publish_exact(outcome_path, canonical_json_bytes(outcome))
    if receipt.read_status == "confirmed":
        _close_components(
            admission, status="sealed_error", report_sha256=outcome_sha256
        )
    return _result(
        str(outcome["status"]), receipt.read_status, admission, request_path, request_sha256,
        outcome_path, outcome_sha256,
    )


def evaluate_ocr_sealed_candidate(
    repository_root: Path,
    registry_path: Path,
    *,
    expected_registry_sha256: str,
    set_id: str,
    candidate_path: Path,
    candidate_sha256: str,
    training_authorization: TrainingAuthorization,
    gate_seal: GateSeal,
    full_ocr_evidence_authenticator: FullOcrEvidenceAuthenticator,
    worker_command_prefix: Sequence[str],
    timeout_seconds: float = 300.0,
    transport_runner: TransportRunner = run_ocr_sealed_worker,
) -> OcrSealedEvaluationResult:
    """Run one fully authenticated, aggregate-only sealed OCR evaluation."""

    root = Path(repository_root).resolve()
    candidate = Path(candidate_path).resolve()
    candidate_sha256 = _sha256(candidate_sha256, "candidate")
    candidate_relative = _relative_artifact(root, candidate, "candidate")
    if not candidate.is_file():
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_PATH_INVALID")
    if (
        isinstance(worker_command_prefix, (str, bytes))
        or not isinstance(worker_command_prefix, Sequence)
        or not worker_command_prefix
        or any(not isinstance(value, str) or not value or "\x00" in value for value in worker_command_prefix)
        or not callable(transport_runner)
        or type(timeout_seconds) not in (int, float)
        or not math.isfinite(float(timeout_seconds))
        or not 1 <= float(timeout_seconds) <= 3600
    ):
        raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_ARGUMENT_INVALID")

    evidence, bars = _authenticate_preflight(
        full_ocr_evidence_authenticator,
        root,
        candidate,
        candidate_sha256,
        worker_command_prefix,
    )
    preflight_binding_sha256, preflight_binding = _preflight_binding(
        evidence,
        registry_sha256=expected_registry_sha256,
        set_id=set_id,
        gate_seal=gate_seal,
        training_authorization=training_authorization,
    )
    attempt_id = preflight_binding_sha256
    existing = _matching_admission(
        root,
        registry_path,
        set_id=set_id,
        candidate_sha256=candidate_sha256,
        training_authorization=training_authorization,
        gate_seal=gate_seal,
        preflight_binding_sha256=preflight_binding_sha256,
        preflight_binding=preflight_binding,
    )
    if existing is not None:
        return _resume_existing_evaluation(
            root,
            registry_path,
            existing=existing,
            evidence=evidence,
            preflight_binding_sha256=preflight_binding_sha256,
            preflight_binding=preflight_binding,
            training_authorization=training_authorization,
            gate_seal=gate_seal,
        )

    admission = prepare_admission(
        registry_path,
        root,
        expected_registry_sha256=expected_registry_sha256,
        set_id=set_id,
        candidate_sha256=candidate_sha256,
        training_authorization=training_authorization,
        gate_seal=gate_seal,
        required_acceptance_scope=ACCEPTANCE_SCOPE,
        required_coverage_protocol_sha256=COVERAGE_PROTOCOL_SHA256,
        attempt_id=attempt_id,
        attempt_binding=preflight_binding,
    )
    evaluation_directory = root / "artifacts" / "ocr-sealed-evaluations" / admission.admission_id
    request_path = evaluation_directory / "request.json"
    outcome_path = evaluation_directory / "aggregate-outcome.json"
    request_sha256: str | None = None
    request_relative: str | None = None
    request: dict[str, object] | None = None
    ack_write_invoked = False

    def acknowledge(attempt: str, nonce: str, writer: Callable[[str], None]) -> None:
        nonlocal ack_write_invoked
        _revalidate_evidence_files(evidence, root, candidate)

        def tracked_writer(frame: str) -> None:
            nonlocal ack_write_invoked
            ack_write_invoked = True
            writer(frame)

        send_ack(
            admission,
            attempt_id=attempt,
            request_nonce=nonce,
            write_and_flush=tracked_writer,
        )

    def confirm(attempt: str, confirmed_candidate: str, nonce: str) -> None:
        record_positive_read_receipt(
            admission,
            attempt_id=attempt,
            candidate_sha256=confirmed_candidate,
            request_nonce=nonce,
        )

    try:
        metadata = bound_reserve_metadata(admission)
        request = _request(
            attempt_id=attempt_id,
            admission=admission,
            metadata=metadata,
            candidate_relative=candidate_relative,
            candidate_sha256=candidate_sha256,
        )
        request_sha256 = _publish_exact(
            request_path, canonical_json_bytes(request), maximum_bytes=64 * 1024
        )
        request_relative = _relative_artifact(root, request_path, "request")
        expected = OcrSealedRequestIdentity(
            attempt_id=attempt_id,
            admission_binding_sha256=admission.admission_id,
            set_id=str(request["set_id"]),
            candidate_sha256=candidate_sha256,
            archive_sha256=str(request["archive_sha256"]),
            archive_manifest_sha256=str(request["archive_manifest_sha256"]),
            coverage_protocol_sha256=COVERAGE_PROTOCOL_SHA256,
            request_sha256=request_sha256,
            source_count=int(request["source_count"]),
        )
        command = [
            *worker_command_prefix,
            "--evaluate-original-db-sealed",
            request_relative,
            request_sha256,
        ]
        transport = transport_runner(
            command,
            root,
            expected,
            acknowledge,
            confirm,
            timeout_seconds=timeout_seconds,
        )
        if not isinstance(transport, OcrSealedTransportResult):
            raise OcrSealedTransportError("OCR_SEALED_TRANSPORT_RESULT_INVALID")
        envelope = validate_result_envelope(transport.envelope, expected)
        _revalidate_evidence_files(evidence, root, candidate)
        authority = _admission_record(root, registry_path, admission.admission_id)
        if authority.get("status") != "confirmed_read" or authority.get("read_status") != "confirmed":
            raise OcrSealedEvaluationError("OCR_SEALED_EVALUATION_READ_UNCONFIRMED")
        status, verdicts = _bar_result(envelope, bars)
        outcome = _outcome(
            status=status,
            read_status="confirmed",
            admission=admission,
            attempt_id=attempt_id,
            preflight_binding_sha256=preflight_binding_sha256,
            preflight_binding=preflight_binding,
            request_relative=request_relative,
            request_sha256=request_sha256,
            aggregate=envelope["aggregate"],
            verdicts=verdicts,
            transport=transport,
            failure_code=None,
        )
        outcome_sha256 = _publish_exact(outcome_path, canonical_json_bytes(outcome))
    except OcrSealedDisclosureError as error:
        sanitized = _sanitized_error(error)
        if request is None:
            raise sanitized
        record_case_level_disclosure(
            admission,
            evidence_sha256=error.evidence_sha256,
            disclosures=error.disclosures,
        )
        receipt = _settle_failure(
            admission, sanitized, ack_write_invoked=ack_write_invoked
        )
        outcome = _outcome(
            status="case_data_disclosure",
            read_status=receipt.read_status,
            admission=admission,
            attempt_id=attempt_id,
            preflight_binding_sha256=preflight_binding_sha256,
            preflight_binding=preflight_binding,
            request_relative=request_relative,
            request_sha256=request_sha256,
            aggregate=None,
            verdicts=None,
            transport=None,
            failure_code=error.code,
            disclosures=error.disclosures,
        )
        outcome_sha256 = _publish_exact(outcome_path, canonical_json_bytes(outcome))
        if receipt.read_status == "confirmed":
            _close_components(admission, status="sealed_error", report_sha256=outcome_sha256)
        return _result(
            str(outcome["status"]), receipt.read_status, admission, request_path,
            request_sha256, outcome_path, outcome_sha256,
        )
    except OcrSealedUnclassifiedOutputError as error:
        sanitized = _sanitized_error(error)
        if request is None:
            raise sanitized
        record_unclassified_output(
            admission, code=error.code, channels=error.channels,
        )
        receipt = _settle_failure(
            admission, sanitized, ack_write_invoked=ack_write_invoked
        )
        status = "void" if receipt.read_status == "none" else (
            "possible_read" if receipt.read_status == "possible" else "failed"
        )
        outcome = _outcome(
            status=status, read_status=receipt.read_status, admission=admission,
            attempt_id=attempt_id,
            preflight_binding_sha256=preflight_binding_sha256,
            preflight_binding=preflight_binding,
            request_relative=request_relative, request_sha256=request_sha256,
            aggregate=None, verdicts=None, transport=None,
            failure_code=error.code,
        )
        outcome_sha256 = _publish_exact(outcome_path, canonical_json_bytes(outcome))
        if receipt.read_status == "confirmed":
            _close_components(admission, status="sealed_error", report_sha256=outcome_sha256)
        return _result(
            status, receipt.read_status, admission, request_path, request_sha256,
            outcome_path, outcome_sha256,
        )
    except BaseException as error:
        sanitized = _sanitized_error(error)
        receipt = _settle_failure(
            admission, sanitized, ack_write_invoked=ack_write_invoked
        )
        status = "void" if receipt.read_status == "none" else (
            "possible_read" if receipt.read_status == "possible" else "failed"
        )
        outcome = _outcome(
            status=status,
            read_status=receipt.read_status,
            admission=admission,
            attempt_id=attempt_id,
            preflight_binding_sha256=preflight_binding_sha256,
            preflight_binding=preflight_binding,
            request_relative=request_relative,
            request_sha256=request_sha256,
            aggregate=None,
            verdicts=None,
            transport=None,
            failure_code=sanitized.code,
        )
        outcome_sha256 = _publish_exact(outcome_path, canonical_json_bytes(outcome))
        if receipt.read_status == "confirmed":
            _close_components(admission, status="sealed_error", report_sha256=outcome_sha256)
        return _result(
            status, receipt.read_status, admission, request_path, request_sha256,
            outcome_path, outcome_sha256,
        )

    try:
        receipt = complete_admission(admission, aggregate_result_sha256=outcome_sha256)
        _close_components(
            admission,
            status="sealed_pass" if status == "pass" else "sealed_fail",
            report_sha256=outcome_sha256,
        )
    except BaseException:
        return _resume_existing_evaluation(
            root,
            registry_path,
            existing=_admission_record(root, registry_path, admission.admission_id),
            evidence=evidence,
            preflight_binding_sha256=preflight_binding_sha256,
            preflight_binding=preflight_binding,
            training_authorization=training_authorization,
            gate_seal=gate_seal,
        )
    return _result(
        status, receipt.read_status, admission, request_path, request_sha256,
        outcome_path, outcome_sha256,
    )


__all__ = [
    "ACCEPTANCE_BARS_PATH",
    "ACCEPTANCE_BARS_SHA256",
    "AuthenticatedFullOcrEvidence",
    "AuthenticatedRuntimeFile",
    "FULL_OCR_EVIDENCE_SCHEMA",
    "FULL_OCR_SCORE_SCHEMA",
    "FullOcrEvidenceAuthenticator",
    "OcrSealedEvaluationError",
    "OcrSealedEvaluationResult",
    "OUTCOME_SCHEMA",
    "REQUEST_SCHEMA",
    "evaluate_ocr_sealed_candidate",
]
