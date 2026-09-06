# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Repository-scoped, source-bound scientific gate seals."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
from typing import Mapping, Sequence
from uuid import uuid4

from ml.policy.evidence_policy import evidence_policy_reference, split_rule


POLICY_SOURCE_PATHS = (
    Path("ml/policy/evidence-policy.json"),
    Path("ml/policy/acceptance-bars.json"),
)


def canonical_json_bytes(value: object) -> bytes:
    return (json.dumps(value, indent=2, sort_keys=True) + "\n").encode("utf-8")


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


def source_bundle_sha256(repo_root: Path, paths: Sequence[Path]) -> str:
    rows = []
    for path in sorted(paths, key=lambda item: item.as_posix()):
        resolved = path if path.is_absolute() else repo_root / path
        relative = resolved.relative_to(repo_root).as_posix()
        rows.append(f"{relative}={sha256_file(resolved)}\n")
    return sha256_bytes("".join(rows).encode("utf-8"))


def _current_base_commit(repo_root: Path) -> str:
    result = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=repo_root,
        capture_output=True,
        check=False,
        text=True,
    )
    commit = result.stdout.strip()
    if result.returncode != 0 or len(commit) != 40:
        raise RuntimeError("Evidence snapshot requires a repository with a valid HEAD commit")
    return commit


def capture_source_snapshot(
    repo_root: Path,
    *,
    identity: Mapping[str, object],
    paths: Sequence[Path],
    preregistration: Mapping[str, object] | None = None,
    inline_hashes: Mapping[str, str] | None = None,
) -> dict[str, object]:
    """Bind an attempt to exact pre-run bytes without requiring a preparation commit."""

    rows: list[dict[str, str]] = []
    seen: set[str] = set()
    for path in sorted((*paths, *POLICY_SOURCE_PATHS), key=lambda item: item.as_posix()):
        resolved = path if path.is_absolute() else repo_root / path
        try:
            relative = resolved.resolve().relative_to(repo_root.resolve()).as_posix()
        except ValueError as error:
            raise RuntimeError(f"Evidence source is outside the repository: {path}") from error
        if relative in seen:
            continue
        if not resolved.is_file():
            raise RuntimeError(f"Evidence source is missing: {relative}")
        seen.add(relative)
        rows.append({"path": relative, "sha256": sha256_file(resolved)})
    snapshot: dict[str, object] = {
        "schema_version": 1,
        "captured_utc": datetime.now(timezone.utc).isoformat(),
        "base_commit": _current_base_commit(repo_root),
        "identity": dict(identity),
        "sources": rows,
        "inline_hashes": dict(sorted((inline_hashes or {}).items())),
    }
    if preregistration is not None:
        snapshot["preregistered_ledger_entry"] = dict(preregistration)
    return snapshot


def verify_source_snapshot(repo_root: Path, snapshot: Mapping[str, object]) -> None:
    sources = snapshot.get("sources")
    if not isinstance(sources, list) or not sources:
        raise RuntimeError("Evidence source snapshot has no bound sources")
    for row in sources:
        if not isinstance(row, dict) or not isinstance(row.get("path"), str) or not isinstance(row.get("sha256"), str):
            raise RuntimeError("Evidence source snapshot contains an invalid source row")
        path = repo_root / row["path"]
        if not path.is_file() or sha256_file(path) != row["sha256"]:
            raise RuntimeError(f"Evidence source changed after snapshot capture: {row['path']}")


def verify_bound_source_snapshot(
    repo_root: Path,
    snapshot_path: Path,
    expected_sha256: object,
) -> None:
    if not isinstance(expected_sha256, str) or sha256_file(snapshot_path) != expected_sha256:
        raise RuntimeError("Evidence source snapshot changed after capture")
    verify_source_snapshot(
        repo_root,
        json.loads(snapshot_path.read_text(encoding="utf-8")),
    )


def require_committed_sources(repo_root: Path, paths: Sequence[Path]) -> None:
    relative = [str((path if path.is_absolute() else repo_root / path).relative_to(repo_root)) for path in paths]
    for path in relative:
        tracked = subprocess.run(
            ["git", "ls-files", "--error-unmatch", "--", path],
            cwd=repo_root,
            capture_output=True,
            check=False,
        )
        if tracked.returncode != 0:
            raise RuntimeError(f"Evidence source must be committed before use: {path}")
    for args in (("git", "diff", "--quiet", "--", *relative), ("git", "diff", "--cached", "--quiet", "--", *relative)):
        result = subprocess.run(args, cwd=repo_root, check=False)
        if result.returncode != 0:
            raise RuntimeError("Evidence sources or configurations differ from the committed revision")


@dataclass(frozen=True)
class GateSeal:
    key: str
    directory: Path
    opened_path: Path
    binding: dict[str, object]
    repo_root: Path | None = None
    snapshot_path: Path | None = None

    @property
    def consumed_path(self) -> Path:
        return self.directory / "consumed.json"

    def consume_sealed_split(self) -> Path:
        """Consume this gate at the first truth-hidden split read."""

        if self.binding.get("evidence_split", "sealed") != "sealed":
            raise RuntimeError("Only the sealed evidence split consumes gate budget")
        if self.consumed_path.exists():
            raise RuntimeError(f"Gate sealed split was already consumed: {self.key}")
        if (self.directory / "void.json").exists():
            raise RuntimeError(f"Gate was voided before sealed-split read: {self.key}")
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
            "key": self.key,
            "opened_sha256": sha256_file(self.opened_path),
            "binding": self.binding,
        }
        try:
            with self.consumed_path.open("xb") as stream:
                stream.write(canonical_json_bytes(payload))
        except FileExistsError as error:
            raise RuntimeError(f"Gate sealed split was already consumed: {self.key}") from error
        return self.consumed_path


def require_evaluator_identity(
    *,
    expected_task: str,
    expected_revision: str,
    manifest: Mapping[str, object],
    split_config: Mapping[str, object],
    seal_binding: Mapping[str, object] | None = None,
    report: Mapping[str, object] | None = None,
) -> None:
    payloads: list[tuple[str, Mapping[str, object]]] = [
        ("manifest", manifest),
        ("split", split_config),
    ]
    if seal_binding is not None:
        payloads.append(("seal", seal_binding))
    if report is not None:
        payloads.append(("report", report))
    for name, payload in payloads:
        if payload.get("task") != expected_task:
            raise RuntimeError(f"{name} task does not match frozen gate identity: {payload.get('task')}")
        if payload.get("revision") != expected_revision:
            raise RuntimeError(f"{name} revision does not match frozen gate identity: {payload.get('revision')}")


def acquire_gate_seal(
    *,
    repo_root: Path,
    task: str,
    revision: str,
    candidate_hashes: Mapping[str, str],
    dataset_manifest_sha256: str,
    split_config_path: Path,
    evaluator_source_paths: Sequence[Path],
    gate_config: Mapping[str, object],
    evidence_split: str = "sealed",
) -> GateSeal:
    canonical_root = repo_root / "ml" / "markers" / "gate-seals"
    retired_path = canonical_root / "retired-historical-pairs.json"
    if not retired_path.is_file():
        raise RuntimeError(f"Canonical retired-pair policy is missing: {retired_path}")
    source_paths = tuple(evaluator_source_paths) + (
        split_config_path,
        retired_path.relative_to(repo_root),
    )
    split_config = json.loads((repo_root / split_config_path).read_text(encoding="utf-8"))
    split_rule(evidence_split)
    expected_task = split_config.get("task")
    if task != expected_task:
        raise RuntimeError(f"Gate task {task} does not match frozen configuration {expected_task}")
    expected_revision = split_config.get("revision")
    if revision != expected_revision:
        raise RuntimeError(f"Gate revision {revision} does not match frozen configuration {expected_revision}")
    expected_candidate_hash_keys = split_config.get("expected_candidate_hash_keys")
    actual_candidate_hash_keys = list(candidate_hashes.keys())
    if actual_candidate_hash_keys != expected_candidate_hash_keys:
        raise RuntimeError(
            "Candidate hash key schema "
            f"{actual_candidate_hash_keys} does not match frozen configuration {expected_candidate_hash_keys}"
        )
    expected_manifest = split_config.get("expected_dataset_manifest_sha256")
    if expected_manifest != dataset_manifest_sha256:
        raise RuntimeError(
            f"Generated split manifest {dataset_manifest_sha256} does not match frozen configuration {expected_manifest}"
        )
    evaluator_sha256 = source_bundle_sha256(repo_root, evaluator_source_paths)
    expected_evaluator = split_config.get("expected_evaluator_source_bundle_sha256")
    if expected_evaluator != evaluator_sha256:
        raise RuntimeError(
            f"Evaluator source bundle {evaluator_sha256} does not match frozen configuration {expected_evaluator}"
        )
    split_config_sha256 = sha256_file(repo_root / split_config_path)
    gate_config_sha256 = sha256_bytes(canonical_json_bytes(dict(gate_config)))
    expected_gate_config = split_config.get("expected_gate_config_sha256")
    if expected_gate_config != gate_config_sha256:
        raise RuntimeError(
            f"Runtime gate configuration {gate_config_sha256} does not match frozen configuration {expected_gate_config}"
        )
    binding: dict[str, object] = {
        "task": task,
        "revision": revision,
        "candidate_hashes": dict(sorted(candidate_hashes.items())),
        "candidate_hash_key_schema": actual_candidate_hash_keys,
        "dataset_manifest_sha256": dataset_manifest_sha256,
        "split_config_path": split_config_path.as_posix(),
        "split_config_sha256": split_config_sha256,
        "evaluator_source_paths": sorted(path.as_posix() for path in evaluator_source_paths),
        "evaluator_source_bundle_sha256": evaluator_sha256,
        "gate_config_sha256": gate_config_sha256,
        "evidence_split": evidence_split,
        "evidence_policy": evidence_policy_reference(),
        "ledger_mode": "canonical_repository",
        "ledger_root": "ml/markers/gate-seals",
        "committed_source_enforcement": True,
        "retired_policy_sha256": sha256_file(retired_path),
    }
    replay_identity = {
        "task": task,
        "revision": revision,
        "candidate_hashes": dict(sorted(candidate_hashes.items())),
    }
    key = sha256_bytes(canonical_json_bytes(replay_identity))
    retired = json.loads(retired_path.read_text(encoding="utf-8"))
    if any(
        item.get("key") == key
        or (
            item.get("task") == task
            and item.get("revision") == revision
            and item.get("candidate_hashes") == dict(sorted(candidate_hashes.items()))
        )
        for item in retired.get("pairs", [])
    ):
        raise RuntimeError(f"Gate pair is retired historical evidence and cannot be replayed: {key}")
    directory = canonical_root / task / key
    directory.mkdir(parents=True, exist_ok=True)
    prior_result = directory / "result.json"
    prior_opened = directory / "opened.json"
    if (
        evidence_split == "dev"
        and prior_result.exists()
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
        raise RuntimeError(f"Gate candidate/revision pair was already opened: {key}")
    snapshot = capture_source_snapshot(
        repo_root,
        identity={"task": task, "revision": revision, "gate_key": key},
        paths=source_paths,
        inline_hashes={
            "candidate_hashes": sha256_bytes(canonical_json_bytes(dict(sorted(candidate_hashes.items())))),
            "dataset_manifest": dataset_manifest_sha256,
            "gate_config": gate_config_sha256,
        },
    )
    with snapshot_path.open("xb") as stream:
        stream.write(canonical_json_bytes(snapshot))
    binding["source_snapshot_path"] = snapshot_path.relative_to(repo_root).as_posix()
    binding["source_snapshot_sha256"] = sha256_file(snapshot_path)
    binding["source_binding_mode"] = "immutable_pre_run_snapshot"
    binding["base_commit"] = snapshot["base_commit"]
    binding.pop("committed_source_enforcement", None)
    opened = {
        "schema_version": 1,
        "status": "opened",
        "evaluation_count": 1,
        "opened_utc": datetime.now(timezone.utc).isoformat(),
        "key": key,
        "binding": binding,
        "budget_status": "pending_sealed_read",
    }
    try:
        with opened_path.open("xb") as stream:
            stream.write(canonical_json_bytes(opened))
    except FileExistsError as error:
        raise RuntimeError(f"Gate candidate/revision pair was already opened: {key}") from error
    return GateSeal(key, directory, opened_path, binding, repo_root, snapshot_path)


def consume_sealed_split(seal: GateSeal) -> Path:
    """Mark the first read of the truth-hidden sealed split as budget use."""

    return seal.consume_sealed_split()


def void_candidate(seal: GateSeal, exception: BaseException) -> Path:
    """Release a gate whose runner failed before reading the sealed split."""

    if seal.consumed_path.exists():
        raise RuntimeError(f"Cannot void gate after sealed-split read: {seal.key}")
    void_path = seal.directory / "void.json"
    payload = {
        "schema_version": 1,
        "status": "void",
        "sealed_split_read": False,
        "budget_consumed": False,
        "voided_utc": datetime.now(timezone.utc).isoformat(),
        "key": seal.key,
        "exception_type": type(exception).__name__,
        "exception_message": str(exception),
        "binding": seal.binding,
    }
    try:
        with void_path.open("xb") as stream:
            stream.write(canonical_json_bytes(payload))
    except FileExistsError as error:
        raise RuntimeError(f"Gate void record was already recorded: {seal.key}") from error
    if seal.opened_path.exists():
        archive = seal.directory / "void-attempts" / uuid4().hex
        archive.mkdir(parents=True, exist_ok=True)
        shutil.move(str(seal.opened_path), str(archive / "opened.json"))
        if seal.snapshot_path is not None and seal.snapshot_path.exists():
            shutil.move(str(seal.snapshot_path), str(archive / "source-snapshot.json"))
    return void_path


def complete_gate_seal(seal: GateSeal, *, status: str, report_sha256: str) -> Path:
    evidence_split = str(seal.binding.get("evidence_split", "sealed"))
    if evidence_split == "sealed" and not seal.consumed_path.exists():
        raise RuntimeError("Sealed split must be consumed at first read before gate completion")
    if evidence_split == "dev" and seal.consumed_path.exists():
        raise RuntimeError("Dev split must not consume gate budget")
    if seal.repo_root is not None and seal.snapshot_path is not None:
        verify_bound_source_snapshot(
            seal.repo_root,
            seal.snapshot_path,
            seal.binding.get("source_snapshot_sha256"),
        )
    result_path = seal.directory / "result.json"
    result = {
        "schema_version": 1,
        "status": status,
        "evaluation_count": 1,
        "key": seal.key,
        "opened_sha256": sha256_file(seal.opened_path),
        "report_sha256": report_sha256,
        "budget_status": "consumed" if evidence_split == "sealed" else "not_consumed_dev",
    }
    try:
        with result_path.open("xb") as stream:
            stream.write(canonical_json_bytes(result))
    except FileExistsError as error:
        raise RuntimeError(f"Gate result was already recorded: {seal.key}") from error
    return result_path


__all__ = [
    "GateSeal",
    "acquire_gate_seal",
    "canonical_json_bytes",
    "capture_source_snapshot",
    "consume_sealed_split",
    "complete_gate_seal",
    "require_evaluator_identity",
    "require_committed_sources",
    "sha256_bytes",
    "sha256_file",
    "source_bundle_sha256",
    "verify_source_snapshot",
    "verify_bound_source_snapshot",
    "void_candidate",
]
