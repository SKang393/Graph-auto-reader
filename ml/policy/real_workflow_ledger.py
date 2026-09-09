# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Durable first-read accounting for aggregate-only real workflow evaluation.

The ledger contains identities and state only. It never opens corpus payloads,
authorizes an evaluation, or stores case-level outputs.
"""

from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass, field
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import time
from typing import Any, Iterator, Sequence
from uuid import uuid4

from ml.policy.evidence_policy import (
    classify_candidate_failure,
    consumes_candidate_budget,
    evidence_policy_reference,
    load_evidence_policy,
)


LEDGER_SCHEMA = "graphreader.real-workflow-sealed-use-ledger.v1"
DISCLOSURE_KINDS = frozenset({"case_identity", "truth", "prediction", "pixel"})
_LEDGER_FIELDS = {
    "schema", "evidence_policy", "assignment_sha256", "selected_inventory_sha256",
    "split", "generation", "candidates", "retirement", "integrity_sha256",
}
_CANDIDATE_FIELDS = {
    "revision", "candidate_id", "candidate_sha256", "protocol_sha256",
    "attempts", "consumed_attempt_id",
}
_ATTEMPT_FIELDS = {
    "attempt_id", "status", "opened_utc", "first_read_utc", "completed_utc",
    "aggregate_result_sha256", "exception",
}
_RETIREMENT_FIELDS = {
    "revision", "candidate_id", "attempt_id", "disclosures",
    "evidence_sha256", "retired_utc",
}
_STATUSES = {
    "active", "consumed", "void", "failed_consumed", "completed_aggregate",
    "completed_disclosed",
}


class RealWorkflowLedgerError(RuntimeError):
    """Raised when an accounting identity or transition is invalid."""


@dataclass(frozen=True)
class RealWorkflowCandidateDescriptor:
    revision: str
    candidate_id: str
    candidate_sha256: str
    protocol_sha256: str
    evidence_policy_sha256: str
    assignment_sha256: str
    selected_inventory_sha256: str


@dataclass
class RealWorkflowAttempt:
    ledger_path: Path
    descriptor: RealWorkflowCandidateDescriptor
    attempt_id: str
    _lease_stream: Any = field(repr=False, compare=False)
    _closed: bool = field(default=False, init=False, repr=False, compare=False)

    def close(self) -> None:
        if not self._closed:
            _unlock(self._lease_stream)
            self._lease_stream.close()
            self._closed = True

    def __enter__(self) -> RealWorkflowAttempt:
        if self._closed:
            raise RealWorkflowLedgerError("attempt lease is closed")
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:
            pass


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _canonical_bytes(value: Any) -> bytes:
    return (json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")


def _sha256(value: Any, label: str) -> str:
    if (
        not isinstance(value, str)
        or len(value) != 64
        or value != value.casefold()
        or any(character not in "0123456789abcdef" for character in value)
    ):
        raise RealWorkflowLedgerError(f"{label} must be a lowercase SHA-256 value")
    return value


def _identity(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value or value.strip() != value or len(value) > 256:
        raise RealWorkflowLedgerError(f"{label} must be a nonempty exact identifier")
    return value


def _policy() -> tuple[dict[str, object], int]:
    if not consumes_candidate_budget("sealed"):
        raise RealWorkflowLedgerError("shared policy no longer consumes budget on sealed reads")
    reference = evidence_policy_reference()
    policy = load_evidence_policy()
    reuse = policy["sealed_set_reuse"]
    maximum = reuse.get("maximum_distinct_revisions")
    if (
        type(maximum) is not int
        or maximum < 1
        or reuse.get("aggregate_only_preserves_set") is not True
        or reuse.get("retire_on_case_identity_or_truth_or_prediction_or_pixel_disclosure") is not True
    ):
        raise RealWorkflowLedgerError("shared sealed-set reuse policy is invalid")
    return reference, maximum


def _descriptor_record(descriptor: RealWorkflowCandidateDescriptor) -> dict[str, str]:
    reference, _ = _policy()
    record = {
        "revision": _identity(descriptor.revision, "revision"),
        "candidate_id": _identity(descriptor.candidate_id, "candidate id"),
        "candidate_sha256": _sha256(descriptor.candidate_sha256, "candidate hash"),
        "protocol_sha256": _sha256(descriptor.protocol_sha256, "protocol hash"),
    }
    if _sha256(descriptor.evidence_policy_sha256, "evidence policy hash") != reference["sha256"]:
        raise RealWorkflowLedgerError("descriptor evidence policy differs from the authoritative policy")
    _sha256(descriptor.assignment_sha256, "assignment hash")
    _sha256(descriptor.selected_inventory_sha256, "selected inventory hash")
    return record


def _lock_once(stream: Any) -> bool:
    stream.seek(0)
    if os.name == "nt":
        import msvcrt

        try:
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            return True
        except OSError:
            return False
    import fcntl

    try:
        fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        return True
    except BlockingIOError:
        return False


def _unlock(stream: Any) -> None:
    stream.seek(0)
    if os.name == "nt":
        import msvcrt

        msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
    else:
        import fcntl

        fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def _acquire_lock_stream(
    lock_path: Path,
    *,
    timeout_seconds: float,
    failure_message: str,
) -> Any:
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    stream = lock_path.open("a+b")
    try:
        stream.seek(0, os.SEEK_END)
        if stream.tell() == 0:
            stream.write(b"0")
            stream.flush()
            os.fsync(stream.fileno())
        deadline = time.monotonic() + timeout_seconds
        while not _lock_once(stream):
            if time.monotonic() >= deadline:
                raise RealWorkflowLedgerError(failure_message)
            time.sleep(0.01)
        return stream
    except BaseException:
        stream.close()
        raise


@contextmanager
def _exclusive_lock(path: Path, timeout_seconds: float = 10.0) -> Iterator[None]:
    path.parent.mkdir(parents=True, exist_ok=True)
    lock_path = path.with_name(f".{path.name}.lock")
    stream = _acquire_lock_stream(
        lock_path,
        timeout_seconds=timeout_seconds,
        failure_message="real workflow ledger is locked by another process",
    )
    try:
        yield
    finally:
        _unlock(stream)
        stream.close()


def _attempt_lock_path(
    path: Path,
    descriptor: RealWorkflowCandidateDescriptor,
    attempt_id: str,
) -> Path:
    identity = _canonical_bytes({
        "revision": descriptor.revision,
        "candidate_id": descriptor.candidate_id,
        "attempt_id": attempt_id,
    })
    suffix = hashlib.sha256(identity).hexdigest()
    return path.with_name(f".{path.name}.attempt-{suffix}.lock")


def _acquire_attempt_lease(
    path: Path,
    descriptor: RealWorkflowCandidateDescriptor,
    attempt_id: str,
) -> Any:
    return _acquire_lock_stream(
        _attempt_lock_path(path, descriptor, attempt_id),
        timeout_seconds=0,
        failure_message="attempt is already owned by a live process",
    )


def _require_live(attempt: RealWorkflowAttempt) -> None:
    if attempt._closed or attempt._lease_stream.closed:
        raise RealWorkflowLedgerError("attempt lease is closed")


def _integrity(document: dict[str, Any]) -> str:
    body = {key: value for key, value in document.items() if key != "integrity_sha256"}
    return hashlib.sha256(_canonical_bytes(body)).hexdigest()


def _atomic_write(path: Path, document: dict[str, Any]) -> None:
    document["integrity_sha256"] = _integrity(document)
    payload = _canonical_bytes(document)
    temporary = path.with_name(f".{path.name}.{uuid4().hex}.partial")
    try:
        with temporary.open("xb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        if os.name != "nt":
            descriptor = os.open(path.parent, os.O_RDONLY)
            try:
                os.fsync(descriptor)
            finally:
                os.close(descriptor)
    finally:
        temporary.unlink(missing_ok=True)


def _validate_attempt(raw: Any) -> dict[str, Any]:
    if not isinstance(raw, dict) or set(raw) != _ATTEMPT_FIELDS:
        raise RealWorkflowLedgerError("ledger attempt has an invalid shape")
    _identity(raw["attempt_id"], "attempt id")
    if raw["status"] not in _STATUSES:
        raise RealWorkflowLedgerError("ledger attempt has an invalid status")
    if not isinstance(raw["opened_utc"], str) or not raw["opened_utc"]:
        raise RealWorkflowLedgerError("ledger attempt opening time is invalid")
    for key in ("first_read_utc", "completed_utc"):
        if raw[key] is not None and (not isinstance(raw[key], str) or not raw[key]):
            raise RealWorkflowLedgerError(f"ledger attempt {key} is invalid")
    if raw["aggregate_result_sha256"] is not None:
        _sha256(raw["aggregate_result_sha256"], "aggregate result hash")
    exception = raw["exception"]
    if exception is not None:
        if not isinstance(exception, dict) or set(exception) != {"type", "message_sha256"}:
            raise RealWorkflowLedgerError("ledger exception has an invalid shape")
        _identity(exception["type"], "exception type")
        _sha256(exception["message_sha256"], "exception message hash")
    status = raw["status"]
    state_values = (
        raw["first_read_utc"], raw["completed_utc"],
        raw["aggregate_result_sha256"], exception,
    )
    valid_state = {
        "active": state_values == (None, None, None, None),
        "consumed": (
            state_values[0] is not None
            and state_values[1:] == (None, None, None)
        ),
        "void": (
            state_values[0] is None
            and state_values[1] is not None
            and state_values[2] is None
            and state_values[3] is not None
        ),
        "failed_consumed": (
            state_values[0] is not None
            and state_values[1] is not None
            and state_values[2] is None
            and state_values[3] is not None
        ),
        "completed_aggregate": (
            state_values[0] is not None
            and state_values[1] is not None
            and state_values[2] is not None
            and state_values[3] is None
        ),
        "completed_disclosed": (
            state_values[0] is not None
            and state_values[1] is not None
            and state_values[2] is not None
            and state_values[3] is None
        ),
    }[status]
    if not valid_state:
        raise RealWorkflowLedgerError("ledger attempt state is inconsistent")
    return raw


def _load(path: Path) -> dict[str, Any]:
    try:
        document = json.loads(path.read_bytes())
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise RealWorkflowLedgerError("real workflow ledger is missing or corrupt") from error
    if not isinstance(document, dict) or set(document) != _LEDGER_FIELDS:
        raise RealWorkflowLedgerError("real workflow ledger has an invalid shape")
    if document["schema"] != LEDGER_SCHEMA or document["split"] != "real-sealed":
        raise RealWorkflowLedgerError("real workflow ledger has an unsupported identity")
    reference, _ = _policy()
    if document["evidence_policy"] != reference:
        raise RealWorkflowLedgerError("ledger evidence policy differs from the authoritative policy")
    _sha256(document["assignment_sha256"], "assignment hash")
    _sha256(document["selected_inventory_sha256"], "selected inventory hash")
    if type(document["generation"]) is not int or document["generation"] < 0:
        raise RealWorkflowLedgerError("ledger generation is invalid")
    if document["integrity_sha256"] != _integrity(document):
        raise RealWorkflowLedgerError("real workflow ledger integrity check failed")
    if not isinstance(document["candidates"], list):
        raise RealWorkflowLedgerError("ledger candidates must be an array")
    keys: set[tuple[str, str]] = set()
    disclosed_attempts: list[dict[str, Any]] = []
    for candidate in document["candidates"]:
        if not isinstance(candidate, dict) or set(candidate) != _CANDIDATE_FIELDS:
            raise RealWorkflowLedgerError("ledger candidate has an invalid shape")
        for key in ("candidate_sha256", "protocol_sha256"):
            _sha256(candidate[key], key)
        identity = (_identity(candidate["revision"], "revision"), _identity(candidate["candidate_id"], "candidate id"))
        if identity in keys:
            raise RealWorkflowLedgerError("ledger contains a duplicate candidate")
        keys.add(identity)
        if not isinstance(candidate["attempts"], list) or not candidate["attempts"]:
            raise RealWorkflowLedgerError("ledger candidate attempts must be nonempty")
        attempts = [_validate_attempt(item) for item in candidate["attempts"]]
        if len({item["attempt_id"] for item in attempts}) != len(attempts):
            raise RealWorkflowLedgerError("ledger contains duplicate attempt identities")
        if sum(item["status"] == "active" for item in attempts) > 1:
            raise RealWorkflowLedgerError("candidate has multiple active attempts")
        consumed = [item for item in attempts if item["first_read_utc"] is not None]
        if len(consumed) > 1 or candidate["consumed_attempt_id"] != (consumed[0]["attempt_id"] if consumed else None):
            raise RealWorkflowLedgerError("candidate first-read accounting is inconsistent")
        disclosed_attempts.extend(
            item for item in attempts if item["status"] == "completed_disclosed"
        )
    retirement = document["retirement"]
    if retirement is not None:
        if not isinstance(retirement, dict) or set(retirement) != _RETIREMENT_FIELDS:
            raise RealWorkflowLedgerError("ledger retirement has an invalid shape")
        retirement_key = (
            _identity(retirement["revision"], "retirement revision"),
            _identity(retirement["candidate_id"], "retirement candidate id"),
            _identity(retirement["attempt_id"], "retirement attempt id"),
        )
        disclosures = retirement["disclosures"]
        if not isinstance(disclosures, list) or not disclosures or disclosures != sorted(set(disclosures)) or not set(disclosures).issubset(DISCLOSURE_KINDS):
            raise RealWorkflowLedgerError("ledger retirement disclosures are invalid")
        _sha256(retirement["evidence_sha256"], "retirement evidence hash")
        retirement_matches = [
            attempt
            for candidate in document["candidates"]
            for attempt in candidate["attempts"]
            if (
                candidate["revision"], candidate["candidate_id"], attempt["attempt_id"]
            ) == retirement_key
        ]
        if (
            len(retirement_matches) != 1
            or retirement_matches[0]["first_read_utc"] is None
        ):
            raise RealWorkflowLedgerError("ledger retirement is not bound to its disclosure")
    elif disclosed_attempts:
        raise RealWorkflowLedgerError("disclosed attempt did not retire the corpus")
    return document


def _new_ledger(descriptor: RealWorkflowCandidateDescriptor) -> dict[str, Any]:
    reference, _ = _policy()
    return {
        "schema": LEDGER_SCHEMA,
        "evidence_policy": reference,
        "assignment_sha256": descriptor.assignment_sha256,
        "selected_inventory_sha256": descriptor.selected_inventory_sha256,
        "split": "real-sealed",
        "generation": 0,
        "candidates": [],
        "retirement": None,
        "integrity_sha256": "",
    }


def _candidate(document: dict[str, Any], record: dict[str, str]) -> dict[str, Any] | None:
    return next((item for item in document["candidates"] if (item["revision"], item["candidate_id"]) == (record["revision"], record["candidate_id"])), None)


def _write_transition(path: Path, document: dict[str, Any]) -> None:
    document["generation"] += 1
    _atomic_write(path, document)


def open_attempt(
    ledger_path: Path,
    descriptor: RealWorkflowCandidateDescriptor,
    *,
    attempt_id: str,
) -> RealWorkflowAttempt:
    """Open or recover one identity-bound attempt without reading sealed data."""

    path = Path(ledger_path).resolve()
    record = _descriptor_record(descriptor)
    attempt_id = _identity(attempt_id, "attempt id")
    lease = _acquire_attempt_lease(path, descriptor, attempt_id)
    try:
        with _exclusive_lock(path):
            document = _load(path) if path.exists() else _new_ledger(descriptor)
            if (
                document["assignment_sha256"] != descriptor.assignment_sha256
                or document["selected_inventory_sha256"] != descriptor.selected_inventory_sha256
                or document["retirement"] is not None
            ):
                raise RealWorkflowLedgerError("real workflow corpus identity is unavailable or retired")
            candidate = _candidate(document, record)
            if candidate is None:
                candidate = {**record, "attempts": [], "consumed_attempt_id": None}
                document["candidates"].append(candidate)
            elif any(candidate[key] != record[key] for key in record):
                raise RealWorkflowLedgerError("candidate identity differs from its ledger binding")
            prior = next((item for item in candidate["attempts"] if item["attempt_id"] == attempt_id), None)
            if prior is not None:
                raise RealWorkflowLedgerError(
                    "attempt identity already exists; recover it without reopening evaluation"
                )
            if candidate["consumed_attempt_id"] is not None:
                raise RealWorkflowLedgerError("candidate sealed read was already consumed")
            if any(item["status"] == "active" for item in candidate["attempts"]):
                raise RealWorkflowLedgerError(
                    "candidate has an interrupted active attempt that must be voided"
                )
            candidate["attempts"].append({
                "attempt_id": attempt_id,
                "status": "active",
                "opened_utc": _utc_now(),
                "first_read_utc": None,
                "completed_utc": None,
                "aggregate_result_sha256": None,
                "exception": None,
            })
            _write_transition(path, document)
        return RealWorkflowAttempt(path, descriptor, attempt_id, lease)
    except BaseException:
        _unlock(lease)
        lease.close()
        raise


def _bound_state(attempt: RealWorkflowAttempt) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any]]:
    document = _load(attempt.ledger_path)
    record = _descriptor_record(attempt.descriptor)
    if document["assignment_sha256"] != attempt.descriptor.assignment_sha256 or document["selected_inventory_sha256"] != attempt.descriptor.selected_inventory_sha256:
        raise RealWorkflowLedgerError("attempt corpus identity differs from the ledger")
    candidate = _candidate(document, record)
    if candidate is None or any(candidate[key] != record[key] for key in record):
        raise RealWorkflowLedgerError("attempt candidate identity differs from the ledger")
    state = next((item for item in candidate["attempts"] if item["attempt_id"] == attempt.attempt_id), None)
    if state is None:
        raise RealWorkflowLedgerError("attempt identity is not registered")
    return document, candidate, state


def record_first_sealed_read(attempt: RealWorkflowAttempt) -> dict[str, object]:
    """Durably consume the candidate, then return an acknowledgement to the reader."""

    _require_live(attempt)
    with _exclusive_lock(attempt.ledger_path):
        document, candidate, state = _bound_state(attempt)
        if document["retirement"] is not None:
            raise RealWorkflowLedgerError("retired corpus cannot acknowledge another payload read")
        if state["status"] == "consumed" and candidate["consumed_attempt_id"] == attempt.attempt_id:
            return {"status": "consumed", "attempt_id": attempt.attempt_id, "generation": document["generation"]}
        if state["status"] != "active" or candidate["consumed_attempt_id"] is not None or document["retirement"] is not None:
            raise RealWorkflowLedgerError("attempt cannot consume a sealed read")
        _, maximum_revisions = _policy()
        revisions = {item["revision"] for item in document["candidates"] if item["consumed_attempt_id"] is not None}
        if candidate["revision"] not in revisions and len(revisions) >= maximum_revisions:
            raise RealWorkflowLedgerError("shared maximum distinct sealed-set revisions is exhausted")
        state["status"] = "consumed"
        state["first_read_utc"] = _utc_now()
        candidate["consumed_attempt_id"] = attempt.attempt_id
        _write_transition(attempt.ledger_path, document)
        return {"status": "consumed", "attempt_id": attempt.attempt_id, "generation": document["generation"]}


def record_attempt_failure(attempt: RealWorkflowAttempt, exception: BaseException) -> str:
    """Record a retryable pre-read void or a permanently consumed post-read failure."""

    if not isinstance(exception, BaseException):
        raise TypeError("exception must be an exception instance")
    _require_live(attempt)
    result = _record_failure_locked(attempt, exception)
    attempt.close()
    return result


def _record_failure_locked(
    attempt: RealWorkflowAttempt,
    exception: BaseException,
) -> str:
    evidence = {
        "type": type(exception).__name__,
        "message_sha256": hashlib.sha256(str(exception).encode("utf-8")).hexdigest(),
    }
    with _exclusive_lock(attempt.ledger_path):
        document, candidate, state = _bound_state(attempt)
        if state["status"] in {"void", "failed_consumed"}:
            return classify_candidate_failure(
                sealed_read_started=state["first_read_utc"] is not None
            )
        if state["status"] not in {"active", "consumed"}:
            raise RealWorkflowLedgerError("completed attempt cannot record a failure")
        consumed = candidate["consumed_attempt_id"] == attempt.attempt_id
        state["status"] = "failed_consumed" if consumed else "void"
        state["completed_utc"] = _utc_now()
        state["exception"] = evidence
        _write_transition(attempt.ledger_path, document)
        return classify_candidate_failure(sealed_read_started=consumed)


def record_interrupted_attempt_failure(
    ledger_path: Path,
    descriptor: RealWorkflowCandidateDescriptor,
    *,
    attempt_id: str,
    exception: BaseException,
) -> str:
    """Close bookkeeping after a process died, without reopening evaluation."""

    if not isinstance(exception, BaseException):
        raise TypeError("exception must be an exception instance")
    path = Path(ledger_path).resolve()
    _descriptor_record(descriptor)
    attempt_id = _identity(attempt_id, "attempt id")
    lease = _acquire_attempt_lease(path, descriptor, attempt_id)
    attempt = RealWorkflowAttempt(path, descriptor, attempt_id, lease)
    try:
        return _record_failure_locked(attempt, exception)
    finally:
        attempt.close()


def record_disclosure(
    attempt: RealWorkflowAttempt,
    *,
    evidence_sha256: str,
    disclosures: Sequence[str],
) -> None:
    """Immediately retire a consumed corpus when case-level evidence is emitted."""

    _require_live(attempt)
    evidence = _sha256(evidence_sha256, "disclosure evidence hash")
    disclosure_list = list(disclosures)
    if (
        not disclosure_list
        or disclosure_list != sorted(set(disclosure_list))
        or not set(disclosure_list).issubset(DISCLOSURE_KINDS)
    ):
        raise RealWorkflowLedgerError("disclosures must be a nonempty unique sorted supported list")
    with _exclusive_lock(attempt.ledger_path):
        document, candidate, state = _bound_state(attempt)
        if state["status"] != "consumed" or candidate["consumed_attempt_id"] != attempt.attempt_id:
            raise RealWorkflowLedgerError("only a consumed live attempt can record disclosure")
        retirement = {
            "revision": candidate["revision"],
            "candidate_id": candidate["candidate_id"],
            "attempt_id": attempt.attempt_id,
            "disclosures": disclosure_list,
            "evidence_sha256": evidence,
            "retired_utc": _utc_now(),
        }
        if document["retirement"] is not None:
            existing = document["retirement"]
            if (
                existing["revision"], existing["candidate_id"], existing["attempt_id"],
                existing["disclosures"], existing["evidence_sha256"],
            ) != (
                retirement["revision"], retirement["candidate_id"], retirement["attempt_id"],
                retirement["disclosures"], retirement["evidence_sha256"],
            ):
                raise RealWorkflowLedgerError("corpus is already retired by different evidence")
            return
        document["retirement"] = retirement
        _write_transition(attempt.ledger_path, document)


def complete_attempt(
    attempt: RealWorkflowAttempt,
    *,
    aggregate_result_sha256: str,
    disclosures: Sequence[str] = (),
) -> str:
    """Record an aggregate result; any case-level disclosure retires the corpus."""

    result_sha256 = _sha256(aggregate_result_sha256, "aggregate result hash")
    disclosure_list = list(disclosures)
    if disclosure_list != sorted(set(disclosure_list)) or not set(disclosure_list).issubset(DISCLOSURE_KINDS):
        raise RealWorkflowLedgerError("disclosures must be a unique sorted supported list")
    _require_live(attempt)
    with _exclusive_lock(attempt.ledger_path):
        document, candidate, state = _bound_state(attempt)
        if state["status"] != "consumed" or candidate["consumed_attempt_id"] != attempt.attempt_id:
            raise RealWorkflowLedgerError("only a consumed active attempt can complete")
        retirement = document["retirement"]
        if disclosure_list:
            expected_retirement = {
                "revision": candidate["revision"],
                "candidate_id": candidate["candidate_id"],
                "attempt_id": attempt.attempt_id,
                "disclosures": disclosure_list,
                "evidence_sha256": result_sha256,
                "retired_utc": state["completed_utc"],
            }
            if retirement is not None and (
                retirement["revision"], retirement["candidate_id"], retirement["attempt_id"],
                retirement["disclosures"],
            ) != (
                candidate["revision"], candidate["candidate_id"], attempt.attempt_id,
                disclosure_list,
            ):
                raise RealWorkflowLedgerError("completion disclosure differs from prior retirement")
            if retirement is None:
                expected_retirement["retired_utc"] = _utc_now()
                document["retirement"] = expected_retirement
        elif retirement is not None and (
            retirement["revision"], retirement["candidate_id"], retirement["attempt_id"]
        ) != (candidate["revision"], candidate["candidate_id"], attempt.attempt_id):
            raise RealWorkflowLedgerError("another attempt retired the corpus")
        state["status"] = "completed_disclosed" if document["retirement"] is not None else "completed_aggregate"
        state["completed_utc"] = _utc_now()
        state["aggregate_result_sha256"] = result_sha256
        _write_transition(attempt.ledger_path, document)
        status = state["status"]
    attempt.close()
    return status


def load_ledger(ledger_path: Path) -> dict[str, Any]:
    """Load a validated independent copy of the identity-only runtime ledger."""

    with _exclusive_lock(Path(ledger_path).resolve()):
        return json.loads(json.dumps(_load(Path(ledger_path).resolve())))


__all__ = [
    "DISCLOSURE_KINDS",
    "LEDGER_SCHEMA",
    "RealWorkflowAttempt",
    "RealWorkflowCandidateDescriptor",
    "RealWorkflowLedgerError",
    "complete_attempt",
    "load_ledger",
    "open_attempt",
    "record_disclosure",
    "record_attempt_failure",
    "record_first_sealed_read",
    "record_interrupted_attempt_failure",
]
