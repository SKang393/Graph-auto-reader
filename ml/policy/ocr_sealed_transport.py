# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bounded aggregate-only transport for the Goal 22 OCR sealed worker."""

from __future__ import annotations

from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass
import hashlib
import json
import math
from pathlib import Path
from queue import Empty, Full, Queue
import re
import subprocess
import threading
import time
from typing import Any, BinaryIO


RESULT_SCHEMA = "graphreader.original-db-ocr-sealed-worker-result.v1"
ACCEPTANCE_SCOPE = "goal22.full-ocr.five-axis-family.real-range.v1"
COVERAGE_PROTOCOL_SHA256 = "26ab0e017ccc6dc17d26effe11b1fb40f440ed4471e896c1afac3c8cc96999c4"
METRIC_REFERENCE_SHA256 = "8b879664c33e83f2aaec0e637f4f0f4df0f9701fe04ac43867672dde2e662656"

_FIRST_READ_PREFIX = b"G22_FIRST_READ/1 "
_POSITIVE_READ_PREFIX = b"G22_POSITIVE_READ/1 "
_ACK_PREFIX = "G22_READ_ACK/1"
_SHA256 = re.compile(r"[0-9a-f]{64}\Z")
_MAX_STDOUT_FRAME_BYTES = 256 * 1024
_STDERR_CHUNK_BYTES = 16 * 1024
_EVENT_CAPACITY = 8
_MAX_SOURCES = 128
_MAX_REGIONS_PER_SOURCE = 1024
_MAX_REGIONS_PER_CORPUS = 16384

_ENVELOPE_FIELDS = {
    "schema", "status", "split", "acceptance_scope", "attempt_id",
    "admission_binding_sha256", "set_id", "candidate_sha256", "archive_sha256",
    "archive_manifest_sha256", "coverage_protocol_sha256", "request_sha256",
    "metric_reference_sha256", "execution_provider", "cpu_threads",
    "graph_optimization", "model_inference", "case_output", "production_approved",
    "elapsed_ms", "aggregate",
}
_AGGREGATE_FIELDS = {"source_count", "panel_count", "metrics"}
_METRICS_FIELDS = {
    "raw_detector_geometry", "successfully_recognized_region_geometry",
    "recognition_failures", "full_ocr_metrics",
}
_GEOMETRY_FIELDS = {
    "truth_region_count", "predicted_region_count", "true_positives",
    "false_positives", "false_negatives", "precision", "recall",
    "intersection_over_union_minimum",
}
_RECOGNITION_FAILURE_FIELDS = {"raw_regions_without_successful_recognition"}
_FULL_FIELDS = {
    "truth_region_count", "predicted_region_count", "geometry_matched_region_count",
    "geometry_false_positive_count", "geometry_false_negative_count",
    "recognition_exact_count", "recognition_exact_accuracy", "truth_character_count",
    "matched_pair_edit_count", "unmatched_truth_deletion_edit_count",
    "unmatched_prediction_insertion_edit_count", "character_error_count",
    "character_error_rate", "role_correct_count", "role_accuracy",
    "by_expected_runtime_role", "intersection_over_union_minimum",
}
_ROLE_FIELDS = {"truth_count", "correct_count", "accuracy"}
_RUNTIME_ROLES = {
    "annotation", "axistitle", "legendtext", "participant", "phaseheading", "xtick", "ytick",
}


class OcrSealedTransportError(RuntimeError):
    """Sanitized transport failure that never includes child output."""

    def __init__(self, code: str):
        self.code = code
        super().__init__(code)


@dataclass(frozen=True)
class OcrSealedRequestIdentity:
    attempt_id: str
    admission_binding_sha256: str
    set_id: str
    candidate_sha256: str
    archive_sha256: str
    archive_manifest_sha256: str
    coverage_protocol_sha256: str
    request_sha256: str
    source_count: int
    acceptance_scope: str = ACCEPTANCE_SCOPE


@dataclass(frozen=True)
class OcrSealedTransportResult:
    envelope: dict[str, object]
    stderr_sha256: str
    stderr_byte_count: int
    elapsed_seconds: float


def _fail(code: str) -> OcrSealedTransportError:
    return OcrSealedTransportError(code)


def _object(value: object, fields: set[str]) -> Mapping[str, object]:
    if type(value) is not dict or set(value) != fields or any(type(key) is not str for key in value):
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
    return value


def _integer(value: object, maximum: int = 2**63 - 1) -> int:
    if type(value) is not int or value < 0 or value > maximum:
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
    return value


def _number(value: object, *, minimum: float | None = None, maximum: float | None = None) -> float:
    if type(value) not in (int, float):
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
    try:
        result = float(value)
    except (OverflowError, ValueError):
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID") from None
    if not math.isfinite(result):
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
    if (minimum is not None and result < minimum) or (maximum is not None and result > maximum):
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
    return result


def _ratio(value: object, numerator: int, denominator: int) -> None:
    observed = _number(value, minimum=0.0)
    expected = numerator / max(1, denominator)
    if not math.isclose(observed, expected, rel_tol=1e-12, abs_tol=1e-12):
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")


def _validate_geometry(value: object) -> dict[str, int]:
    row = _object(value, _GEOMETRY_FIELDS)
    truth = _integer(row["truth_region_count"])
    predicted = _integer(row["predicted_region_count"])
    matched = _integer(row["true_positives"])
    false_positive = _integer(row["false_positives"])
    false_negative = _integer(row["false_negatives"])
    if matched > min(truth, predicted) or false_positive != predicted - matched or false_negative != truth - matched:
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
    _ratio(row["precision"], matched, predicted)
    _ratio(row["recall"], matched, truth)
    if _number(row["intersection_over_union_minimum"]) != 0.5:
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
    return {"truth": truth, "predicted": predicted, "matched": matched}


def _validate_full(value: object) -> dict[str, int]:
    row = _object(value, _FULL_FIELDS)
    truth = _integer(row["truth_region_count"])
    predicted = _integer(row["predicted_region_count"])
    matched = _integer(row["geometry_matched_region_count"])
    false_positive = _integer(row["geometry_false_positive_count"])
    false_negative = _integer(row["geometry_false_negative_count"])
    exact = _integer(row["recognition_exact_count"])
    characters = _integer(row["truth_character_count"])
    matched_edits = _integer(row["matched_pair_edit_count"])
    deleted_edits = _integer(row["unmatched_truth_deletion_edit_count"])
    inserted_edits = _integer(row["unmatched_prediction_insertion_edit_count"])
    total_edits = _integer(row["character_error_count"])
    role_correct = _integer(row["role_correct_count"])
    if (
        truth <= 0 or characters < truth or matched > min(truth, predicted)
        or false_positive != predicted - matched or false_negative != truth - matched
        or exact > matched or role_correct > matched
        or total_edits != matched_edits + deleted_edits + inserted_edits
        or deleted_edits < false_negative
    ):
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
    _ratio(row["recognition_exact_accuracy"], exact, truth)
    _ratio(row["character_error_rate"], total_edits, characters)
    _ratio(row["role_accuracy"], role_correct, truth)
    if _number(row["intersection_over_union_minimum"]) != 0.5:
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")

    roles = _object(row["by_expected_runtime_role"], _RUNTIME_ROLES)
    role_truth = 0
    role_correct_total = 0
    for role in sorted(_RUNTIME_ROLES):
        counts = _object(roles[role], _ROLE_FIELDS)
        role_truth_count = _integer(counts["truth_count"])
        role_correct_count = _integer(counts["correct_count"])
        if role_truth_count <= 0 or role_correct_count > role_truth_count:
            raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
        _ratio(counts["accuracy"], role_correct_count, role_truth_count)
        role_truth += role_truth_count
        role_correct_total += role_correct_count
    if role_truth != truth or role_correct_total != role_correct:
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
    return {"truth": truth, "predicted": predicted, "matched": matched}


def _validate_expected(expected: OcrSealedRequestIdentity) -> None:
    if not isinstance(expected, OcrSealedRequestIdentity):
        raise _fail("OCR_SEALED_TRANSPORT_ARGUMENT_INVALID")
    if (
        not isinstance(expected.attempt_id, str)
        or not 1 <= len(expected.attempt_id) <= 128
        or any(character < "!" or character > "~" for character in expected.attempt_id)
        or expected.acceptance_scope != ACCEPTANCE_SCOPE
        or expected.coverage_protocol_sha256 != COVERAGE_PROTOCOL_SHA256
        or type(expected.source_count) is not int
        or not 1 <= expected.source_count <= _MAX_SOURCES
    ):
        raise _fail("OCR_SEALED_TRANSPORT_ARGUMENT_INVALID")
    for value in (
        expected.admission_binding_sha256, expected.set_id, expected.candidate_sha256,
        expected.archive_sha256, expected.archive_manifest_sha256, expected.request_sha256,
    ):
        if not isinstance(value, str) or _SHA256.fullmatch(value) is None:
            raise _fail("OCR_SEALED_TRANSPORT_ARGUMENT_INVALID")


def validate_result_envelope(
    value: object,
    expected: OcrSealedRequestIdentity,
) -> dict[str, object]:
    """Validate one deserialized worker envelope and return a detached plain dictionary."""

    _validate_expected(expected)
    row = _object(value, _ENVELOPE_FIELDS)
    identities = {
        "schema": RESULT_SCHEMA,
        "status": "completed",
        "split": "sealed",
        "acceptance_scope": expected.acceptance_scope,
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
    }
    if any(type(row[key]) is not type(expected_value) or row[key] != expected_value
           for key, expected_value in identities.items()):
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
    _number(row["elapsed_ms"], minimum=0.0)

    aggregate = _object(row["aggregate"], _AGGREGATE_FIELDS)
    source_count = _integer(aggregate["source_count"], _MAX_SOURCES)
    panel_count = _integer(aggregate["panel_count"], 2**31 - 1)
    if source_count != expected.source_count or panel_count < source_count or panel_count > 2**31 - 1:
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
    metrics = _object(aggregate["metrics"], _METRICS_FIELDS)
    raw = _validate_geometry(metrics["raw_detector_geometry"])
    recognized = _validate_geometry(metrics["successfully_recognized_region_geometry"])
    failures = _object(metrics["recognition_failures"], _RECOGNITION_FAILURE_FIELDS)
    recognition_failures = _integer(failures["raw_regions_without_successful_recognition"])
    full = _validate_full(metrics["full_ocr_metrics"])
    maximum_regions = min(
        _MAX_REGIONS_PER_CORPUS,
        source_count * _MAX_REGIONS_PER_SOURCE,
    )
    if (
        raw["truth"] != recognized["truth"] or raw["truth"] != full["truth"]
        or raw["truth"] > maximum_regions or raw["predicted"] > maximum_regions
        or recognized["predicted"] > raw["predicted"]
        or recognized["matched"] > raw["matched"]
        or recognition_failures != raw["predicted"] - recognized["predicted"]
        or full != recognized
    ):
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
    return json.loads(json.dumps(row, allow_nan=False))


def _decode_json(payload: bytes) -> object:
    def reject_duplicate(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
            result[key] = value
        return result

    try:
        text = payload.decode("utf-8", errors="strict")
        return json.loads(
            text,
            object_pairs_hook=reject_duplicate,
            parse_constant=lambda _value: (_ for _ in ()).throw(
                _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID")
            ),
        )
    except OcrSealedTransportError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError, ValueError, TypeError):
        raise _fail("OCR_SEALED_TRANSPORT_RESULT_INVALID") from None


class _PipeState:
    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.unsafe = False
        self.stdout_done = threading.Event()
        self.stderr_done = threading.Event()
        self.stderr_hash = hashlib.sha256()
        self.stderr_count = 0

    def mark_unsafe(self) -> None:
        with self.lock:
            self.unsafe = True

    def add_stderr(self, payload: bytes) -> None:
        with self.lock:
            self.stderr_hash.update(payload)
            self.stderr_count += len(payload)

    def snapshot(self) -> tuple[bool, str, int]:
        with self.lock:
            return self.unsafe, self.stderr_hash.hexdigest(), self.stderr_count


def _stdout_reader(stream: BinaryIO, events: Queue[bytes], state: _PipeState) -> None:
    try:
        while True:
            frame = stream.readline(_MAX_STDOUT_FRAME_BYTES + 1)
            if not frame:
                return
            if len(frame) > _MAX_STDOUT_FRAME_BYTES or not frame.endswith(b"\n"):
                state.mark_unsafe()
                continue
            try:
                events.put_nowait(frame)
            except Full:
                state.mark_unsafe()
    except BaseException:
        state.mark_unsafe()
    finally:
        state.stdout_done.set()


def _stderr_reader(stream: BinaryIO, state: _PipeState) -> None:
    try:
        while True:
            payload = stream.read(_STDERR_CHUNK_BYTES)
            if not payload:
                return
            state.add_stderr(payload)
    except BaseException:
        state.mark_unsafe()
    finally:
        state.stderr_done.set()


def _line(frame: bytes) -> bytes:
    if frame.endswith(b"\r\n"):
        line = frame[:-2]
    elif frame.endswith(b"\n") and b"\r" not in frame:
        line = frame[:-1]
    else:
        raise _fail("OCR_SEALED_TRANSPORT_UNSAFE_OUTPUT")
    if not line or b"\r" in line:
        raise _fail("OCR_SEALED_TRANSPORT_UNSAFE_OUTPUT")
    return line


def _parse_identity_frame(line: bytes, prefix: bytes) -> tuple[str, str, str]:
    if not line.startswith(prefix):
        raise _fail("OCR_SEALED_TRANSPORT_PROTOCOL_INVALID")
    try:
        fields = line[len(prefix):].decode("ascii", errors="strict").split(" ")
    except UnicodeDecodeError:
        raise _fail("OCR_SEALED_TRANSPORT_PROTOCOL_INVALID") from None
    if len(fields) != 3 or any(not field for field in fields):
        raise _fail("OCR_SEALED_TRANSPORT_PROTOCOL_INVALID")
    return fields[0], fields[1], fields[2]


def _stop_process(process: subprocess.Popen[bytes]) -> bool:
    if process.poll() is None:
        try:
            process.kill()
        except OSError:
            pass
    try:
        process.wait(timeout=5)
    except OSError:
        return process.poll() is not None
    except subprocess.TimeoutExpired:
        try:
            process.kill()
            process.wait(timeout=5)
        except (OSError, subprocess.TimeoutExpired):
            return False
    return process.poll() is not None


def run_ocr_sealed_worker(
    command: Sequence[str],
    cwd: Path,
    expected: OcrSealedRequestIdentity,
    send_ack: Callable[[str, str, Callable[[str], None]], object],
    confirm_positive_read: Callable[[str, str, str], object],
    *,
    timeout_seconds: float = 300.0,
) -> OcrSealedTransportResult:
    """Run one worker session and return only its validated aggregate envelope.

    Durable bookkeeping callbacks and pipe writes are trusted synchronous operations.
    The deadline is checked immediately before and after each such operation.
    """

    _validate_expected(expected)
    if (
        isinstance(command, (str, bytes)) or not isinstance(command, Sequence) or not command
        or any(not isinstance(item, str) or not item or "\x00" in item for item in command)
        or not callable(send_ack) or not callable(confirm_positive_read)
        or type(timeout_seconds) not in (int, float)
        or not math.isfinite(float(timeout_seconds)) or not 1 <= float(timeout_seconds) <= 3600
    ):
        raise _fail("OCR_SEALED_TRANSPORT_ARGUMENT_INVALID")
    try:
        working_directory = Path(cwd).resolve()
    except (OSError, TypeError, ValueError):
        raise _fail("OCR_SEALED_TRANSPORT_ARGUMENT_INVALID") from None
    if not working_directory.is_dir():
        raise _fail("OCR_SEALED_TRANSPORT_ARGUMENT_INVALID")

    started = time.monotonic()
    deadline = started + float(timeout_seconds)
    process: subprocess.Popen[bytes] | None = None
    threads: list[threading.Thread] = []
    events: Queue[bytes] = Queue(maxsize=_EVENT_CAPACITY)
    state = _PipeState()
    first_read_seen = False
    ack_sent = False
    positive_confirmed = False
    nonce: str | None = None
    result: dict[str, object] | None = None
    active = True
    write_lock = threading.Lock()
    ack_writes = 0
    ack_invalid = False

    def remaining() -> float:
        value = deadline - time.monotonic()
        if value <= 0:
            raise _fail("OCR_SEALED_TRANSPORT_TIMEOUT")
        return value

    try:
        try:
            process = subprocess.Popen(
                list(command), cwd=working_directory, stdin=subprocess.PIPE,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, shell=False, bufsize=0,
            )
        except (OSError, ValueError):
            raise _fail("OCR_SEALED_TRANSPORT_PROCESS_FAILED") from None
        if process.stdin is None or process.stdout is None or process.stderr is None:
            raise _fail("OCR_SEALED_TRANSPORT_PROCESS_FAILED")
        threads = [
            threading.Thread(target=_stdout_reader, args=(process.stdout, events, state), daemon=True),
            threading.Thread(target=_stderr_reader, args=(process.stderr, state), daemon=True),
        ]
        for thread in threads:
            thread.start()

        while True:
            unsafe, _, _ = state.snapshot()
            if unsafe:
                raise _fail("OCR_SEALED_TRANSPORT_UNSAFE_OUTPUT")
            if process.poll() is not None and state.stdout_done.is_set() and events.empty():
                break
            try:
                frame = events.get(timeout=min(remaining(), 0.05))
            except Empty:
                continue
            line = _line(frame)
            if result is not None:
                raise _fail(
                    "OCR_SEALED_TRANSPORT_PROTOCOL_INVALID"
                    if line.startswith(b"{") else "OCR_SEALED_TRANSPORT_UNSAFE_OUTPUT"
                )
            if line.startswith(_FIRST_READ_PREFIX):
                if first_read_seen:
                    raise _fail("OCR_SEALED_TRANSPORT_PROTOCOL_INVALID")
                attempt, candidate, request_nonce = _parse_identity_frame(line, _FIRST_READ_PREFIX)
                if (
                    attempt != expected.attempt_id or candidate != expected.candidate_sha256
                    or _SHA256.fullmatch(request_nonce) is None
                ):
                    raise _fail("OCR_SEALED_TRANSPORT_PROTOCOL_INVALID")
                first_read_seen = True
                nonce = request_nonce

                expected_ack = f"{_ACK_PREFIX} {request_nonce}\n"

                def write_and_flush(frame: str) -> None:
                    nonlocal ack_writes, ack_invalid
                    with write_lock:
                        if (
                            type(frame) is not str or frame != expected_ack or not active
                            or ack_writes != 0 or process is None or process.stdin is None
                        ):
                            ack_invalid = True
                            raise OSError()
                        ack_writes += 1
                        try:
                            process.stdin.write(frame.encode("ascii"))
                            process.stdin.flush()
                        except OSError:
                            ack_invalid = True
                            raise

                remaining()
                try:
                    send_ack(attempt, request_nonce, write_and_flush)
                except BaseException:
                    raise _fail("OCR_SEALED_TRANSPORT_ACK_FAILED") from None
                remaining()
                if ack_invalid or ack_writes != 1:
                    raise _fail("OCR_SEALED_TRANSPORT_ACK_FAILED")
                ack_sent = True
                continue
            if line.startswith(_POSITIVE_READ_PREFIX):
                if not first_read_seen or not ack_sent or positive_confirmed or nonce is None:
                    raise _fail("OCR_SEALED_TRANSPORT_PROTOCOL_INVALID")
                attempt, candidate, receipt_nonce = _parse_identity_frame(line, _POSITIVE_READ_PREFIX)
                if (attempt, candidate, receipt_nonce) != (
                    expected.attempt_id, expected.candidate_sha256, nonce,
                ):
                    raise _fail("OCR_SEALED_TRANSPORT_POSITIVE_READ_INVALID")
                remaining()
                try:
                    confirm_positive_read(attempt, candidate, receipt_nonce)
                except BaseException:
                    raise _fail("OCR_SEALED_TRANSPORT_POSITIVE_READ_FAILED") from None
                remaining()
                positive_confirmed = True
                continue
            if not line.startswith(b"{"):
                raise _fail("OCR_SEALED_TRANSPORT_UNSAFE_OUTPUT")
            if not positive_confirmed:
                raise _fail("OCR_SEALED_TRANSPORT_POSITIVE_READ_MISSING")
            result = validate_result_envelope(_decode_json(line), expected)

        return_code = process.wait(timeout=remaining())
        if return_code != 0:
            raise _fail("OCR_SEALED_TRANSPORT_CHILD_FAILED")
        if not first_read_seen or not ack_sent or not positive_confirmed:
            raise _fail("OCR_SEALED_TRANSPORT_POSITIVE_READ_MISSING")
        if result is None:
            raise _fail("OCR_SEALED_TRANSPORT_RESULT_MISSING")
        for thread in threads:
            thread.join(timeout=remaining())
            if thread.is_alive():
                raise _fail("OCR_SEALED_TRANSPORT_TIMEOUT")
        unsafe, stderr_sha256, stderr_byte_count = state.snapshot()
        if unsafe:
            raise _fail("OCR_SEALED_TRANSPORT_UNSAFE_OUTPUT")
        return OcrSealedTransportResult(
            envelope=result,
            stderr_sha256=stderr_sha256,
            stderr_byte_count=stderr_byte_count,
            elapsed_seconds=time.monotonic() - started,
        )
    except OcrSealedTransportError:
        raise
    except (OSError, subprocess.SubprocessError):
        raise _fail("OCR_SEALED_TRANSPORT_PROCESS_FAILED") from None
    except BaseException:
        raise _fail("OCR_SEALED_TRANSPORT_FAILED") from None
    finally:
        active = False
        cleanup_failed = False
        if process is not None:
            if result is None or process.poll() is None:
                cleanup_failed = not _stop_process(process)
            for stream in (process.stdin, process.stdout, process.stderr):
                if stream is not None:
                    try:
                        stream.close()
                    except OSError:
                        pass
        for thread in threads:
            thread.join(timeout=1)
            cleanup_failed = cleanup_failed or thread.is_alive()
        if cleanup_failed:
            raise _fail("OCR_SEALED_TRANSPORT_CLEANUP_FAILED")


__all__ = [
    "ACCEPTANCE_SCOPE",
    "COVERAGE_PROTOCOL_SHA256",
    "METRIC_REFERENCE_SHA256",
    "OcrSealedRequestIdentity",
    "OcrSealedTransportError",
    "OcrSealedTransportResult",
    "RESULT_SCHEMA",
    "run_ocr_sealed_worker",
    "validate_result_envelope",
]
