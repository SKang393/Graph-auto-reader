# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bounded parent process for aggregate-only real workflow evaluation.

This module supervises an already authorized C# worker. It does not authorize
the candidate, open a corpus, or interpret acceptance metrics.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
import hashlib
import json
import math
import os
from pathlib import Path
from queue import Empty, Full, Queue
import re
import subprocess
import threading
import time
from typing import Any, BinaryIO
from uuid import uuid4

from ml.policy.evidence_policy import evidence_policy_reference
from ml.policy.real_workflow_ledger import (
    RealWorkflowAttempt,
    RealWorkflowCandidateDescriptor,
    complete_attempt,
    open_attempt,
    record_attempt_failure,
    record_corpus_content,
    record_disclosure,
)
from ml.policy.real_workflow_transport import FirstReadAccountingBridge


_RESULT_PREFIX = b"G22_RESULT/1 "
_FAILURE_FRAME = b"G22_FAILURE/1 REAL_WORKFLOW_FAILED\n"
_FIRST_READ_PREFIX = b"G22_FIRST_READ/1 "
_CORPUS_PREFIX = b"G22_CORPUS/1 "
_CORPUS_ACK_PREFIX = "G22_CORPUS_ACK/1"
_MAX_FRAME_BYTES = 128 * 1024
_STDERR_CHUNK_BYTES = 4096
_EVENT_BUFFER_CAPACITY = 16
_DISCLOSURES = ["case_identity", "pixel", "prediction", "truth"]
_SHA256 = re.compile(r"[0-9a-f]{64}\Z")

_ENVELOPE_FIELDS = {
    "candidate_sha256", "protocol_sha256", "assignment_sha256",
    "selected_inventory_sha256", "corpus_content_sha256", "split", "aggregate",
}
_AGGREGATE_FIELDS = {
    "schema", "image_groups", "decoded_image_groups", "project_files",
    "workflow_invocations", "workflow_succeeded_image_groups",
    "workflow_failed_image_groups", "workflow_succeeded_projects",
    "workflow_failed_projects", "truth_series", "truth_points",
    "coincident_cross_project_point_pairs", "execution_failure_kinds",
    "evaluation", "aggregate_only", "case_level_output", "truth_rows_output",
    "prediction_output", "paths_output", "names_output",
}
_AGGREGATE_INTEGER_FIELDS = {
    "image_groups", "decoded_image_groups", "project_files", "workflow_invocations",
    "workflow_succeeded_image_groups", "workflow_failed_image_groups",
    "workflow_succeeded_projects", "workflow_failed_projects", "truth_series",
    "truth_points", "coincident_cross_project_point_pairs",
}
_EVALUATION_FIELDS = {
    "truth_cases", "output_cases", "completed_cases", "failed_cases",
    "unexpected_cases", "integrity_failure_cases", "truth_series",
    "predicted_series", "matched_series", "truth_points", "predicted_points",
    "matched_points", "unique_point_value_metrics_available",
    "relational_row_metrics_available", "relational_phase_metrics_available",
    "actual_rows", "residual_artifact_rows_from_failed_cases", "expected_rows",
    "structurally_matched_rows", "correct_rows", "missing_rows", "extra_rows",
    "duplicate_rows", "wrong_scale_rows", "wrong_export_mode_rows",
    "wrong_phase_rows", "wrong_relation_rows", "unique_point_value_correct",
    "unique_point_value_incorrect", "unique_point_missing", "unique_point_extra",
    "unique_point_wrong_scale", "unique_point_wrong_export_mode",
    "unique_point_structural_precision", "unique_point_structural_coverage",
    "unique_point_value_precision", "unique_point_value_coverage",
    "matched_unique_point_value_accuracy", "relational_row_precision",
    "relational_row_coverage", "matched_relational_graph_value_accuracy",
    "matched_relational_phase_accuracy", "artifact_integrity_valid", "failure_kinds",
}
_EVALUATION_INTEGER_FIELDS = {
    "truth_cases", "output_cases", "completed_cases", "failed_cases",
    "unexpected_cases", "integrity_failure_cases", "truth_series",
    "predicted_series", "matched_series", "truth_points", "predicted_points",
    "matched_points", "actual_rows", "residual_artifact_rows_from_failed_cases",
}
_OPTIONAL_INTEGER_FIELDS = {
    "expected_rows", "structurally_matched_rows", "correct_rows", "missing_rows",
    "extra_rows", "duplicate_rows", "wrong_scale_rows", "wrong_export_mode_rows",
    "wrong_phase_rows", "wrong_relation_rows", "unique_point_value_correct",
    "unique_point_value_incorrect", "unique_point_missing", "unique_point_extra",
    "unique_point_wrong_scale", "unique_point_wrong_export_mode",
}
_REQUIRED_METRIC_FIELDS = {
    "unique_point_structural_precision", "unique_point_structural_coverage",
}
_OPTIONAL_METRIC_FIELDS = {
    "unique_point_value_precision", "unique_point_value_coverage",
    "matched_unique_point_value_accuracy", "relational_row_precision",
    "relational_row_coverage", "matched_relational_graph_value_accuracy",
    "matched_relational_phase_accuracy",
}
_EXECUTION_FAILURE_CODES = {
    "WORKFLOW_IMAGE_IMPORT_FAILED", "WORKFLOW_PDF_IMPORT_UNAVAILABLE",
    "WORKFLOW_PDF_IMPORT_FAILED", "WORKFLOW_PDF_PANEL_BYTES_UNAVAILABLE",
    "WORKFLOW_DETECTION_MODELS_UNAVAILABLE", "WORKFLOW_DETECTION_EVIDENCE_REJECTED",
    "WORKFLOW_REVIEW_PROJECTION_REJECTED", "WORKFLOW_RECALIBRATION_REQUIRED",
    "WORKFLOW_EXPORT_FAILED", "WORKFLOW_EXECUTION_FAILED", "WORKFLOW_OUTPUT_MISSING",
    "WORKFLOW_OUTPUT_IDENTITY_MISMATCH", "WORKFLOW_OUTPUT_INVALID",
}
_EVALUATION_FAILURE_CODES = {
    "duplicate_case_output", "unexpected_case", "missing_case_output",
    "source_identity_mismatch", "workflow_failed_without_code", "workflow_failed",
    "artifact_integrity:InvalidDataException", "artifact_integrity:IOException",
    "artifact_integrity:UnauthorizedAccessException", "artifact_integrity:JsonException",
    "artifact_integrity:DecoderFallbackException", "artifact_integrity:OverflowException",
    "artifact_integrity:ArgumentException", "artifact_integrity:NotSupportedException",
    "artifact_integrity:SecurityException",
}


class FrozenWorkflowProcessError(RuntimeError):
    """Sanitized process-boundary failure with no worker output attached."""


class _ReaderOverflow:
    """Retain one digest when bounded event delivery cannot keep up."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._digest: str | None = None

    def record(self, digest: str) -> None:
        with self._lock:
            if self._digest is None:
                self._digest = digest

    def digest(self) -> str | None:
        with self._lock:
            return self._digest


def _fail(code: str) -> FrozenWorkflowProcessError:
    return FrozenWorkflowProcessError(code)


def _require_sha256(value: Any, label: str) -> str:
    if not isinstance(value, str) or _SHA256.fullmatch(value) is None:
        raise _fail(label)
    return value


def _validate_descriptor(descriptor: RealWorkflowCandidateDescriptor) -> None:
    if not isinstance(descriptor, RealWorkflowCandidateDescriptor):
        raise _fail("REAL_WORKFLOW_DESCRIPTOR_INVALID")
    for value in (descriptor.revision, descriptor.candidate_id):
        if not isinstance(value, str) or not value or value.strip() != value or len(value) > 256:
            raise _fail("REAL_WORKFLOW_DESCRIPTOR_INVALID")
    _require_sha256(descriptor.candidate_sha256, "REAL_WORKFLOW_DESCRIPTOR_INVALID")
    _require_sha256(descriptor.protocol_sha256, "REAL_WORKFLOW_DESCRIPTOR_INVALID")
    _require_sha256(descriptor.assignment_sha256, "REAL_WORKFLOW_DESCRIPTOR_INVALID")
    _require_sha256(descriptor.selected_inventory_sha256, "REAL_WORKFLOW_DESCRIPTOR_INVALID")
    reference = evidence_policy_reference()
    if descriptor.evidence_policy_sha256 != reference["sha256"]:
        raise _fail("REAL_WORKFLOW_DESCRIPTOR_INVALID")


def _nonnegative_int(value: Any, label: str) -> int:
    if type(value) is not int or value < 0:
        raise _fail(label)
    return value


def _metric(value: Any, label: str, *, nullable: bool) -> float | None:
    if value is None and nullable:
        return None
    if type(value) not in (int, float):
        raise _fail(label)
    result = float(value)
    if not math.isfinite(result) or result < 0 or result > 1:
        raise _fail(label)
    return result


def _failure_counts(value: Any, allowed: set[str], label: str) -> dict[str, int]:
    if not isinstance(value, dict):
        raise _fail(label)
    result: dict[str, int] = {}
    for key, count in value.items():
        if not isinstance(key, str) or len(key) > 128 or key not in allowed:
            raise _fail(label)
        result[key] = _nonnegative_int(count, label)
    return result


def _require_exact_mapping(value: Any, fields: set[str], label: str) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != fields:
        raise _fail(label)
    return value


def _validate_evaluation(value: Any, aggregate: Mapping[str, Any]) -> dict[str, Any]:
    evaluation = _require_exact_mapping(value, _EVALUATION_FIELDS, "REAL_WORKFLOW_EVALUATION_INVALID")
    for name in _EVALUATION_INTEGER_FIELDS:
        _nonnegative_int(evaluation[name], "REAL_WORKFLOW_EVALUATION_INVALID")
    for name in _OPTIONAL_INTEGER_FIELDS:
        if evaluation[name] is not None:
            _nonnegative_int(evaluation[name], "REAL_WORKFLOW_EVALUATION_INVALID")
    for name in (
        "unique_point_value_metrics_available", "relational_row_metrics_available",
        "relational_phase_metrics_available", "artifact_integrity_valid",
    ):
        if type(evaluation[name]) is not bool:
            raise _fail("REAL_WORKFLOW_EVALUATION_INVALID")
    for name in _REQUIRED_METRIC_FIELDS:
        _metric(evaluation[name], "REAL_WORKFLOW_EVALUATION_INVALID", nullable=False)
    for name in _OPTIONAL_METRIC_FIELDS:
        _metric(evaluation[name], "REAL_WORKFLOW_EVALUATION_INVALID", nullable=True)
    failures = _failure_counts(
        evaluation["failure_kinds"], _EVALUATION_FAILURE_CODES,
        "REAL_WORKFLOW_EVALUATION_FAILURE_KIND_INVALID",
    )

    if not evaluation["unique_point_value_metrics_available"]:
        if any(evaluation[name] is not None for name in {
            "unique_point_value_correct", "unique_point_value_incorrect",
            "unique_point_missing", "unique_point_extra", "unique_point_wrong_scale",
            "unique_point_wrong_export_mode", "unique_point_value_precision",
            "unique_point_value_coverage", "matched_unique_point_value_accuracy",
        }):
            raise _fail("REAL_WORKFLOW_EVALUATION_AVAILABILITY_INVALID")
    else:
        for name in {
            "unique_point_value_correct", "unique_point_value_incorrect",
            "unique_point_missing", "unique_point_extra", "unique_point_wrong_scale",
            "unique_point_wrong_export_mode", "unique_point_value_coverage",
        }:
            if evaluation[name] is None:
                raise _fail("REAL_WORKFLOW_EVALUATION_AVAILABILITY_INVALID")

    row_fields = {
        "expected_rows", "structurally_matched_rows", "correct_rows", "missing_rows",
        "extra_rows", "duplicate_rows", "wrong_scale_rows", "wrong_export_mode_rows",
        "wrong_relation_rows", "relational_row_precision", "relational_row_coverage",
        "matched_relational_graph_value_accuracy",
    }
    if not evaluation["relational_row_metrics_available"]:
        if any(evaluation[name] is not None for name in row_fields):
            raise _fail("REAL_WORKFLOW_EVALUATION_AVAILABILITY_INVALID")
    elif any(evaluation[name] is None for name in row_fields - {
        "relational_row_precision", "matched_relational_graph_value_accuracy",
    }):
        raise _fail("REAL_WORKFLOW_EVALUATION_AVAILABILITY_INVALID")

    if not evaluation["relational_phase_metrics_available"]:
        if evaluation["wrong_phase_rows"] is not None or evaluation["matched_relational_phase_accuracy"] is not None:
            raise _fail("REAL_WORKFLOW_EVALUATION_AVAILABILITY_INVALID")
    elif (
        not evaluation["relational_row_metrics_available"]
        or evaluation["wrong_phase_rows"] is None
    ):
        raise _fail("REAL_WORKFLOW_EVALUATION_AVAILABILITY_INVALID")

    if evaluation["truth_cases"] != aggregate["image_groups"] or evaluation["output_cases"] != aggregate["image_groups"]:
        raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    if evaluation["completed_cases"] + evaluation["failed_cases"] != evaluation["truth_cases"]:
        raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    if evaluation["unexpected_cases"] != 0:
        raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    if evaluation["truth_series"] != aggregate["truth_series"] or evaluation["truth_points"] != aggregate["truth_points"]:
        raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    if evaluation["matched_series"] > min(evaluation["truth_series"], evaluation["predicted_series"]):
        raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    if evaluation["matched_points"] > min(evaluation["truth_points"], evaluation["predicted_points"]):
        raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    if evaluation["actual_rows"] < evaluation["residual_artifact_rows_from_failed_cases"]:
        raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    if evaluation["unique_point_value_metrics_available"]:
        if (
            evaluation["unique_point_value_correct"] + evaluation["unique_point_value_incorrect"]
            != evaluation["matched_points"]
            or evaluation["unique_point_missing"] != evaluation["truth_points"] - evaluation["matched_points"]
            or evaluation["unique_point_extra"] != evaluation["predicted_points"] - evaluation["matched_points"]
            or evaluation["unique_point_wrong_scale"] > evaluation["unique_point_value_incorrect"]
            or evaluation["unique_point_wrong_export_mode"] > evaluation["unique_point_value_incorrect"]
        ):
            raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    if evaluation["relational_row_metrics_available"]:
        if (
            evaluation["correct_rows"] > evaluation["structurally_matched_rows"]
            or evaluation["structurally_matched_rows"] > evaluation["actual_rows"]
            or evaluation["missing_rows"] > evaluation["expected_rows"]
        ):
            raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    if sum(failures.values()) < evaluation["failed_cases"]:
        raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    return evaluation


def _validate_envelope(
    value: Any,
    descriptor: RealWorkflowCandidateDescriptor,
    split: str,
    expected_corpus_content_sha256: str | None,
) -> dict[str, Any]:
    envelope = _require_exact_mapping(value, _ENVELOPE_FIELDS, "REAL_WORKFLOW_RESULT_SHAPE_INVALID")
    expected = {
        "candidate_sha256": descriptor.candidate_sha256,
        "protocol_sha256": descriptor.protocol_sha256,
        "assignment_sha256": descriptor.assignment_sha256,
        "selected_inventory_sha256": descriptor.selected_inventory_sha256,
        "split": split,
    }
    for name, identity in expected.items():
        if envelope[name] != identity:
            raise _fail("REAL_WORKFLOW_RESULT_IDENTITY_INVALID")
    content_sha256 = _require_sha256(
        envelope["corpus_content_sha256"], "REAL_WORKFLOW_RESULT_IDENTITY_INVALID",
    )
    if expected_corpus_content_sha256 is not None and content_sha256 != expected_corpus_content_sha256:
        raise _fail("REAL_WORKFLOW_RESULT_IDENTITY_INVALID")
    aggregate = _require_exact_mapping(
        envelope["aggregate"], _AGGREGATE_FIELDS, "REAL_WORKFLOW_AGGREGATE_INVALID",
    )
    if aggregate["schema"] != "graphreader.engauge-grouped-workflow-aggregate.v1":
        raise _fail("REAL_WORKFLOW_AGGREGATE_INVALID")
    for name in _AGGREGATE_INTEGER_FIELDS:
        _nonnegative_int(aggregate[name], "REAL_WORKFLOW_AGGREGATE_INVALID")
    if aggregate["image_groups"] < 1 or aggregate["project_files"] < 1:
        raise _fail("REAL_WORKFLOW_AGGREGATE_INVALID")
    expected_flags = {
        "aggregate_only": True, "case_level_output": False, "truth_rows_output": False,
        "prediction_output": False, "paths_output": False, "names_output": False,
    }
    if any(aggregate[name] is not expected_value for name, expected_value in expected_flags.items()):
        raise _fail("REAL_WORKFLOW_AGGREGATE_SCOPE_INVALID")
    execution_failures = _failure_counts(
        aggregate["execution_failure_kinds"], _EXECUTION_FAILURE_CODES,
        "REAL_WORKFLOW_EXECUTION_FAILURE_KIND_INVALID",
    )
    if aggregate["decoded_image_groups"] > aggregate["image_groups"]:
        raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    if aggregate["workflow_invocations"] != aggregate["decoded_image_groups"]:
        raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    if aggregate["workflow_succeeded_image_groups"] + aggregate["workflow_failed_image_groups"] != aggregate["image_groups"]:
        raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    if aggregate["workflow_succeeded_projects"] + aggregate["workflow_failed_projects"] != aggregate["project_files"]:
        raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    if sum(execution_failures.values()) != aggregate["workflow_failed_image_groups"]:
        raise _fail("REAL_WORKFLOW_AGGREGATE_CONSERVATION_INVALID")
    _validate_evaluation(aggregate["evaluation"], aggregate)
    return envelope


def _reject_duplicate_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise _fail("REAL_WORKFLOW_RESULT_JSON_INVALID")
        result[key] = value
    return result


def _parse_result(
    payload: bytes,
    descriptor: RealWorkflowCandidateDescriptor,
    split: str,
    expected_corpus_content_sha256: str | None,
) -> dict[str, Any]:
    try:
        value = json.loads(
            payload.decode("utf-8", errors="strict"),
            object_pairs_hook=_reject_duplicate_object,
            parse_constant=lambda _: (_ for _ in ()).throw(_fail("REAL_WORKFLOW_RESULT_JSON_INVALID")),
        )
    except FrozenWorkflowProcessError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError, MemoryError):
        raise _fail("REAL_WORKFLOW_RESULT_JSON_INVALID") from None
    return _validate_envelope(value, descriptor, split, expected_corpus_content_sha256)


def _parse_corpus_frame(
    line: bytes,
    attempt_id: str,
    candidate_sha256: str,
) -> tuple[str, str]:
    try:
        text = line.decode("ascii", errors="strict")
    except UnicodeDecodeError:
        raise _fail("REAL_WORKFLOW_CORPUS_FRAME_INVALID") from None
    if len(text) > 512 or "\r" in text or "\n" in text:
        raise _fail("REAL_WORKFLOW_CORPUS_FRAME_INVALID")
    parts = text.split(" ")
    if (
        len(parts) != 5
        or parts[0] != "G22_CORPUS/1"
        or parts[1] != attempt_id
        or parts[2] != candidate_sha256
        or _SHA256.fullmatch(parts[3]) is None
        or _SHA256.fullmatch(parts[4]) is None
    ):
        raise _fail("REAL_WORKFLOW_CORPUS_FRAME_INVALID")
    return parts[3], parts[4]


def _canonical_bytes(value: Any) -> bytes:
    return (json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")


def _offer_event(
    events: Queue[tuple[str, Any]],
    overflow: _ReaderOverflow,
    event: tuple[str, Any],
    evidence_sha256: str,
) -> bool:
    try:
        events.put_nowait(event)
        return True
    except Full:
        overflow.record(evidence_sha256)
        return False


def _offer_eof(events: Queue[tuple[str, Any]], event: tuple[str, None]) -> None:
    try:
        events.put_nowait(event)
    except Full:
        # Thread termination plus an empty queue is also treated as EOF. Never
        # block a reader on bookkeeping while the parent is shutting down.
        pass


def _stdout_reader(
    stream: BinaryIO,
    events: Queue[tuple[str, Any]],
    overflow: _ReaderOverflow,
) -> None:
    try:
        while True:
            raw = stream.readline(_MAX_FRAME_BYTES + 1)
            if not raw:
                break
            if len(raw) > _MAX_FRAME_BYTES or not raw.endswith(b"\n"):
                digest = hashlib.sha256(raw).hexdigest()
                if not _offer_event(events, overflow, ("stdout_invalid", digest), digest):
                    return
                while raw and not raw.endswith(b"\n"):
                    raw = stream.readline(_MAX_FRAME_BYTES + 1)
                continue
            digest = hashlib.sha256(raw).hexdigest()
            if not _offer_event(events, overflow, ("stdout", raw), digest):
                return
    except BaseException:
        digest = hashlib.sha256(b"stdout-reader-error").hexdigest()
        _offer_event(events, overflow, ("reader_error", "stdout"), digest)
    finally:
        _offer_eof(events, ("stdout_eof", None))


def _stderr_reader(
    stream: BinaryIO,
    events: Queue[tuple[str, Any]],
    overflow: _ReaderOverflow,
) -> None:
    try:
        while True:
            raw = stream.read(_STDERR_CHUNK_BYTES)
            if not raw:
                break
            digest = hashlib.sha256(raw).hexdigest()
            if not _offer_event(events, overflow, ("stderr", digest), digest):
                return
    except BaseException:
        digest = hashlib.sha256(b"stderr-reader-error").hexdigest()
        _offer_event(events, overflow, ("reader_error", "stderr"), digest)
    finally:
        _offer_eof(events, ("stderr_eof", None))


def _stop_process(process: subprocess.Popen[bytes]) -> None:
    if process.poll() is None:
        process.kill()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def _write_result_create_new(path: Path, value: Mapping[str, Any]) -> tuple[bytes, str]:
    payload = _canonical_bytes(value)
    created = False
    try:
        with path.open("xb") as stream:
            created = True
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
    except BaseException:
        if created:
            path.unlink(missing_ok=True)
        raise
    return payload, hashlib.sha256(payload).hexdigest()


def run_bound_worker(
    command: Sequence[str],
    working_directory: Path,
    descriptor: RealWorkflowCandidateDescriptor,
    split: str,
    ledger_path: Path | None,
    result_path: Path,
    timeout_seconds: float,
) -> dict[str, Any]:
    """Run one fixed worker command and persist only its validated aggregate."""

    if isinstance(command, (str, bytes)) or not isinstance(command, Sequence) or not command:
        raise _fail("REAL_WORKFLOW_COMMAND_INVALID")
    if (
        any(not isinstance(item, str) or not item or "\x00" in item for item in command)
        or "--attempt-id" in command
    ):
        raise _fail("REAL_WORKFLOW_COMMAND_INVALID")
    _validate_descriptor(descriptor)
    if split not in {"real-dev", "real-sealed"}:
        raise _fail("REAL_WORKFLOW_SPLIT_INVALID")
    if (split == "real-sealed") != (ledger_path is not None):
        raise _fail("REAL_WORKFLOW_LEDGER_MODE_INVALID")
    if type(timeout_seconds) not in (int, float) or not math.isfinite(float(timeout_seconds)) or timeout_seconds <= 0 or timeout_seconds > 86400:
        raise _fail("REAL_WORKFLOW_TIMEOUT_INVALID")
    working_directory = Path(working_directory).resolve()
    result_path = Path(result_path).resolve()
    if not working_directory.is_dir():
        raise _fail("REAL_WORKFLOW_WORKING_DIRECTORY_INVALID")
    if result_path.exists() or not result_path.parent.is_dir():
        raise _fail("REAL_WORKFLOW_RESULT_PATH_INVALID")
    if ledger_path is not None and Path(ledger_path).resolve() == result_path:
        raise _fail("REAL_WORKFLOW_RESULT_PATH_INVALID")

    attempt_id = uuid4().hex
    attempt: RealWorkflowAttempt | None = None
    process: subprocess.Popen[bytes] | None = None
    disclosed = False
    failure: FrozenWorkflowProcessError | None = None
    result: dict[str, Any] | None = None
    terminal_kind: str | None = None
    first_read_recorded = False
    corpus_content_sha256: str | None = None
    events: Queue[tuple[str, Any]] = Queue(maxsize=_EVENT_BUFFER_CAPACITY)
    overflow = _ReaderOverflow()
    threads: list[threading.Thread] = []

    def disclose(evidence_sha256: str) -> None:
        nonlocal disclosed
        if attempt is not None and first_read_recorded and not disclosed:
            record_disclosure(attempt, evidence_sha256=evidence_sha256, disclosures=_DISCLOSURES)
            disclosed = True

    def bind_corpus_frame(line: bytes, *, acknowledge: bool) -> None:
        nonlocal corpus_content_sha256
        if attempt is None or split != "real-sealed" or not first_read_recorded:
            raise _fail("REAL_WORKFLOW_CORPUS_FRAME_INVALID")
        content_sha256, nonce = _parse_corpus_frame(
            line, attempt_id, descriptor.candidate_sha256,
        )
        if corpus_content_sha256 is not None:
            raise _fail("REAL_WORKFLOW_CORPUS_FRAME_REPEATED")
        record_corpus_content(attempt, content_sha256)
        corpus_content_sha256 = content_sha256
        if acknowledge:
            if process is None or process.stdin is None:
                raise _fail("REAL_WORKFLOW_CORPUS_ACK_FAILED")
            process.stdin.write(f"{_CORPUS_ACK_PREFIX} {nonce}\n".encode("ascii"))
            process.stdin.flush()

    def inspect_drained_event(kind: str, data: Any) -> None:
        if kind in {"stdout_eof", "stderr_eof", "reader_error"}:
            return
        if kind in {"stdout_invalid", "stderr"}:
            disclose(str(data))
            return
        if kind != "stdout" or not isinstance(data, bytes):
            disclose(hashlib.sha256(b"unknown-reader-event").hexdigest())
            return
        raw = data
        digest = hashlib.sha256(raw).hexdigest()
        if b"\r" in raw or not raw.endswith(b"\n"):
            disclose(digest)
            return
        if raw == _FAILURE_FRAME:
            return
        line = raw[:-1]
        if line.startswith(_RESULT_PREFIX):
            try:
                _parse_result(
                    line[len(_RESULT_PREFIX):], descriptor, split,
                    corpus_content_sha256 if split == "real-sealed" else None,
                )
                return
            except FrozenWorkflowProcessError:
                disclose(digest)
                return
        if line.startswith(_CORPUS_PREFIX):
            try:
                bind_corpus_frame(line, acknowledge=False)
            except FrozenWorkflowProcessError:
                # A syntactically valid repeated or out-of-sequence content
                # identity is an accounting failure, not case disclosure.
                try:
                    _parse_corpus_frame(line, attempt_id, descriptor.candidate_sha256)
                except FrozenWorkflowProcessError:
                    disclose(digest)
            except RuntimeError:
                # A valid baseline mismatch is consumed and failed, without a
                # claim that the identity-only frame disclosed case material.
                pass
            return
        if line.startswith(_FIRST_READ_PREFIX) and not first_read_recorded:
            return
        disclose(digest)

    try:
        if split == "real-sealed":
            if ledger_path is None:  # guarded above; retained for optimized mode
                raise _fail("REAL_WORKFLOW_LEDGER_MODE_INVALID")
            attempt = open_attempt(Path(ledger_path), descriptor, attempt_id=attempt_id)
        argv = [*command, "--attempt-id", attempt_id]
        process = subprocess.Popen(
            argv,
            cwd=working_directory,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=False,
        )
        if process.stdin is None or process.stdout is None or process.stderr is None:
            raise _fail("REAL_WORKFLOW_PIPE_SETUP_FAILED")
        threads = [
            threading.Thread(target=_stdout_reader, args=(process.stdout, events, overflow), daemon=True),
            threading.Thread(target=_stderr_reader, args=(process.stderr, events, overflow), daemon=True),
        ]
        for thread in threads:
            thread.start()

        bridge = FirstReadAccountingBridge(attempt) if attempt is not None else None
        stdout_eof = False
        stderr_eof = False
        deadline = time.monotonic() + float(timeout_seconds)

        while not (
            ((stdout_eof and stderr_eof) or all(not thread.is_alive() for thread in threads))
            and process.poll() is not None
            and events.empty()
        ):
            overflow_digest = overflow.digest()
            if overflow_digest is not None:
                disclose(overflow_digest)
                failure = _fail("REAL_WORKFLOW_EVENT_BUFFER_OVERFLOW")
                break
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                failure = _fail("REAL_WORKFLOW_PROCESS_TIMEOUT")
                break
            try:
                kind, data = events.get(timeout=min(remaining, 0.05))
            except Empty:
                continue
            if kind == "stdout_eof":
                stdout_eof = True
                continue
            if kind == "stderr_eof":
                stderr_eof = True
                continue
            if kind in {"stdout_invalid", "stderr", "reader_error"}:
                digest = data if kind != "reader_error" else hashlib.sha256(str(data).encode("ascii")).hexdigest()
                disclose(digest)
                failure = _fail("REAL_WORKFLOW_UNEXPECTED_OUTPUT")
                break

            raw: bytes = data
            digest = hashlib.sha256(raw).hexdigest()
            if b"\r" in raw:
                disclose(digest)
                failure = _fail("REAL_WORKFLOW_FRAME_INVALID")
                break
            line = raw[:-1]
            if terminal_kind is not None:
                disclose(digest)
                failure = _fail("REAL_WORKFLOW_UNEXPECTED_OUTPUT")
                break
            if line.startswith(_FIRST_READ_PREFIX):
                if bridge is None:
                    failure = _fail("REAL_WORKFLOW_DEV_FIRST_READ_FORBIDDEN")
                    break
                if first_read_recorded:
                    disclose(digest)
                    failure = _fail("REAL_WORKFLOW_UNEXPECTED_OUTPUT")
                    break
                try:
                    def write_ack(reply: str) -> None:
                        nonlocal first_read_recorded
                        # FirstReadAccountingBridge invokes this callback only
                        # after the consumed state is durable.
                        first_read_recorded = True
                        if process.stdin is None:
                            raise BrokenPipeError()
                        process.stdin.write(reply.encode("ascii"))
                        process.stdin.flush()

                    bridge.handle_request(
                        line.decode("ascii", errors="strict"),
                        write_ack,
                    )
                    first_read_recorded = True
                except (UnicodeDecodeError, OSError, RuntimeError):
                    failure = _fail("REAL_WORKFLOW_FIRST_READ_FAILED")
                    break
                continue
            if line.startswith(_CORPUS_PREFIX):
                if bridge is None or not first_read_recorded or corpus_content_sha256 is not None:
                    try:
                        _parse_corpus_frame(line, attempt_id, descriptor.candidate_sha256)
                    except FrozenWorkflowProcessError:
                        disclose(digest)
                    failure = _fail("REAL_WORKFLOW_CORPUS_FRAME_INVALID")
                    break
                try:
                    bind_corpus_frame(line, acknowledge=True)
                except FrozenWorkflowProcessError as error:
                    if str(error) == "REAL_WORKFLOW_CORPUS_FRAME_INVALID":
                        disclose(digest)
                    failure = _fail("REAL_WORKFLOW_CORPUS_BINDING_FAILED")
                    break
                except (OSError, RuntimeError):
                    failure = _fail("REAL_WORKFLOW_CORPUS_BINDING_FAILED")
                    break
                continue
            if raw == _FAILURE_FRAME:
                terminal_kind = "failure"
                continue
            if line.startswith(_RESULT_PREFIX):
                if bridge is not None and not first_read_recorded:
                    failure = _fail("REAL_WORKFLOW_FIRST_READ_MISSING")
                    break
                if bridge is not None and corpus_content_sha256 is None:
                    try:
                        _parse_result(line[len(_RESULT_PREFIX):], descriptor, split, None)
                    except FrozenWorkflowProcessError:
                        disclose(digest)
                    failure = _fail("REAL_WORKFLOW_CORPUS_FRAME_MISSING")
                    break
                try:
                    result = _parse_result(
                        line[len(_RESULT_PREFIX):], descriptor, split,
                        corpus_content_sha256 if bridge is not None else None,
                    )
                except FrozenWorkflowProcessError:
                    disclose(digest)
                    failure = _fail("REAL_WORKFLOW_RESULT_INVALID")
                    break
                terminal_kind = "result"
                continue
            disclose(digest)
            failure = _fail("REAL_WORKFLOW_UNEXPECTED_OUTPUT")
            break

        if failure is not None:
            raise failure
        return_code = process.wait(timeout=max(0.01, deadline - time.monotonic()))
        for thread in threads:
            # The process has exited and all queue writes are nonblocking, so
            # both pipe readers must reach EOF without a shutdown deadlock.
            thread.join()
        if terminal_kind == "failure":
            if return_code != 2:
                raise _fail("REAL_WORKFLOW_FAILURE_EXIT_INVALID")
            raise _fail("REAL_WORKFLOW_FAILED")
        if terminal_kind != "result" or result is None or return_code != 0:
            raise _fail("REAL_WORKFLOW_RESULT_MISSING")

        _, result_sha256 = _write_result_create_new(result_path, result)
        if attempt is not None:
            complete_attempt(attempt, aggregate_result_sha256=result_sha256)
        return json.loads(json.dumps(result))
    except BaseException as error:
        if process is not None:
            _stop_process(process)
        for thread in threads:
            # Preserve the attempt lease until every already-emitted byte has
            # either been queued or reduced to an overflow digest.
            thread.join()
        overflow_digest = overflow.digest()
        if overflow_digest is not None:
            disclose(overflow_digest)
        while True:
            try:
                kind, data = events.get_nowait()
            except Empty:
                break
            inspect_drained_event(kind, data)
        sanitized = error if isinstance(error, FrozenWorkflowProcessError) else _fail("REAL_WORKFLOW_PROCESS_FAILED")
        if attempt is not None and not attempt._closed:
            try:
                record_attempt_failure(attempt, sanitized)
            except BaseException:
                attempt.close()
        raise sanitized from None
    finally:
        if process is not None:
            for stream in (process.stdin, process.stdout, process.stderr):
                if stream is not None:
                    try:
                        stream.close()
                    except OSError:
                        pass
        if attempt is not None and not attempt._closed:
            attempt.close()
