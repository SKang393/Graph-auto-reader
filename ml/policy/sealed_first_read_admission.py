# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Atomic parent-side admission for one synthetic sealed first read.

This module records identities and accounting state only. It never opens a
sealed archive and never launches or retries an evaluator.
"""

from __future__ import annotations

from collections.abc import Callable, Mapping, Sequence
from copy import deepcopy
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
from typing import Any
from uuid import uuid4

from ml.markers.gate_seal import (
    GateSeal,
    canonical_json_bytes,
    ensure_first_read_consumed_projection as ensure_gate_consumed,
    ensure_first_read_void_projection as ensure_gate_void,
    first_read_intent_payload as gate_intent_payload,
    prepare_first_read_intent as prepare_gate_intent,
    sha256_bytes,
    sha256_file,
)
from ml.markers.training_budget import (
    TrainingAuthorization,
    ensure_first_read_consumed_projection as ensure_training_consumed,
    ensure_first_read_void_projection as ensure_training_void,
    first_read_intent_payload as training_intent_payload,
    prepare_first_read_intent as prepare_training_intent,
)
from ml.policy.evidence_policy import evidence_policy_reference
from ml.policy import sealed_reserve


ADMISSION_SCHEMA = sealed_reserve.FIRST_READ_ADMISSION_SCHEMA
ACK_PREFIX = "G22_READ_ACK/1"
_SHA256 = frozenset("0123456789abcdef")
_CAPACITY_HOLD_STATES = frozenset({"ack_intent", "possible_read", "confirmed_read"})
_RECORD_FIELDS = {
    "schema", "admission_id", "status", "read_status", "registry_path",
    "set_id", "revision", "candidate_id", "candidate_sha256",
    "gate_identity_sha256", "read_binding_sha256", "acceptance_scope",
    "coverage_protocol_sha256", "reserve_metadata_sha256", "archive_path",
    "archive_sha256", "archive_manifest_sha256", "case_count",
    "training_opened_sha256",
    "training_binding_sha256", "training_intent_path",
    "training_intent_sha256", "gate_opened_sha256", "gate_binding_sha256",
    "gate_intent_path", "gate_intent_sha256", "evidence_policy", "attempt_id",
    "attempt_binding", "attempt_binding_sha256", "request_nonce",
    "disclosure_binding_sha256", "disclosure_evidence_sha256", "disclosures",
    "disclosure_confirmed_read", "aggregate_result_sha256", "failure", "created_utc",
    "quarantine_binding_sha256", "quarantine_code", "quarantine_channels",
    "quarantine_confirmed_read",
    "updated_utc", "authority_binding_sha256",
}
_ADMISSION_IDENTITY_FIELDS = {
    "registry_path", "set_id", "revision", "candidate_id", "candidate_sha256",
    "gate_identity_sha256", "acceptance_scope", "coverage_protocol_sha256",
    "reserve_metadata_sha256", "archive_path", "archive_sha256",
    "archive_manifest_sha256", "case_count", "training_opened_sha256",
    "training_binding_sha256", "gate_opened_sha256", "gate_binding_sha256",
    "evidence_policy", "attempt_id", "attempt_binding", "attempt_binding_sha256",
}
_AUTHORITY_BINDING_FIELDS = (
    _ADMISSION_IDENTITY_FIELDS
    | {
        "schema", "admission_id", "read_binding_sha256", "training_intent_path",
        "training_intent_sha256", "gate_intent_path", "gate_intent_sha256",
    }
)


class SealedFirstReadAdmissionError(RuntimeError):
    """Raised when admission state or identity cannot be proven."""


def _holds_reserve_set(record: Mapping[str, object]) -> bool:
    return (
        record.get("status") in _CAPACITY_HOLD_STATES
        or record.get("quarantine_binding_sha256") is not None
    )


@dataclass(frozen=True)
class SealedFirstReadAdmission:
    repository_root: Path
    registry_path: Path
    authority_path: Path
    admission_id: str
    training_authorization: TrainingAuthorization
    gate_seal: GateSeal


@dataclass(frozen=True)
class AdmissionReceipt:
    admission_id: str
    status: str
    read_status: str
    registry_sha256: str


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _sha256(value: object, label: str) -> str:
    if (
        not isinstance(value, str)
        or len(value) != 64
        or value != value.casefold()
        or any(character not in _SHA256 for character in value)
    ):
        raise SealedFirstReadAdmissionError(f"{label} must be a lowercase SHA-256 value")
    return value


def _identity(value: object, label: str) -> str:
    if not isinstance(value, str) or not value or value.strip() != value or len(value) > 256:
        raise SealedFirstReadAdmissionError(f"{label} must be an exact nonempty identifier")
    return value


def _relative(root: Path, path: Path, label: str) -> str:
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError as error:
        raise SealedFirstReadAdmissionError(f"{label} is outside the repository") from error


def _atomic_write(path: Path, record: dict[str, Any], *, create: bool = False) -> None:
    payload = canonical_json_bytes(record)
    path.parent.mkdir(parents=True, exist_ok=True)
    if create:
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
        finally:
            temporary.unlink(missing_ok=True)
        return
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


def _normalize_quarantine_channels(
    channels: Sequence[Mapping[str, object]],
) -> list[dict[str, object]]:
    rows: list[dict[str, object]] = []
    for source in channels:
        row = dict(source)
        if (
            set(row) != {"channel", "sha256", "byte_count", "complete"}
            or row["channel"] not in {"stdout", "stderr"}
            or type(row["byte_count"]) is not int
            or row["byte_count"] < 0
            or type(row["complete"]) is not bool
        ):
            raise SealedFirstReadAdmissionError("unclassified-output channel metadata is invalid")
        _sha256(row["sha256"], "unclassified-output channel hash")
        rows.append(row)
    rows.sort(key=lambda item: str(item["channel"]))
    if not rows or len({row["channel"] for row in rows}) != len(rows):
        raise SealedFirstReadAdmissionError("unclassified-output channels must be unique")
    return rows


def _load_record(path: Path) -> dict[str, Any]:
    def reject_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        value: dict[str, Any] = {}
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
        raise SealedFirstReadAdmissionError("first-read admission authority is missing or corrupt") from error
    if not isinstance(value, dict) or set(value) != _RECORD_FIELDS or value.get("schema") != ADMISSION_SCHEMA:
        raise SealedFirstReadAdmissionError("first-read admission authority has an invalid shape")
    if value["status"] not in sealed_reserve.FIRST_READ_ADMISSION_STATUSES:
        raise SealedFirstReadAdmissionError("first-read admission status is invalid")
    for key in (
        "admission_id", "set_id", "candidate_sha256", "gate_identity_sha256",
        "read_binding_sha256", "coverage_protocol_sha256", "reserve_metadata_sha256",
        "archive_sha256", "archive_manifest_sha256", "training_opened_sha256",
        "training_binding_sha256", "training_intent_sha256", "gate_opened_sha256",
        "gate_binding_sha256", "gate_intent_sha256", "attempt_id",
        "attempt_binding_sha256",
    ):
        _sha256(value[key], key)
    if value["admission_id"] != path.stem or value["read_binding_sha256"] != value["admission_id"]:
        raise SealedFirstReadAdmissionError("first-read admission file identity differs")
    admission_identity = {
        "schema": "graphreader.synthetic-sealed-first-read-binding.v2",
        **{key: value[key] for key in _ADMISSION_IDENTITY_FIELDS},
    }
    if sha256_bytes(canonical_json_bytes(admission_identity)) != value["admission_id"]:
        raise SealedFirstReadAdmissionError("first-read admission content identity differs")
    authority_binding = {key: value[key] for key in _AUTHORITY_BINDING_FIELDS}
    if (
        sha256_bytes(canonical_json_bytes(authority_binding))
        != value["authority_binding_sha256"]
    ):
        raise SealedFirstReadAdmissionError("first-read admission authority binding differs")
    expected_read_status = {
        "prepared": "none",
        "ack_intent": "possible",
        "possible_read": "possible",
        "confirmed_read": "confirmed",
        "completed": "confirmed",
        "failed": "confirmed",
        "void": "none",
    }[value["status"]]
    if value["read_status"] != expected_read_status:
        raise SealedFirstReadAdmissionError("first-read admission read status is inconsistent")
    if (
        not isinstance(value["attempt_binding"], dict)
        or sha256_bytes(canonical_json_bytes(value["attempt_binding"]))
        != value["attempt_binding_sha256"]
        or value["attempt_id"] != value["attempt_binding_sha256"]
    ):
        raise SealedFirstReadAdmissionError("first-read attempt binding differs")
    if value["status"] == "prepared" and value["request_nonce"] is not None:
        raise SealedFirstReadAdmissionError("prepared admission contains ACK nonce")
    if value["status"] in {"ack_intent", "possible_read", "confirmed_read", "completed", "failed"}:
        _sha256(value["request_nonce"], "request nonce")
    if value["aggregate_result_sha256"] is not None:
        _sha256(value["aggregate_result_sha256"], "aggregate result hash")
    failure = value["failure"]
    if failure is not None and (
        not isinstance(failure, dict)
        or set(failure) != {"type", "message_sha256"}
        or not isinstance(failure["type"], str)
    ):
        raise SealedFirstReadAdmissionError("first-read admission failure is invalid")
    if failure is not None:
        _sha256(failure["message_sha256"], "failure message hash")
    disclosures = value["disclosures"]
    if (
        type(disclosures) is not list
        or any(type(item) is not str for item in disclosures)
        or disclosures != sorted(set(disclosures))
        or not set(disclosures).issubset(sealed_reserve.DISCLOSURE_KINDS)
    ):
        raise SealedFirstReadAdmissionError("first-read disclosure categories are invalid")
    if not disclosures:
        if any(value[key] is not None for key in (
            "disclosure_binding_sha256", "disclosure_evidence_sha256",
            "disclosure_confirmed_read",
        )):
            raise SealedFirstReadAdmissionError("empty disclosure has bound evidence")
    else:
        _sha256(value["disclosure_binding_sha256"], "disclosure binding hash")
        _sha256(value["disclosure_evidence_sha256"], "disclosure evidence hash")
        if type(value["disclosure_confirmed_read"]) is not bool:
            raise SealedFirstReadAdmissionError("disclosure confirmation state is invalid")
        disclosure_binding = {
            "schema": "graphreader.synthetic-sealed-disclosure-binding.v1",
            "admission_id": value["admission_id"],
            "set_id": value["set_id"],
            "read_binding_sha256": value["read_binding_sha256"],
            "evidence_sha256": value["disclosure_evidence_sha256"],
            "disclosures": disclosures,
            "confirmed_read": value["disclosure_confirmed_read"],
        }
        if (
            sha256_bytes(canonical_json_bytes(disclosure_binding))
            != value["disclosure_binding_sha256"]
        ):
            raise SealedFirstReadAdmissionError("first-read disclosure binding differs")
    channels = value["quarantine_channels"]
    if type(channels) is not list:
        raise SealedFirstReadAdmissionError("unclassified-output channels are invalid")
    if not channels:
        if any(value[key] is not None for key in (
            "quarantine_binding_sha256", "quarantine_code", "quarantine_confirmed_read",
        )):
            raise SealedFirstReadAdmissionError("empty quarantine has bound evidence")
    else:
        if (
            type(value["quarantine_code"]) is not str
            or not value["quarantine_code"].startswith("OCR_SEALED_")
            or type(value["quarantine_confirmed_read"]) is not bool
        ):
            raise SealedFirstReadAdmissionError("unclassified-output quarantine is invalid")
        normalized = _normalize_quarantine_channels(channels)
        if normalized != channels:
            raise SealedFirstReadAdmissionError("unclassified-output channels are not canonical")
        quarantine_binding = {
            "schema": "graphreader.synthetic-sealed-unclassified-output-binding.v1",
            "admission_id": value["admission_id"],
            "set_id": value["set_id"],
            "read_binding_sha256": value["read_binding_sha256"],
            "code": value["quarantine_code"],
            "channels": channels,
            "confirmed_read": value["quarantine_confirmed_read"],
        }
        if sha256_bytes(canonical_json_bytes(quarantine_binding)) != value["quarantine_binding_sha256"]:
            raise SealedFirstReadAdmissionError("unclassified-output quarantine binding differs")
    if (
        not isinstance(value["case_count"], int)
        or isinstance(value["case_count"], bool)
        or not 1 <= value["case_count"] <= 128
    ):
        raise SealedFirstReadAdmissionError("bound reserve case count is invalid")
    if (
        not isinstance(value["archive_path"], str)
        or not value["archive_path"].startswith("artifacts/")
    ):
        raise SealedFirstReadAdmissionError("bound reserve archive path is invalid")
    archive_path = Path(value["archive_path"])
    if archive_path.is_absolute() or archive_path.drive or ".." in archive_path.parts:
        raise SealedFirstReadAdmissionError("bound reserve archive path is invalid")
    if value["evidence_policy"] != evidence_policy_reference():
        raise SealedFirstReadAdmissionError("admission evidence policy differs")
    return value


def _registry_metadata(
    path: Path,
    repository_root: Path,
    expected_sha256: str | None = None,
) -> dict[str, Any]:
    if expected_sha256 is not None and sha256_file(path) != _sha256(expected_sha256, "registry hash"):
        raise SealedFirstReadAdmissionError("sealed reserve registry differs from its expected hash")
    try:
        return sealed_reserve.load_registry_metadata_only(path, repository_root)
    except sealed_reserve.SealedReserveError as error:
        raise SealedFirstReadAdmissionError(str(error)) from error


def _reserve_set(record: dict[str, Any], set_id: str) -> dict[str, Any]:
    match = next((item for item in record["sets"] if isinstance(item, dict) and item.get("set_id") == set_id), None)
    if match is None:
        raise SealedFirstReadAdmissionError("sealed reserve set is not registered")
    return match


def _validate_reserve_identity(
    registry: dict[str, Any], *, set_id: str, scope: str, protocol_sha256: str,
    permit_retired_accounting: bool = False,
) -> dict[str, Any]:
    item = _reserve_set(registry, set_id)
    reserve_scope = item.get("scope")
    protocol = sealed_reserve._acceptance_protocol(scope)
    if (
        not isinstance(reserve_scope, dict)
        or reserve_scope.get("purpose") != sealed_reserve.ACCEPTANCE_PURPOSE
        or reserve_scope.get("acceptance_scope") != scope
        or reserve_scope.get("coverage_protocol_path") != protocol.PROTOCOL_PATH.as_posix()
        or reserve_scope.get("coverage_protocol_sha256") != protocol_sha256
        or (item.get("state") == "retired" and not permit_retired_accounting)
    ):
        raise SealedFirstReadAdmissionError("sealed reserve is incompatible with the required acceptance scope")
    try:
        protocol.require_supported_identity(
            scope, protocol.PROTOCOL_PATH.as_posix(), protocol_sha256
        )
    except Exception as error:
        raise SealedFirstReadAdmissionError(str(error)) from error
    return item


def _binding_record(
    repository_root: Path,
    registry_path: Path,
    set_id: str,
    candidate_sha256: str,
    scope: str,
    protocol_sha256: str,
    authorization: TrainingAuthorization,
    gate: GateSeal,
    selected: dict[str, Any],
    attempt_id: str,
    attempt_binding: Mapping[str, object],
) -> dict[str, Any]:
    training = authorization.binding
    gate_binding = gate.binding
    if (
        authorization.repo_root is None
        or authorization.snapshot_path is None
        or gate.repo_root is None
        or gate.snapshot_path is None
        or authorization.repo_root.resolve() != repository_root.resolve()
        or gate.repo_root.resolve() != repository_root.resolve()
    ):
        raise SealedFirstReadAdmissionError("admission requires source-bound canonical training and gate objects")
    task = _identity(training.get("task"), "training task")
    revision = _identity(training.get("revision"), "revision")
    candidate_id = _identity(training.get("candidate_id"), "candidate id")
    if gate_binding.get("task") != task or gate_binding.get("revision") != revision:
        raise SealedFirstReadAdmissionError("training and gate identities differ")
    hashes = gate_binding.get("candidate_hashes")
    candidate_sha256 = _sha256(candidate_sha256, "candidate hash")
    if not isinstance(hashes, dict) or candidate_sha256 not in hashes.values():
        raise SealedFirstReadAdmissionError("candidate hash is absent from the frozen gate identity")
    if gate_binding.get("evidence_split", "sealed") != "sealed":
        raise SealedFirstReadAdmissionError("first-read admission requires a sealed gate")
    selected_metadata = {
        key: deepcopy(selected.get(key))
        for key in ("set_id", "scope", "generator", "archive", "chain")
    }
    archive = selected.get("archive")
    chain = selected.get("chain")
    if not isinstance(archive, dict) or not isinstance(chain, dict):
        raise SealedFirstReadAdmissionError("selected reserve metadata is incomplete")
    if (
        type(chain.get("case_count")) is not int
        or not 1 <= chain["case_count"] <= 128
    ):
        raise SealedFirstReadAdmissionError("selected reserve case count is outside worker limits")
    if gate_binding.get("dataset_manifest_sha256") != chain.get("archive_manifest_sha256"):
        raise SealedFirstReadAdmissionError(
            "frozen gate dataset identity differs from the selected reserve manifest"
        )
    training_opened = sha256_file(authorization.opened_path)
    gate_opened = sha256_file(gate.opened_path)
    attempt_payload = deepcopy(dict(attempt_binding))
    attempt_binding_sha256 = sha256_bytes(canonical_json_bytes(attempt_payload))
    if _sha256(attempt_id, "attempt id") != attempt_binding_sha256:
        raise SealedFirstReadAdmissionError("attempt id differs from its canonical binding")
    binding_identity = {
        "schema": "graphreader.synthetic-sealed-first-read-binding.v2",
        "registry_path": _relative(repository_root, registry_path, "registry"),
        "set_id": _sha256(set_id, "set id"),
        "revision": revision,
        "candidate_id": candidate_id,
        "candidate_sha256": candidate_sha256,
        "gate_identity_sha256": _sha256(gate.key, "gate identity"),
        "acceptance_scope": _identity(scope, "acceptance scope"),
        "coverage_protocol_sha256": _sha256(protocol_sha256, "coverage protocol hash"),
        "reserve_metadata_sha256": sha256_bytes(canonical_json_bytes(selected_metadata)),
        "archive_path": archive.get("path"),
        "archive_sha256": _sha256(archive.get("sha256"), "archive hash"),
        "archive_manifest_sha256": _sha256(
            chain.get("archive_manifest_sha256"), "archive manifest hash"
        ),
        "case_count": chain.get("case_count"),
        "training_opened_sha256": training_opened,
        "training_binding_sha256": sha256_bytes(canonical_json_bytes(training)),
        "gate_opened_sha256": gate_opened,
        "gate_binding_sha256": sha256_bytes(canonical_json_bytes(gate_binding)),
        "evidence_policy": evidence_policy_reference(),
        "attempt_id": attempt_binding_sha256,
        "attempt_binding": attempt_payload,
        "attempt_binding_sha256": attempt_binding_sha256,
    }
    admission_id = sha256_bytes(canonical_json_bytes(binding_identity))
    identity = {key: value for key, value in binding_identity.items() if key != "schema"}
    identity["admission_id"] = admission_id
    return identity


def _receipt(record: dict[str, Any], registry_file: Path) -> AdmissionReceipt:
    return AdmissionReceipt(
        admission_id=record["admission_id"],
        status=record["status"],
        read_status=record["read_status"],
        registry_sha256=sha256_file(registry_file),
    )


def _selected_metadata(item: dict[str, Any]) -> dict[str, Any]:
    return {
        key: deepcopy(item.get(key))
        for key in ("set_id", "scope", "generator", "archive", "chain")
    }


def _validate_bound_reserve_metadata(
    registry: dict[str, Any], record: dict[str, Any], *, permit_retired_accounting: bool = False,
) -> dict[str, Any]:
    selected = _validate_reserve_identity(
        registry,
        set_id=record["set_id"],
        scope=record["acceptance_scope"],
        protocol_sha256=record["coverage_protocol_sha256"],
        permit_retired_accounting=permit_retired_accounting,
    )
    metadata = _selected_metadata(selected)
    archive = selected.get("archive")
    chain = selected.get("chain")
    if (
        sha256_bytes(canonical_json_bytes(metadata)) != record["reserve_metadata_sha256"]
        or not isinstance(archive, dict)
        or not isinstance(chain, dict)
        or archive.get("path") != record["archive_path"]
        or archive.get("sha256") != record["archive_sha256"]
        or chain.get("archive_manifest_sha256") != record["archive_manifest_sha256"]
        or chain.get("case_count") != record["case_count"]
    ):
        raise SealedFirstReadAdmissionError("selected immutable reserve metadata changed")
    return selected


def _validate_component_intents(
    admission: SealedFirstReadAdmission,
    record: dict[str, Any],
    *,
    verify_sources: bool,
) -> None:
    training_path, training_payload = training_intent_payload(
        admission.training_authorization,
        read_binding_sha256=record["read_binding_sha256"],
        verify_sources=verify_sources,
    )
    gate_path, gate_payload = gate_intent_payload(
        admission.gate_seal,
        read_binding_sha256=record["read_binding_sha256"],
        verify_sources=verify_sources,
    )
    expected = (
        (training_path, training_payload, "training_intent_path", "training_intent_sha256"),
        (gate_path, gate_payload, "gate_intent_path", "gate_intent_sha256"),
    )
    for path, payload, path_key, hash_key in expected:
        if (
            _relative(admission.repository_root, path, "component intent") != record[path_key]
            or sha256_bytes(payload) != record[hash_key]
            or not path.is_file()
            or path.read_bytes() != payload
        ):
            raise SealedFirstReadAdmissionError("first-read component intent differs")
    if (
        sha256_file(admission.training_authorization.opened_path)
        != record["training_opened_sha256"]
        or sha256_bytes(canonical_json_bytes(admission.training_authorization.binding))
        != record["training_binding_sha256"]
        or sha256_file(admission.gate_seal.opened_path) != record["gate_opened_sha256"]
        or sha256_bytes(canonical_json_bytes(admission.gate_seal.binding))
        != record["gate_binding_sha256"]
        or admission.gate_seal.key != record["gate_identity_sha256"]
    ):
        raise SealedFirstReadAdmissionError("source-bound component identity changed")


def _transition(path: Path, record: dict[str, Any], *, status: str, failure: BaseException | None = None) -> dict[str, Any]:
    updated = dict(record)
    updated["status"] = status
    updated["read_status"] = {
        "prepared": "none", "ack_intent": "possible", "possible_read": "possible",
        "confirmed_read": "confirmed", "completed": "confirmed", "failed": "confirmed",
        "void": "none",
    }[status]
    updated["updated_utc"] = _utc_now()
    if failure is not None:
        updated["failure"] = {
            "type": type(failure).__name__,
            "message_sha256": hashlib.sha256(str(failure).encode("utf-8")).hexdigest(),
        }
    _atomic_write(path, updated)
    return _load_record(path)


def _validate_capacity(
    registry_file: Path,
    registry: dict[str, Any],
    record: dict[str, Any],
    *,
    will_hold_selected_set: bool,
) -> None:
    maximum, minimum = sealed_reserve._policy_limits()
    compatible = [
        item for item in registry["sets"]
        if isinstance(item, dict)
        and isinstance(item.get("scope"), dict)
        and item["scope"].get("purpose") == sealed_reserve.ACCEPTANCE_PURPOSE
        and item["scope"].get("acceptance_scope") == record["acceptance_scope"]
        and item["scope"].get("coverage_protocol_sha256") == record["coverage_protocol_sha256"]
    ]
    admissions = sealed_reserve._load_first_read_admission_identities(registry_file)
    conflicts = [
        item for item in admissions
        if item["admission_id"] != record["admission_id"]
        and (
            item["status"] != "void"
            or item.get("quarantine_binding_sha256") is not None
        )
        and (
            (
                item["set_id"] == record["set_id"]
                and _holds_reserve_set(item)
            )
            or
            item["gate_identity_sha256"] == record["gate_identity_sha256"]
            or item["read_binding_sha256"] == record["read_binding_sha256"]
            or (
                item["acceptance_scope"] == record["acceptance_scope"]
                and item["coverage_protocol_sha256"] == record["coverage_protocol_sha256"]
                and item["revision"] == record["revision"]
                and item["candidate_id"] == record["candidate_id"]
            )
        )
    ]
    if conflicts:
        raise SealedFirstReadAdmissionError("candidate, gate, or admission binding is already reserved")
    for item in registry["sets"]:
        if not isinstance(item, dict):
            continue
        item_scope = item.get("scope")
        for use in item.get("uses", []):
            if not isinstance(use, dict):
                continue
            if (
                use.get("gate_identity_sha256") == record["gate_identity_sha256"]
                or use.get("read_binding_sha256") == record["read_binding_sha256"]
                or (
                    isinstance(item_scope, dict)
                    and item_scope.get("acceptance_scope") == record["acceptance_scope"]
                    and item_scope.get("coverage_protocol_sha256") == record["coverage_protocol_sha256"]
                    and use.get("revision") == record["revision"]
                    and use.get("candidate_id") == record["candidate_id"]
                )
            ):
                raise SealedFirstReadAdmissionError("candidate, gate, or admission binding was already used")
    held = {
        item["set_id"] for item in admissions
        if item["admission_id"] != record["admission_id"]
        and _holds_reserve_set(item)
    }
    unused = sum(item.get("state") == "unused" and item.get("set_id") not in held for item in compatible)
    selected = _reserve_set(registry, record["set_id"])
    if unused < minimum or (
        will_hold_selected_set
        and
        selected.get("state") == "unused" and selected.get("set_id") not in held and unused - 1 < minimum
    ):
        raise SealedFirstReadAdmissionError("first-read admission would violate the minimum unused reserve")
    revisions = {use.get("revision") for use in selected.get("uses", []) if isinstance(use, dict)}
    revisions.update(
        item["revision"] for item in admissions
        if item["admission_id"] != record["admission_id"]
        and item["set_id"] == record["set_id"]
        and _holds_reserve_set(item)
    )
    if record["revision"] not in revisions and len(revisions) >= maximum:
        raise SealedFirstReadAdmissionError("sealed reserve reached its revision reuse limit")


def prepare_admission(
    registry_path: Path,
    repository_root: Path,
    *,
    expected_registry_sha256: str,
    set_id: str,
    candidate_sha256: str,
    training_authorization: TrainingAuthorization,
    gate_seal: GateSeal,
    required_acceptance_scope: str,
    required_coverage_protocol_sha256: str,
    attempt_id: str,
    attempt_binding: Mapping[str, object],
) -> SealedFirstReadAdmission:
    """Prepare exact identities without authorizing or claiming a sealed read."""

    root = Path(repository_root).resolve()
    registry_file = sealed_reserve._registry_file(root, registry_path)
    with sealed_reserve._registry_update_lock(registry_file):
        registry = _registry_metadata(registry_file, root, expected_registry_sha256)
        selected = _validate_reserve_identity(
            registry,
            set_id=_sha256(set_id, "set id"),
            scope=_identity(required_acceptance_scope, "acceptance scope"),
            protocol_sha256=_sha256(
                required_coverage_protocol_sha256, "coverage protocol hash"
            ),
        )
        identity = _binding_record(
            root, registry_file, set_id, candidate_sha256,
            required_acceptance_scope, required_coverage_protocol_sha256,
            training_authorization, gate_seal, selected,
            attempt_id, attempt_binding,
        )
        admission_id = identity["admission_id"]
        authority_path = sealed_reserve.first_read_admission_path(
            registry_file, root, admission_id
        )
        training_intent, training_payload = training_intent_payload(
            training_authorization,
            read_binding_sha256=admission_id,
            verify_sources=True,
        )
        gate_intent, gate_payload = gate_intent_payload(
            gate_seal, read_binding_sha256=admission_id, verify_sources=True
        )
        now = _utc_now()
        record = {
            "schema": ADMISSION_SCHEMA,
            **identity,
            "status": "prepared",
            "read_status": "none",
            "read_binding_sha256": admission_id,
            "training_intent_path": _relative(root, training_intent, "training intent"),
            "training_intent_sha256": sha256_bytes(training_payload),
            "gate_intent_path": _relative(root, gate_intent, "gate intent"),
            "gate_intent_sha256": sha256_bytes(gate_payload),
            "request_nonce": None,
            "disclosure_binding_sha256": None,
            "disclosure_evidence_sha256": None,
            "disclosures": [],
            "disclosure_confirmed_read": None,
            "quarantine_binding_sha256": None,
            "quarantine_code": None,
            "quarantine_channels": [],
            "quarantine_confirmed_read": None,
            "aggregate_result_sha256": None,
            "failure": None,
            "created_utc": now,
            "updated_utc": now,
        }
        record["authority_binding_sha256"] = sha256_bytes(canonical_json_bytes({
            key: record[key] for key in _AUTHORITY_BINDING_FIELDS
        }))
        _validate_capacity(
            registry_file, registry, record, will_hold_selected_set=False
        )
        if authority_path.exists():
            existing = _load_record(authority_path)
            if any(existing[key] != record[key] for key in identity):
                raise SealedFirstReadAdmissionError("admission identity already belongs to another binding")
        else:
            _atomic_write(authority_path, record, create=True)
    admission = SealedFirstReadAdmission(
        root, registry_file, authority_path, admission_id,
        training_authorization, gate_seal,
    )
    try:
        prepare_training_intent(
            training_authorization, read_binding_sha256=admission_id
        )
        prepare_gate_intent(gate_seal, read_binding_sha256=admission_id)
        _validate_component_intents(admission, _load_record(authority_path), verify_sources=True)
    except BaseException as error:
        with sealed_reserve._registry_update_lock(registry_file):
            current = _load_record(authority_path)
            if current["status"] == "prepared":
                _transition(authority_path, current, status="void", failure=error)
        try:
            ensure_training_void(
                training_authorization,
                read_binding_sha256=admission_id,
                exception=error,
            )
        finally:
            ensure_gate_void(
                gate_seal, read_binding_sha256=admission_id, exception=error
            )
        raise
    return admission


def send_ack(
    admission: SealedFirstReadAdmission,
    *,
    attempt_id: str,
    request_nonce: str,
    write_and_flush: Callable[[str], None],
) -> AdmissionReceipt:
    """Durably arm one ACK and immediately invoke its sole writer callback."""

    attempt_id = _sha256(attempt_id, "attempt id")
    request_nonce = _sha256(request_nonce, "request nonce")
    if not callable(write_and_flush):
        raise TypeError("write_and_flush must be callable")
    with sealed_reserve._registry_update_lock(admission.registry_path):
        record = _load_record(admission.authority_path)
        if record["status"] != "prepared":
            raise SealedFirstReadAdmissionError("only a prepared admission can send an ACK")
        if record["disclosures"] or record["quarantine_channels"]:
            raise SealedFirstReadAdmissionError(
                "admission with pre-ACK evidence cannot send an ACK"
            )
        if record["attempt_id"] != attempt_id:
            raise SealedFirstReadAdmissionError("ACK attempt differs from admission authority")
        registry = _registry_metadata(admission.registry_path, admission.repository_root)
        _validate_bound_reserve_metadata(registry, record)
        _validate_component_intents(admission, record, verify_sources=True)
        _validate_capacity(
            admission.registry_path, registry, record, will_hold_selected_set=True
        )
        record["request_nonce"] = request_nonce
        record = _transition(admission.authority_path, record, status="ack_intent")
    try:
        write_and_flush(f"{ACK_PREFIX} {request_nonce}\n")
    except BaseException as error:
        with sealed_reserve._registry_update_lock(admission.registry_path):
            current = _load_record(admission.authority_path)
            if current["status"] == "ack_intent":
                _transition(admission.authority_path, current, status="possible_read", failure=error)
        raise
    return _receipt(_load_record(admission.authority_path), admission.registry_path)


def record_positive_read_receipt(
    admission: SealedFirstReadAdmission,
    *,
    attempt_id: str,
    candidate_sha256: str,
    request_nonce: str,
) -> AdmissionReceipt:
    """Confirm first read only from the worker's post-positive-byte receipt."""

    with sealed_reserve._registry_update_lock(admission.registry_path):
        record = _load_record(admission.authority_path)
        if record["status"] not in {"ack_intent", "possible_read", "confirmed_read"}:
            raise SealedFirstReadAdmissionError("admission cannot accept a positive-read receipt")
        if (
            record["attempt_id"] != _sha256(attempt_id, "attempt id")
            or record["candidate_sha256"] != _sha256(candidate_sha256, "candidate hash")
            or record["request_nonce"] != _sha256(request_nonce, "request nonce")
        ):
            raise SealedFirstReadAdmissionError("positive-read receipt identity mismatch")
        registry = _registry_metadata(admission.registry_path, admission.repository_root)
        _validate_bound_reserve_metadata(registry, record)
        _validate_component_intents(admission, record, verify_sources=False)
        if record["status"] != "confirmed_read":
            record = _transition(admission.authority_path, record, status="confirmed_read")
    return materialize_projections(admission)


def _disclosure_binding(
    record: Mapping[str, object], evidence_sha256: str, disclosures: Sequence[str]
) -> dict[str, object]:
    disclosure_list = sorted(set(disclosures))
    if (
        not disclosure_list
        or len(disclosure_list) != len(disclosures)
        or not set(disclosure_list).issubset(sealed_reserve.DISCLOSURE_KINDS)
    ):
        raise SealedFirstReadAdmissionError("case-level disclosure categories are invalid")
    return {
        "schema": "graphreader.synthetic-sealed-disclosure-binding.v1",
        "admission_id": record["admission_id"],
        "set_id": record["set_id"],
        "read_binding_sha256": record["read_binding_sha256"],
        "evidence_sha256": _sha256(evidence_sha256, "disclosure evidence hash"),
        "disclosures": disclosure_list,
        "confirmed_read": record["read_status"] == "confirmed",
    }


def _materialize_disclosure_retirement_locked(
    admission: SealedFirstReadAdmission, record: dict[str, Any]
) -> None:
    if not record["disclosures"]:
        return
    sealed_reserve._record_admission_case_level_disclosure_locked(
        admission.registry_path,
        admission.repository_root,
        expected_registry_sha256=sha256_file(admission.registry_path),
        admission_id=record["admission_id"],
        set_id=record["set_id"],
        revision=record["revision"],
        candidate_id=record["candidate_id"],
        gate_identity_sha256=record["gate_identity_sha256"],
        read_binding_sha256=record["read_binding_sha256"],
        evidence_sha256=record["disclosure_evidence_sha256"],
        disclosures=record["disclosures"],
        confirmed_read=record["disclosure_confirmed_read"],
    )


def record_case_level_disclosure(
    admission: SealedFirstReadAdmission,
    *,
    evidence_sha256: str,
    disclosures: Sequence[str],
) -> AdmissionReceipt:
    """Bind sanitized disclosure evidence before metadata-only reserve retirement."""

    with sealed_reserve._registry_update_lock(admission.registry_path):
        record = _load_record(admission.authority_path)
        if record["quarantine_channels"]:
            raise SealedFirstReadAdmissionError(
                "known disclosure cannot replace unclassified-output quarantine"
            )
        if record["status"] == "void" and not record["disclosures"]:
            raise SealedFirstReadAdmissionError(
                "void admission cannot acquire new case-level disclosure evidence"
            )
        if record["status"] not in {
            "prepared", "ack_intent", "possible_read", "confirmed_read", "failed", "void",
        }:
            raise SealedFirstReadAdmissionError(
                "admission cannot bind case-level disclosure in this state"
            )
        binding = _disclosure_binding(record, evidence_sha256, disclosures)
        binding_sha256 = sha256_bytes(canonical_json_bytes(binding))
        if record["disclosures"]:
            if (
                record["disclosure_binding_sha256"] != binding_sha256
                or record["disclosure_evidence_sha256"] != binding["evidence_sha256"]
                or record["disclosures"] != binding["disclosures"]
                or record["disclosure_confirmed_read"] != binding["confirmed_read"]
            ):
                raise SealedFirstReadAdmissionError(
                    "case-level disclosure differs from admission authority"
                )
        else:
            record["disclosure_binding_sha256"] = binding_sha256
            record["disclosure_evidence_sha256"] = binding["evidence_sha256"]
            record["disclosures"] = binding["disclosures"]
            record["disclosure_confirmed_read"] = binding["confirmed_read"]
            record["updated_utc"] = _utc_now()
            _atomic_write(admission.authority_path, record)
            record = _load_record(admission.authority_path)
        _materialize_disclosure_retirement_locked(admission, record)
    return _receipt(_load_record(admission.authority_path), admission.registry_path)


def record_unclassified_output(
    admission: SealedFirstReadAdmission,
    *,
    code: str,
    channels: Sequence[Mapping[str, object]],
) -> AdmissionReceipt:
    """Bind content-free unknown-output evidence without claiming disclosure."""

    rows = _normalize_quarantine_channels(channels)
    if not isinstance(code, str) or not code.startswith("OCR_SEALED_"):
        raise SealedFirstReadAdmissionError("unclassified-output code is invalid")
    with sealed_reserve._registry_update_lock(admission.registry_path):
        record = _load_record(admission.authority_path)
        if record["status"] == "void" and not record["quarantine_channels"]:
            raise SealedFirstReadAdmissionError(
                "void admission cannot acquire new unclassified-output evidence"
            )
        if record["status"] not in {
            "prepared", "ack_intent", "possible_read", "confirmed_read", "failed", "void",
        }:
            raise SealedFirstReadAdmissionError(
                "admission cannot bind unclassified output in this state"
            )
        if record["disclosures"]:
            raise SealedFirstReadAdmissionError(
                "unclassified output cannot replace known disclosure"
            )
        binding = {
            "schema": "graphreader.synthetic-sealed-unclassified-output-binding.v1",
            "admission_id": record["admission_id"],
            "set_id": record["set_id"],
            "read_binding_sha256": record["read_binding_sha256"],
            "code": code,
            "channels": rows,
            "confirmed_read": record["read_status"] == "confirmed",
        }
        binding_sha256 = sha256_bytes(canonical_json_bytes(binding))
        if record["quarantine_channels"]:
            if (
                record["quarantine_binding_sha256"] != binding_sha256
                or record["quarantine_code"] != code
                or record["quarantine_channels"] != rows
                or record["quarantine_confirmed_read"] != binding["confirmed_read"]
            ):
                raise SealedFirstReadAdmissionError(
                    "unclassified output differs from admission authority"
                )
        else:
            record["quarantine_binding_sha256"] = binding_sha256
            record["quarantine_code"] = code
            record["quarantine_channels"] = rows
            record["quarantine_confirmed_read"] = binding["confirmed_read"]
            record["updated_utc"] = _utc_now()
            _atomic_write(admission.authority_path, record)
            _load_record(admission.authority_path)
    return _receipt(_load_record(admission.authority_path), admission.registry_path)


def _ensure_reserve_projection(admission: SealedFirstReadAdmission, record: dict[str, Any]) -> None:
    with sealed_reserve._registry_update_lock(admission.registry_path):
        registry = _registry_metadata(admission.registry_path, admission.repository_root)
        exact = [
            (item.get("set_id"), use)
            for item in registry["sets"] if isinstance(item, dict)
            for use in item.get("uses", []) if isinstance(use, dict)
            if use.get("read_binding_sha256") == record["read_binding_sha256"]
        ]
        expected = {
            "revision": record["revision"],
            "candidate_id": record["candidate_id"],
            "gate_identity_sha256": record["gate_identity_sha256"],
            "read_binding_sha256": record["read_binding_sha256"],
            "aggregate_only": not record["disclosures"],
            "disclosures": record["disclosures"],
        }
        if exact:
            if len(exact) != 1 or exact[0] != (record["set_id"], expected):
                raise SealedFirstReadAdmissionError("reserve projection differs from confirmed admission")
            return
        if record["disclosures"]:
            raise SealedFirstReadAdmissionError(
                "confirmed disclosure has no exact reserve use"
            )
        holds = frozenset(
            item["set_id"] for item in sealed_reserve._load_first_read_admission_identities(admission.registry_path)
            if item["admission_id"] != record["admission_id"]
            and _holds_reserve_set(item)
        )
        sealed_reserve._record_sealed_read_locked(
            admission.registry_path,
            admission.repository_root,
            expected_registry_sha256=sha256_file(admission.registry_path),
            set_id=record["set_id"],
            revision=record["revision"],
            candidate_id=record["candidate_id"],
            gate_identity_sha256=record["gate_identity_sha256"],
            read_binding_sha256=record["read_binding_sha256"],
            required_acceptance_scope=record["acceptance_scope"],
            required_coverage_protocol_sha256=record["coverage_protocol_sha256"],
            held_set_ids=holds,
            metadata_only=True,
        )


def materialize_projections(admission: SealedFirstReadAdmission) -> AdmissionReceipt:
    """Finish confirmed accounting only; this function never launches evaluation."""

    with sealed_reserve._registry_update_lock(admission.registry_path):
        record = _load_record(admission.authority_path)
        if record["status"] not in {"confirmed_read", "completed", "failed"}:
            raise SealedFirstReadAdmissionError("only a confirmed read can materialize consumed projections")
        registry = _registry_metadata(admission.registry_path, admission.repository_root)
        _validate_bound_reserve_metadata(registry, record, permit_retired_accounting=True)
        _validate_component_intents(admission, record, verify_sources=False)
    ensure_training_consumed(
        admission.training_authorization,
        read_binding_sha256=record["read_binding_sha256"],
    )
    ensure_gate_consumed(
        admission.gate_seal,
        read_binding_sha256=record["read_binding_sha256"],
    )
    _ensure_reserve_projection(admission, record)
    return _receipt(_load_record(admission.authority_path), admission.registry_path)


def void_pre_ack(
    admission: SealedFirstReadAdmission, exception: BaseException
) -> AdmissionReceipt:
    """Void only when the durable authority proves no ACK callback was invoked."""

    with sealed_reserve._registry_update_lock(admission.registry_path):
        record = _load_record(admission.authority_path)
        if record["status"] not in {"prepared", "void"}:
            raise SealedFirstReadAdmissionError("admission cannot be voided after ACK intent")
        if record["status"] == "prepared":
            record = _transition(admission.authority_path, record, status="void", failure=exception)
    ensure_training_void(
        admission.training_authorization,
        read_binding_sha256=record["read_binding_sha256"], exception=exception,
    )
    ensure_gate_void(
        admission.gate_seal,
        read_binding_sha256=record["read_binding_sha256"], exception=exception,
    )
    return _receipt(_load_record(admission.authority_path), admission.registry_path)


def fail_admission(
    admission: SealedFirstReadAdmission, exception: BaseException
) -> AdmissionReceipt:
    """Record possible exposure separately from a receipt-confirmed failure."""

    record = _load_record(admission.authority_path)
    if record["status"] == "confirmed_read":
        materialize_projections(admission)
    with sealed_reserve._registry_update_lock(admission.registry_path):
        record = _load_record(admission.authority_path)
        if record["status"] == "ack_intent":
            record = _transition(admission.authority_path, record, status="possible_read", failure=exception)
        elif record["status"] == "confirmed_read":
            record = _transition(admission.authority_path, record, status="failed", failure=exception)
        elif record["status"] not in {"possible_read", "failed"}:
            raise SealedFirstReadAdmissionError("admission cannot record this failure")
    return _receipt(_load_record(admission.authority_path), admission.registry_path)


def complete_admission(
    admission: SealedFirstReadAdmission, *, aggregate_result_sha256: str
) -> AdmissionReceipt:
    """Complete only a receipt-confirmed aggregate-only evaluation."""

    result_sha256 = _sha256(aggregate_result_sha256, "aggregate result hash")
    materialize_projections(admission)
    with sealed_reserve._registry_update_lock(admission.registry_path):
        record = _load_record(admission.authority_path)
        if record["status"] != "confirmed_read":
            raise SealedFirstReadAdmissionError("only a confirmed read can complete")
        if record["disclosures"] or record["quarantine_channels"]:
            raise SealedFirstReadAdmissionError(
                "non-aggregate evidence cannot complete as aggregate-only evidence"
            )
        record["aggregate_result_sha256"] = result_sha256
        record = _transition(admission.authority_path, record, status="completed")
    return _receipt(record, admission.registry_path)


def recover_admission(
    registry_path: Path,
    repository_root: Path,
    *,
    admission_id: str,
    training_authorization: TrainingAuthorization,
    gate_seal: GateSeal,
) -> SealedFirstReadAdmission:
    """Recover accounting only; an uncertain admission can never relaunch work."""

    root = Path(repository_root).resolve()
    registry_file = sealed_reserve._registry_file(root, registry_path)
    authority_path = sealed_reserve.first_read_admission_path(
        registry_file, root, admission_id
    )
    admission = SealedFirstReadAdmission(
        root, registry_file, authority_path, _sha256(admission_id, "admission id"),
        training_authorization, gate_seal,
    )
    recovery_error = RuntimeError(
        "prepared admission was interrupted before ACK and was voided during recovery"
    )
    with sealed_reserve._registry_update_lock(registry_file):
        record = _load_record(authority_path)
        registry = _registry_metadata(registry_file, root)
        _validate_bound_reserve_metadata(registry, record, permit_retired_accounting=True)
        _materialize_disclosure_retirement_locked(admission, record)
        if record["status"] == "prepared":
            record = _transition(
                authority_path, record, status="void", failure=recovery_error
            )
        elif record["status"] != "void":
            _validate_component_intents(admission, record, verify_sources=False)
        if record["status"] == "ack_intent":
            record = _transition(
                authority_path, record, status="possible_read",
                failure=RuntimeError("ACK delivery is uncertain after coordinator recovery"),
            )
    if record["status"] == "void":
        ensure_training_void(
            training_authorization,
            read_binding_sha256=record["read_binding_sha256"],
            exception=recovery_error,
        )
        ensure_gate_void(
            gate_seal,
            read_binding_sha256=record["read_binding_sha256"],
            exception=recovery_error,
        )
    elif record["read_status"] == "confirmed":
        materialize_projections(admission)
    return admission


def bound_reserve_metadata(admission: SealedFirstReadAdmission) -> dict[str, Any]:
    """Return the authenticated immutable metadata used to build a worker request."""

    with sealed_reserve._registry_update_lock(admission.registry_path):
        record = _load_record(admission.authority_path)
        if record["status"] != "prepared":
            raise SealedFirstReadAdmissionError(
                "worker request metadata is available only before ACK intent"
            )
        registry = _registry_metadata(admission.registry_path, admission.repository_root)
        selected = _validate_bound_reserve_metadata(registry, record)
        return _selected_metadata(selected)


__all__ = [
    "ACK_PREFIX",
    "ADMISSION_SCHEMA",
    "AdmissionReceipt",
    "SealedFirstReadAdmission",
    "SealedFirstReadAdmissionError",
    "complete_admission",
    "bound_reserve_metadata",
    "fail_admission",
    "materialize_projections",
    "prepare_admission",
    "record_case_level_disclosure",
    "record_unclassified_output",
    "record_positive_read_receipt",
    "recover_admission",
    "send_ack",
    "void_pre_ack",
]
