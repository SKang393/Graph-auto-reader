# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Canonical fail-closed training-budget enforcement for marker repairs."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import json
from pathlib import Path
import shutil
from typing import Sequence
from uuid import uuid4

from ml.markers.gate_seal import (
    FIRST_READ_INTENT_NAME,
    canonical_json_bytes,
    capture_source_snapshot,
    sha256_file,
    sha256_bytes,
    source_bundle_sha256,
    verify_bound_source_snapshot,
)
from ml.policy.evidence_policy import evidence_policy_reference


CANONICAL_LEDGER_PATH = Path("ml/markers/training-budgets/production-repair-v1.json")
_CANONICAL_TRAINING_BINDING_FIELDS = {
    "task", "revision", "candidate_id", "candidate_config_path",
    "candidate_config_sha256", "runner_source_paths",
    "runner_source_bundle_sha256", "training_budget_ledger_sha256",
    "evidence_policy", "source_snapshot_path", "source_snapshot_sha256",
    "source_binding_mode", "base_commit",
}
_FIRST_READ_VOID_FIELDS = {
    "schema_version", "status", "sealed_split_read", "budget_consumed",
    "voided_utc", "exception_type", "exception_message_sha256", "binding",
    "read_binding_sha256",
}


def _read_json(path: Path, label: str) -> dict[str, object]:
    def reject_duplicates(pairs: list[tuple[str, object]]) -> dict[str, object]:
        value: dict[str, object] = {}
        for key, item in pairs:
            if key in value:
                raise ValueError("duplicate JSON key")
            value[key] = item
        return value

    try:
        value = json.loads(
            path.read_text(encoding="utf-8"), object_pairs_hook=reject_duplicates
        )
    except (OSError, UnicodeDecodeError, json.JSONDecodeError, ValueError) as error:
        raise RuntimeError(f"{label} is missing or corrupt") from error
    if not isinstance(value, dict):
        raise RuntimeError(f"{label} must be a JSON object")
    return value


@dataclass(frozen=True)
class TrainingAuthorization:
    directory: Path
    opened_path: Path
    binding: dict[str, object]
    repo_root: Path | None = None
    snapshot_path: Path | None = None

    @property
    def consumed_path(self) -> Path:
        return self.directory / "consumed.json"

    def consume_sealed_split(self) -> Path:
        """Consume this candidate at the first sealed-split read.

        Acquisition only reserves a candidate.  This marker is the durable
        budget boundary and is intentionally created separately from
        ``opened.json`` so pre-sealed failures remain void.
        """

        if (self.directory / FIRST_READ_INTENT_NAME).exists():
            raise RuntimeError("Training candidate has a pending coordinated first-read admission")
        if self.consumed_path.exists():
            raise RuntimeError("Training candidate sealed split was already consumed")
        if (self.directory / "void.json").exists():
            raise RuntimeError("Training candidate was voided before sealed-split read")
        if self.repo_root is not None and self.snapshot_path is not None:
            verify_bound_source_snapshot(
                self.repo_root,
                self.snapshot_path,
                self.binding.get("source_snapshot_sha256"),
            )
        payload = {
            "schema_version": 1,
            "status": "consumed",
            "sealed_split_read": True,
            "budget_consumed": True,
            "consumed_utc": datetime.now(timezone.utc).isoformat(),
            "opened_sha256": sha256_file(self.opened_path),
            "binding": self.binding,
        }
        try:
            with self.consumed_path.open("x", encoding="utf-8") as stream:
                stream.write(canonical_json_bytes(payload).decode("utf-8"))
        except FileExistsError as error:
            raise RuntimeError("Training candidate sealed split was already consumed") from error
        return self.consumed_path


def require_training_budget(repo_root: Path, *, task: str, revision: str) -> None:
    """Refuse exhausted or unregistered revisions before any training output is created."""

    ledger_path = repo_root / CANONICAL_LEDGER_PATH
    if not ledger_path.is_file():
        raise RuntimeError(f"Canonical marker training-budget ledger is missing: {ledger_path}")
    ledger = json.loads(ledger_path.read_text(encoding="utf-8"))
    match = next(
        (
            entry
            for entry in ledger.get("revisions", [])
            if entry.get("task") == task and entry.get("revision") == revision
        ),
        None,
    )
    if match is None:
        raise RuntimeError(f"Marker training revision is not preregistered: {task}/{revision}")
    if match.get("status") != "available":
        consumed = ", ".join(str(item) for item in match.get("consumed_candidate_ids", []))
        raise RuntimeError(
            f"Marker training budget is {match.get('status')}: {task}/{revision}; consumed candidates: {consumed}"
        )
    raise RuntimeError(
        f"Marker training revision is recorded as available but no authorized runner is bound: {task}/{revision}"
    )


def acquire_training_candidate(
    repo_root: Path,
    *,
    task: str,
    revision: str,
    candidate_id: str,
    config_path: Path,
    runner_source_paths: Sequence[Path],
) -> TrainingAuthorization:
    ledger_path = repo_root / CANONICAL_LEDGER_PATH
    evidence_paths = (CANONICAL_LEDGER_PATH, config_path, *runner_source_paths)
    ledger = json.loads(ledger_path.read_text(encoding="utf-8"))
    entry = next(
        (
            item
            for item in ledger.get("revisions", [])
            if item.get("task") == task and item.get("revision") == revision
        ),
        None,
    )
    if (
        entry is None
        or entry.get("execution_authorized") is not True
        or entry.get("authorized_candidate_id") != candidate_id
    ):
        raise RuntimeError(f"Training candidate is not authorized by the canonical ledger: {task}/{revision}/{candidate_id}")
    candidate_ordinal = candidate_id[1:] if candidate_id.startswith("P") else ""
    expected_status = f"candidate_{int(candidate_ordinal)}_preregistered" if candidate_ordinal.isdigit() else None
    if expected_status is None or entry.get("status") != expected_status:
        raise RuntimeError(
            f"Training candidate is not in its exact preregistered status: "
            f"{task}/{revision}/{candidate_id}; expected {expected_status}, found {entry.get('status')}"
        )
    if entry.get("preregistered_candidate_ids") != [candidate_id] or candidate_id in entry.get("consumed_candidate_ids", []):
        raise RuntimeError(f"Training candidate budget is not an unused single-candidate authorization: {candidate_id}")
    config_file = repo_root / config_path
    config_sha256 = sha256_file(config_file)
    configured_paths = entry.get("candidate_config_paths")
    configured_hashes = entry.get("candidate_config_sha256")
    expected_path = configured_paths.get(candidate_id) if isinstance(configured_paths, dict) else entry.get("candidate_config_path")
    expected_hash = configured_hashes.get(candidate_id) if isinstance(configured_hashes, dict) else configured_hashes
    if expected_path != config_path.as_posix() or expected_hash != config_sha256:
        raise RuntimeError("Training candidate configuration does not match the canonical budget ledger")
    config = json.loads(config_file.read_text(encoding="utf-8"))
    if (config.get("task"), config.get("revision"), config.get("candidate_id")) != (task, revision, candidate_id):
        raise RuntimeError("Training candidate identity does not match the preregistered configuration")
    runner_sha256 = source_bundle_sha256(repo_root, runner_source_paths)
    if config.get("expected_runner_source_bundle_sha256") != runner_sha256:
        raise RuntimeError("Training runner source bundle does not match the preregistered configuration")
    directory = repo_root / "ml" / "markers" / "training-seals" / task / revision / candidate_id
    directory.mkdir(parents=True, exist_ok=True)
    if (directory / FIRST_READ_INTENT_NAME).exists():
        raise RuntimeError("Training candidate has a pending coordinated first-read admission")
    prior_result = directory / "result.json"
    prior_opened = directory / "opened.json"
    if (
        prior_result.exists()
        and prior_opened.exists()
        and not (directory / "consumed.json").exists()
    ):
        archive = directory / "dev-attempts" / uuid4().hex
        archive.mkdir(parents=True, exist_ok=True)
        shutil.move(str(prior_opened), str(archive / "opened.json"))
        shutil.move(str(prior_result), str(archive / "result.json"))
        prior_snapshot = directory / "source-snapshot.json"
        if prior_snapshot.exists():
            shutil.move(str(prior_snapshot), str(archive / "source-snapshot.json"))
    prior_void = directory / "void.json"
    if prior_void.exists() and not (directory / "opened.json").exists() and not (directory / "consumed.json").exists():
        archive = directory / "void-attempts" / uuid4().hex
        archive.mkdir(parents=True, exist_ok=True)
        shutil.move(str(prior_void), str(archive / "void.json"))
    opened_path = directory / "opened.json"
    snapshot_path = directory / "source-snapshot.json"
    if opened_path.exists() or snapshot_path.exists():
        raise RuntimeError(f"Training candidate was already opened: {task}/{revision}/{candidate_id}")
    snapshot = capture_source_snapshot(
        repo_root,
        identity={"task": task, "revision": revision, "candidate_id": candidate_id},
        paths=evidence_paths,
        preregistration=entry,
    )
    with snapshot_path.open("xb") as stream:
        stream.write(canonical_json_bytes(snapshot))
    binding: dict[str, object] = {
        "task": task,
        "revision": revision,
        "candidate_id": candidate_id,
        "candidate_config_path": config_path.as_posix(),
        "candidate_config_sha256": config_sha256,
        "runner_source_paths": sorted(path.as_posix() for path in runner_source_paths),
        "runner_source_bundle_sha256": runner_sha256,
        "training_budget_ledger_sha256": sha256_file(ledger_path),
        "evidence_policy": evidence_policy_reference(),
        "source_snapshot_path": snapshot_path.relative_to(repo_root).as_posix(),
        "source_snapshot_sha256": sha256_file(snapshot_path),
        "source_binding_mode": "immutable_pre_run_snapshot",
        "base_commit": snapshot["base_commit"],
    }
    opened = {
        "schema_version": 1,
        "status": "opened",
        "budget_status": "pending_sealed_read",
        "opened_utc": datetime.now(timezone.utc).isoformat(),
        "binding": binding,
    }
    try:
        with opened_path.open("xb") as stream:
            stream.write(canonical_json_bytes(opened))
    except FileExistsError as error:
        raise RuntimeError(f"Training candidate was already opened: {task}/{revision}/{candidate_id}") from error
    return TrainingAuthorization(directory, opened_path, binding, repo_root, snapshot_path)


def consume_sealed_split(authorization: TrainingAuthorization) -> Path:
    """Mark the first read of the truth-hidden sealed split as budget use."""

    return authorization.consume_sealed_split()


def void_candidate(
    authorization: TrainingAuthorization,
    exception: BaseException,
) -> Path:
    """Release an unconsumed candidate and retain the pre-sealed exception.

    A void run is retryable under the same authorization.  The opened seal is
    moved into a retained attempt folder rather than deleted.
    """

    if (authorization.directory / FIRST_READ_INTENT_NAME).exists():
        raise RuntimeError("Training candidate has a pending coordinated first-read admission")
    if authorization.consumed_path.exists():
        raise RuntimeError("Cannot void a training candidate after sealed-split read")
    void_path = authorization.directory / "void.json"
    payload = {
        "schema_version": 1,
        "status": "void",
        "sealed_split_read": False,
        "budget_consumed": False,
        "voided_utc": datetime.now(timezone.utc).isoformat(),
        "exception_type": type(exception).__name__,
        "exception_message": str(exception),
        "binding": authorization.binding,
    }
    try:
        with void_path.open("x", encoding="utf-8") as stream:
            stream.write(canonical_json_bytes(payload).decode("utf-8"))
    except FileExistsError as error:
        raise RuntimeError("Training candidate void record was already recorded") from error
    if authorization.opened_path.exists():
        archive = authorization.directory / "void-attempts" / uuid4().hex
        archive.mkdir(parents=True, exist_ok=True)
        shutil.move(str(authorization.opened_path), str(archive / "opened.json"))
        if authorization.snapshot_path is not None and authorization.snapshot_path.exists():
            shutil.move(str(authorization.snapshot_path), str(archive / "source-snapshot.json"))
    return void_path


def prepare_first_read_intent(
    authorization: TrainingAuthorization, *, read_binding_sha256: str
) -> Path:
    """Bind this source-authenticated candidate to one coordinated admission."""

    path, encoded = first_read_intent_payload(
        authorization, read_binding_sha256=read_binding_sha256,
        verify_sources=True,
    )
    if path.exists():
        if path.read_bytes() != encoded:
            raise RuntimeError("Training first-read intent belongs to another admission")
        return path
    from ml.markers.gate_seal import _write_new_durable
    _write_new_durable(path, encoded)
    return path


def first_read_intent_payload(
    authorization: TrainingAuthorization,
    *,
    read_binding_sha256: str,
    verify_sources: bool,
) -> tuple[Path, bytes]:
    """Return the exact training intent after validating its canonical binding."""

    if authorization.repo_root is None or authorization.snapshot_path is None:
        raise RuntimeError("Coordinated admission requires a source-bound canonical training candidate")
    if len(read_binding_sha256) != 64 or any(character not in "0123456789abcdef" for character in read_binding_sha256):
        raise RuntimeError("First-read binding must be a lowercase SHA-256 value")
    if set(authorization.binding) != _CANONICAL_TRAINING_BINDING_FIELDS:
        raise RuntimeError("Coordinated admission requires a canonical training binding")
    if (
        authorization.binding.get("source_binding_mode") != "immutable_pre_run_snapshot"
        or authorization.binding.get("evidence_policy") != evidence_policy_reference()
    ):
        raise RuntimeError("Coordinated admission has a noncanonical training policy binding")
    expected_snapshot = authorization.snapshot_path.resolve().relative_to(
        authorization.repo_root.resolve()
    ).as_posix()
    if authorization.binding.get("source_snapshot_path") != expected_snapshot:
        raise RuntimeError("Training source snapshot path differs from its binding")
    if (
        not authorization.snapshot_path.is_file()
        or sha256_file(authorization.snapshot_path)
        != authorization.binding.get("source_snapshot_sha256")
    ):
        raise RuntimeError("Training source snapshot differs from its binding")
    for key in (
        "candidate_config_sha256", "runner_source_bundle_sha256",
        "training_budget_ledger_sha256", "source_snapshot_sha256",
    ):
        value = authorization.binding.get(key)
        if not isinstance(value, str) or len(value) != 64 or any(character not in "0123456789abcdef" for character in value):
            raise RuntimeError(f"Training {key} is not a lowercase SHA-256 value")
    if verify_sources:
        verify_bound_source_snapshot(
            authorization.repo_root,
            authorization.snapshot_path,
            authorization.binding.get("source_snapshot_sha256"),
        )
    opened = _read_json(authorization.opened_path, "Training opened record")
    if not isinstance(opened, dict) or opened.get("status") != "opened" or opened.get("binding") != authorization.binding:
        raise RuntimeError("Training opened record differs from the source-bound authorization")
    path = authorization.directory / FIRST_READ_INTENT_NAME
    if (authorization.consumed_path.exists() or (authorization.directory / "void.json").exists()) and not path.exists():
        raise RuntimeError("Training candidate is unavailable for coordinated first-read admission")
    payload = {
        "schema": "graphreader.synthetic-sealed-first-read-training-intent.v1",
        "role": "training",
        "read_binding_sha256": read_binding_sha256,
        "opened_sha256": sha256_file(authorization.opened_path),
        "binding_sha256": sha256_bytes(canonical_json_bytes(authorization.binding)),
    }
    return path, canonical_json_bytes(payload)


def ensure_first_read_consumed_projection(
    authorization: TrainingAuthorization, *, read_binding_sha256: str
) -> Path:
    """Idempotently project a coordinator-confirmed positive sealed read."""

    intent_path, expected_intent = first_read_intent_payload(
        authorization, read_binding_sha256=read_binding_sha256,
        verify_sources=False,
    )
    if not intent_path.is_file() or intent_path.read_bytes() != expected_intent:
        raise RuntimeError("Training first-read intent differs from the admission")
    payload = {
        "schema_version": 2,
        "status": "consumed",
        "sealed_split_read": True,
        "budget_consumed": True,
        "consumed_utc": None,
        "opened_sha256": sha256_file(authorization.opened_path),
        "binding": authorization.binding,
        "read_binding_sha256": read_binding_sha256,
        "first_read_intent_sha256": sha256_file(intent_path),
    }
    path = authorization.consumed_path
    if path.exists():
        existing = _read_json(path, "Training consumed projection")
        comparable = dict(existing)
        comparable["consumed_utc"] = None
        if comparable != payload:
            raise RuntimeError("Training consumed projection differs from the admission")
        return path
    payload["consumed_utc"] = datetime.now(timezone.utc).isoformat()
    from ml.markers.gate_seal import _write_new_durable
    _write_new_durable(path, canonical_json_bytes(payload))
    return path


def ensure_first_read_void_projection(
    authorization: TrainingAuthorization,
    *,
    read_binding_sha256: str,
    exception: BaseException,
) -> Path:
    """Idempotently void a coordinated candidate before ACK invocation."""

    if authorization.repo_root is None or authorization.snapshot_path is None:
        raise RuntimeError("Coordinated admission requires a source-bound canonical training candidate")
    intent_path = authorization.directory / FIRST_READ_INTENT_NAME
    void_path = authorization.directory / "void.json"
    archive = authorization.directory / "void-attempts" / read_binding_sha256
    if void_path.exists():
        existing = _read_json(void_path, "Training void projection")
        if (
            set(existing) != _FIRST_READ_VOID_FIELDS
            or existing.get("schema_version") != 2
            or existing.get("status") != "void"
            or existing.get("sealed_split_read") is not False
            or existing.get("budget_consumed") is not False
            or existing.get("binding") != authorization.binding
            or existing.get("read_binding_sha256") != read_binding_sha256
            or not isinstance(existing.get("voided_utc"), str)
            or not isinstance(existing.get("exception_type"), str)
            or not isinstance(existing.get("exception_message_sha256"), str)
            or len(existing["exception_message_sha256"]) != 64
            or any(character not in "0123456789abcdef" for character in existing["exception_message_sha256"])
        ):
            raise RuntimeError("Training void projection differs from the admission")
        opened_source = (
            authorization.opened_path
            if authorization.opened_path.exists()
            else archive / "opened.json"
        )
        snapshot_source = (
            authorization.snapshot_path
            if authorization.snapshot_path.exists()
            else archive / "source-snapshot.json"
        )
        opened = _read_json(opened_source, "Training archived opened record")
        if opened.get("status") != "opened" or opened.get("binding") != authorization.binding:
            raise RuntimeError("Training archived opened record differs from the admission")
        if (
            not snapshot_source.is_file()
            or sha256_file(snapshot_source)
            != authorization.binding.get("source_snapshot_sha256")
        ):
            raise RuntimeError("Training archived source snapshot differs from the admission")
        expected_intent = canonical_json_bytes({
            "schema": "graphreader.synthetic-sealed-first-read-training-intent.v1",
            "role": "training",
            "read_binding_sha256": read_binding_sha256,
            "opened_sha256": sha256_file(opened_source),
            "binding_sha256": sha256_bytes(canonical_json_bytes(authorization.binding)),
        })
        if intent_path.exists() and intent_path.read_bytes() != expected_intent:
            raise RuntimeError("Training first-read intent belongs to another admission")
        archive.mkdir(parents=True, exist_ok=True)
        for source, name in (
            (authorization.opened_path, "opened.json"),
            (authorization.snapshot_path, "source-snapshot.json"),
        ):
            target = archive / name
            if source.exists() and target.exists():
                if source.read_bytes() != target.read_bytes():
                    raise RuntimeError("Training void archive contains conflicting source evidence")
                source.unlink()
            elif source.exists():
                shutil.move(str(source), str(target))
        intent_path.unlink(missing_ok=True)
        return void_path
    expected_path, expected_payload = first_read_intent_payload(
        authorization,
        read_binding_sha256=read_binding_sha256,
        verify_sources=False,
    )
    if expected_path != intent_path or (
        intent_path.exists() and intent_path.read_bytes() != expected_payload
    ):
        raise RuntimeError("Training first-read intent belongs to another admission")
    if authorization.consumed_path.exists():
        raise RuntimeError("Cannot void a training candidate after a confirmed sealed read")
    payload = {
        "schema_version": 2,
        "status": "void",
        "sealed_split_read": False,
        "budget_consumed": False,
        "voided_utc": None,
        "exception_type": type(exception).__name__,
        "exception_message_sha256": sha256_bytes(str(exception).encode("utf-8")),
        "binding": authorization.binding,
        "read_binding_sha256": read_binding_sha256,
    }
    if void_path.exists():
        existing = _read_json(void_path, "Training void projection")
        comparable = dict(existing)
        comparable["voided_utc"] = None
        if comparable != payload:
            raise RuntimeError("Training void projection differs from the admission")
    else:
        payload["voided_utc"] = datetime.now(timezone.utc).isoformat()
        from ml.markers.gate_seal import _write_new_durable
        _write_new_durable(void_path, canonical_json_bytes(payload))
    archive.mkdir(parents=True, exist_ok=True)
    for source, name in ((authorization.opened_path, "opened.json"), (authorization.snapshot_path, "source-snapshot.json")):
        target = archive / name
        if source.exists() and not target.exists():
            shutil.move(str(source), str(target))
    intent_path.unlink(missing_ok=True)
    return void_path


def complete_training_candidate(
    authorization: TrainingAuthorization,
    *,
    status: str,
    report_sha256: str,
) -> Path:
    if (
        (authorization.directory / FIRST_READ_INTENT_NAME).exists()
        and not authorization.consumed_path.exists()
    ):
        raise RuntimeError("Training candidate has an unresolved coordinated first-read admission")
    if authorization.repo_root is not None and authorization.snapshot_path is not None:
        verify_bound_source_snapshot(
            authorization.repo_root,
            authorization.snapshot_path,
            authorization.binding.get("source_snapshot_sha256"),
        )
    result_path = authorization.directory / "result.json"
    result = {
        "schema_version": 1,
        "status": status,
        "opened_sha256": sha256_file(authorization.opened_path),
        "report_sha256": report_sha256,
        "budget_status": "consumed" if authorization.consumed_path.exists() else "pending_sealed_read",
    }
    try:
        with result_path.open("xb") as stream:
            stream.write(canonical_json_bytes(result))
    except FileExistsError as error:
        raise RuntimeError("Training candidate result was already recorded") from error
    return result_path


__all__ = [
    "CANONICAL_LEDGER_PATH",
    "TrainingAuthorization",
    "acquire_training_candidate",
    "consume_sealed_split",
    "complete_training_candidate",
    "prepare_first_read_intent",
    "first_read_intent_payload",
    "ensure_first_read_consumed_projection",
    "ensure_first_read_void_projection",
    "require_training_budget",
    "void_candidate",
]
