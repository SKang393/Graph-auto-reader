# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Run the authenticated aggregate-only OCR sealed-evaluation parent."""
from __future__ import annotations

import argparse
from collections.abc import Callable
import json
from pathlib import Path
import sys
from typing import Any


_TOOL_DIRECTORY = Path(__file__).resolve().parent
_IMPORT_ROOT = _TOOL_DIRECTORY.parents[1]
for _path in (_IMPORT_ROOT, _TOOL_DIRECTORY):
    if str(_path) not in sys.path:
        sys.path.insert(0, str(_path))

from authenticate_text_extent_evidence import TextExtentFullOcrAuthenticator
from authenticate_composed_ocr_evidence import ComposedOcrEvidenceAuthenticator
from ml.markers import gate_seal as gate_authority
from ml.markers import training_budget as training_authority
from ml.markers.gate_seal import (
    GateSeal,
    canonical_json_bytes,
    sha256_bytes,
    sha256_file,
)
from ml.markers.training_budget import TrainingAuthorization
from ml.policy import ocr_sealed_evaluation as parent
from ml.policy.evidence_policy import evidence_policy_reference


_SHA256_CHARACTERS = frozenset("0123456789abcdef")


class OcrSealedCommandError(RuntimeError):
    """A sanitized command-boundary failure."""

    def __init__(self, code: str):
        self.code = code
        super().__init__(code)


def _sha256(value: object) -> str:
    if (
        not isinstance(value, str)
        or len(value) != 64
        or value != value.casefold()
        or any(character not in _SHA256_CHARACTERS for character in value)
    ):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_ARGUMENT_INVALID")
    return value


def _repository_path(
    root: Path,
    value: Path,
    *,
    required_parent: Path | None = None,
    must_exist: bool = True,
) -> Path:
    path = (value if value.is_absolute() else root / value).resolve()
    boundary = root if required_parent is None else (root / required_parent).resolve()
    if not path.is_relative_to(boundary) or (must_exist and not path.is_file()):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_PATH_INVALID")
    return path


def _read_canonical_json(path: Path, expected_sha256: object) -> dict[str, Any]:
    def reject_duplicates(pairs: list[tuple[str, object]]) -> dict[str, object]:
        result: dict[str, object] = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate JSON key")
            result[key] = value
        return result

    try:
        payload = path.read_bytes()
        if sha256_bytes(payload) != _sha256(expected_sha256):
            raise OcrSealedCommandError("OCR_SEALED_COMMAND_IDENTITY_INVALID")
        value = json.loads(payload.decode("utf-8"), object_pairs_hook=reject_duplicates)
    except OcrSealedCommandError:
        raise
    except (OSError, UnicodeDecodeError, json.JSONDecodeError, ValueError) as error:
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID") from error
    if not isinstance(value, dict) or canonical_json_bytes(value) != payload:
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    return value


def _exact_fields(value: dict[str, Any], fields: set[str]) -> None:
    if set(value) != fields:
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")


def _snapshot_sources(root: Path, snapshot: dict[str, Any]) -> dict[str, str]:
    sources = snapshot.get("sources")
    if not isinstance(sources, list) or not sources:
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    rows: list[tuple[str, str]] = []
    for row in sources:
        if not isinstance(row, dict):
            raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
        _exact_fields(row, {"path", "sha256"})
        raw_path, digest = row["path"], row["sha256"]
        if not isinstance(raw_path, str) or not raw_path or Path(raw_path).is_absolute():
            raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
        path = _repository_path(root, Path(raw_path), must_exist=False)
        if path.relative_to(root).as_posix() != raw_path:
            raise OcrSealedCommandError("OCR_SEALED_COMMAND_IDENTITY_INVALID")
        rows.append((raw_path, _sha256(digest)))
    if rows != sorted(rows) or len({path for path, _digest in rows}) != len(rows):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    return dict(rows)


def _snapshot_bundle_sha256(sources: dict[str, str], paths: list[str]) -> str:
    # Match the authority's bundle encoding using frozen rows. Live equality is
    # checked by new admission, not by metadata-only recovery.
    return sha256_bytes("".join(
        f"{path}={sources[path]}\n" for path in sorted(paths)
    ).encode("utf-8"))


def _bound_snapshot(
    root: Path,
    opened_path: Path,
    binding: dict[str, Any],
    *,
    fields: set[str],
) -> tuple[Path, dict[str, Any], dict[str, str]]:
    raw_path = binding.get("source_snapshot_path")
    raw_sha256 = binding.get("source_snapshot_sha256")
    if not isinstance(raw_path, str) or not raw_path or Path(raw_path).is_absolute():
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    snapshot = _repository_path(root, Path(raw_path))
    if snapshot != (opened_path.parent / "source-snapshot.json").resolve():
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    document = _read_canonical_json(snapshot, raw_sha256)
    _exact_fields(document, fields)
    if (
        document.get("schema_version") != 1
        or document.get("base_commit") != binding.get("base_commit")
        or not isinstance(document.get("captured_utc"), str)
        or not document["captured_utc"]
        or not isinstance(document.get("identity"), dict)
        or not isinstance(document.get("inline_hashes"), dict)
    ):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    return snapshot, document, _snapshot_sources(root, document)


def _require_policy_sources(sources: dict[str, str]) -> None:
    for policy_path in gate_authority.POLICY_SOURCE_PATHS:
        if policy_path.as_posix() not in sources:
            raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    policy = evidence_policy_reference()
    if sources.get(str(policy.get("path"))) != policy.get("sha256"):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_IDENTITY_INVALID")


def _load_training_authorization(
    root: Path, opened_value: Path, expected_sha256: str
) -> TrainingAuthorization:
    opened = _repository_path(
        root,
        opened_value,
        required_parent=Path("ml/markers/training-seals"),
    )
    relative = opened.relative_to(root / "ml" / "markers" / "training-seals")
    if len(relative.parts) != 4 or relative.name != "opened.json":
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_PATH_INVALID")
    document = _read_canonical_json(opened, expected_sha256)
    _exact_fields(document, {"schema_version", "status", "budget_status", "opened_utc", "binding"})
    binding = document["binding"]
    if not isinstance(binding, dict):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    _exact_fields(binding, training_authority._CANONICAL_TRAINING_BINDING_FIELDS)
    task, revision, candidate_id, _name = relative.parts
    if (
        document.get("schema_version") != 1
        or document.get("status") != "opened"
        or document.get("budget_status") != "pending_sealed_read"
        or not isinstance(document.get("opened_utc"), str)
        or not document["opened_utc"]
        or binding.get("task") != task
        or binding.get("revision") != revision
        or binding.get("candidate_id") != candidate_id
        or binding.get("source_binding_mode") != "immutable_pre_run_snapshot"
        or binding.get("evidence_policy") != evidence_policy_reference()
    ):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    snapshot, snapshot_document, sources = _bound_snapshot(
        root,
        opened,
        binding,
        fields={
            "schema_version", "captured_utc", "base_commit", "identity",
            "sources", "inline_hashes", "preregistered_ledger_entry",
        },
    )
    identity = snapshot_document["identity"]
    preregistration = snapshot_document["preregistered_ledger_entry"]
    if (
        identity != {"task": task, "revision": revision, "candidate_id": candidate_id}
        or snapshot_document["inline_hashes"] != {}
        or not isinstance(preregistration, dict)
    ):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    ordinal = candidate_id[1:] if candidate_id.startswith("P") else ""
    expected_status = f"candidate_{int(ordinal)}_preregistered" if ordinal.isdigit() else None
    configured_paths = preregistration.get("candidate_config_paths")
    configured_hashes = preregistration.get("candidate_config_sha256")
    preregistered_ids = preregistration.get("preregistered_candidate_ids")
    consumed_ids = preregistration.get("consumed_candidate_ids")
    if (
        preregistration.get("task") != task
        or preregistration.get("revision") != revision
        or preregistration.get("status") != expected_status
        or preregistration.get("execution_authorized") is not True
        or preregistration.get("authorized_candidate_id") != candidate_id
        or preregistered_ids != [candidate_id]
        or not isinstance(consumed_ids, list)
        or candidate_id in consumed_ids
        or preregistration.get("evidence_policy") != binding.get("evidence_policy")
        or not isinstance(configured_paths, dict)
        or configured_paths.get(candidate_id) != binding.get("candidate_config_path")
        or not isinstance(configured_hashes, dict)
        or configured_hashes.get(candidate_id) != binding.get("candidate_config_sha256")
    ):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    runner_paths = binding.get("runner_source_paths")
    config_path = binding.get("candidate_config_path")
    if not isinstance(config_path, str) or not config_path:
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    for field in (
        "candidate_config_sha256", "runner_source_bundle_sha256",
        "training_budget_ledger_sha256", "source_snapshot_sha256",
    ):
        _sha256(binding.get(field))
    expected_sources = (
        set(runner_paths)
        if isinstance(runner_paths, list) and all(isinstance(path, str) for path in runner_paths)
        else set()
    )
    expected_sources.update({
        config_path,
        training_authority.CANONICAL_LEDGER_PATH.as_posix(),
        *(path.as_posix() for path in gate_authority.POLICY_SOURCE_PATHS),
    })
    if (
        not isinstance(runner_paths, list)
        or not runner_paths
        or runner_paths != sorted(runner_paths)
        or len(runner_paths) != len(set(runner_paths))
        or any(not isinstance(path, str) or path not in sources for path in runner_paths)
        or config_path not in sources
        or sources.get(config_path) != binding.get("candidate_config_sha256")
        or sources.get(training_authority.CANONICAL_LEDGER_PATH.as_posix())
        != binding.get("training_budget_ledger_sha256")
        or set(sources) != expected_sources
    ):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    _require_policy_sources(sources)
    runner_bundle = _snapshot_bundle_sha256(sources, runner_paths)
    if runner_bundle != binding.get("runner_source_bundle_sha256"):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_IDENTITY_INVALID")
    return TrainingAuthorization(opened.parent, opened, binding, root, snapshot)


def _load_gate_seal(root: Path, opened_value: Path, expected_sha256: str) -> GateSeal:
    opened = _repository_path(
        root,
        opened_value,
        required_parent=Path("ml/markers/gate-seals"),
    )
    relative = opened.relative_to(root / "ml" / "markers" / "gate-seals")
    if len(relative.parts) != 3 or relative.name != "opened.json":
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_PATH_INVALID")
    document = _read_canonical_json(opened, expected_sha256)
    _exact_fields(
        document,
        {"schema_version", "status", "evaluation_count", "opened_utc", "key", "binding", "budget_status"},
    )
    binding = document["binding"]
    if not isinstance(binding, dict):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    _exact_fields(binding, gate_authority._CANONICAL_GATE_BINDING_FIELDS)
    task, key, _name = relative.parts
    if (
        document.get("schema_version") != 1
        or document.get("status") != "opened"
        or document.get("evaluation_count") != 1
        or document.get("budget_status") != "pending_sealed_read"
        or not isinstance(document.get("opened_utc"), str)
        or not document["opened_utc"]
        or document.get("key") != key
        or _sha256(key) != key
        or binding.get("task") != task
        or binding.get("source_binding_mode") != "immutable_pre_run_snapshot"
        or binding.get("evidence_policy") != evidence_policy_reference()
        or binding.get("ledger_mode") != "canonical_repository"
        or binding.get("ledger_root") != "ml/markers/gate-seals"
        or binding.get("evidence_split") != "sealed"
    ):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    hashes = binding.get("candidate_hashes")
    schema = binding.get("candidate_hash_key_schema")
    if (
        not isinstance(hashes, dict)
        or not hashes
        or not isinstance(schema, list)
        or any(not isinstance(name, str) or not name for name in schema)
        or any(not isinstance(name, str) or not name for name in hashes)
        or len(schema) != len(set(schema))
        or set(schema) != set(hashes)
    ):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    for digest in hashes.values():
        _sha256(digest)
    for field in (
        "dataset_manifest_sha256", "split_config_sha256",
        "evaluator_source_bundle_sha256", "gate_config_sha256",
        "retired_policy_sha256", "source_snapshot_sha256",
    ):
        _sha256(binding.get(field))
    replay_key = sha256_bytes(canonical_json_bytes({
        "task": task,
        "revision": binding.get("revision"),
        "candidate_hashes": dict(sorted(hashes.items())),
    }))
    if replay_key != key:
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_IDENTITY_INVALID")
    snapshot, snapshot_document, sources = _bound_snapshot(
        root,
        opened,
        binding,
        fields={"schema_version", "captured_utc", "base_commit", "identity", "sources", "inline_hashes"},
    )
    if snapshot_document["identity"] != {
        "task": task, "revision": binding.get("revision"), "gate_key": key,
    }:
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    expected_inline = {
        "candidate_hashes": sha256_bytes(canonical_json_bytes(dict(sorted(hashes.items())))),
        "dataset_manifest": binding.get("dataset_manifest_sha256"),
        "gate_config": binding.get("gate_config_sha256"),
    }
    if snapshot_document["inline_hashes"] != dict(sorted(expected_inline.items())):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    evaluator_paths = binding.get("evaluator_source_paths")
    split_path = binding.get("split_config_path")
    retired_path = "ml/markers/gate-seals/retired-historical-pairs.json"
    expected_sources = (
        set(evaluator_paths)
        if isinstance(evaluator_paths, list) and all(isinstance(path, str) for path in evaluator_paths)
        else set()
    )
    expected_sources.update({
        str(split_path), retired_path,
        *(path.as_posix() for path in gate_authority.POLICY_SOURCE_PATHS),
    })
    if (
        not isinstance(evaluator_paths, list)
        or not evaluator_paths
        or evaluator_paths != sorted(evaluator_paths)
        or len(evaluator_paths) != len(set(evaluator_paths))
        or any(not isinstance(path, str) or path not in sources for path in evaluator_paths)
        or not isinstance(split_path, str)
        or sources.get(split_path) != binding.get("split_config_sha256")
        or sources.get(retired_path) != binding.get("retired_policy_sha256")
        or set(sources) != expected_sources
    ):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_METADATA_INVALID")
    _require_policy_sources(sources)
    evaluator_bundle = _snapshot_bundle_sha256(sources, evaluator_paths)
    if evaluator_bundle != binding.get("evaluator_source_bundle_sha256"):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_IDENTITY_INVALID")
    return GateSeal(key, opened.parent, opened, binding, root, snapshot)


def _relative_artifact(root: Path, value: Path) -> str:
    path = Path(value).resolve()
    if not path.is_relative_to((root / "artifacts").resolve()):
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_RESULT_INVALID")
    return path.relative_to(root).as_posix()


def execute(
    args: argparse.Namespace,
    *,
    evaluator: Callable[..., parent.OcrSealedEvaluationResult] = parent.evaluate_ocr_sealed_candidate,
) -> dict[str, object]:
    root = Path(args.repository_root).resolve()
    if not root.is_dir():
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_PATH_INVALID")
    registry = _repository_path(root, Path(args.registry))
    candidate = _repository_path(
        root, Path(args.candidate), required_parent=Path("artifacts")
    )
    candidate_sha256 = _sha256(args.candidate_sha256)
    if sha256_file(candidate) != candidate_sha256:
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_IDENTITY_INVALID")
    score = _repository_path(
        root, Path(args.full_ocr_score), required_parent=Path("artifacts")
    )
    score_sha256 = _sha256(args.full_ocr_score_sha256)
    if sha256_file(score) != score_sha256:
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_IDENTITY_INVALID")
    apphost = _repository_path(root, Path(args.apphost))
    training = _load_training_authorization(
        root, Path(args.training_opened), args.training_opened_sha256
    )
    gate = _load_gate_seal(root, Path(args.gate_opened), args.gate_opened_sha256)
    evidence_kind = getattr(args, "evidence_kind", "original-db")
    development_request = getattr(args, "development_request", None)
    development_request_sha256 = getattr(args, "development_request_sha256", None)
    if evidence_kind == "composed-ocr":
        if development_request is None or development_request_sha256 is None:
            raise OcrSealedCommandError("OCR_SEALED_COMMAND_ARGUMENT_INVALID")
        request = _repository_path(root, Path(development_request), required_parent=Path("artifacts/goal22-runs"))
        request_sha256 = _sha256(development_request_sha256)
        if sha256_file(request) != request_sha256:
            raise OcrSealedCommandError("OCR_SEALED_COMMAND_IDENTITY_INVALID")
        authenticator = ComposedOcrEvidenceAuthenticator(
            score.relative_to(root), score_sha256, apphost.relative_to(root),
            request.relative_to(root), request_sha256)
    elif evidence_kind == "original-db" and development_request is None and development_request_sha256 is None:
        authenticator = TextExtentFullOcrAuthenticator(
            score.relative_to(root), score_sha256, apphost.relative_to(root))
    else:
        raise OcrSealedCommandError("OCR_SEALED_COMMAND_ARGUMENT_INVALID")
    result = evaluator(
        root,
        registry,
        expected_registry_sha256=_sha256(args.expected_registry_sha256),
        set_id=_sha256(args.set_id),
        candidate_path=candidate,
        candidate_sha256=candidate_sha256,
        training_authorization=training,
        gate_seal=gate,
        full_ocr_evidence_authenticator=authenticator,
        worker_command_prefix=(str(apphost),),
        timeout_seconds=args.timeout_seconds,
    )
    return {
        "status": result.status,
        "read_status": result.read_status,
        "admission_id": result.admission_id,
        "request": {
            "path": _relative_artifact(root, result.request_path),
            "sha256": result.request_sha256,
        },
        "outcome": {
            "path": _relative_artifact(root, result.outcome_path),
            "sha256": result.outcome_sha256,
        },
    }


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository-root", type=Path, required=True)
    parser.add_argument("--registry", type=Path, required=True)
    parser.add_argument("--expected-registry-sha256", required=True)
    parser.add_argument("--set-id", required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--candidate-sha256", required=True)
    parser.add_argument("--training-opened", type=Path, required=True)
    parser.add_argument("--training-opened-sha256", required=True)
    parser.add_argument("--gate-opened", type=Path, required=True)
    parser.add_argument("--gate-opened-sha256", required=True)
    parser.add_argument("--full-ocr-score", type=Path, required=True)
    parser.add_argument("--full-ocr-score-sha256", required=True)
    parser.add_argument("--apphost", type=Path, required=True)
    parser.add_argument("--evidence-kind", choices=("original-db", "composed-ocr"), default="original-db")
    parser.add_argument("--development-request", type=Path)
    parser.add_argument("--development-request-sha256")
    parser.add_argument("--timeout-seconds", type=float, default=300.0)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        payload = execute(args)
    except parent.OcrSealedEvaluationError as error:
        payload = {"status": "error", "code": error.code}
        exit_code = 1
    except OcrSealedCommandError as error:
        payload = {"status": "error", "code": error.code}
        exit_code = 1
    except Exception:
        payload = {"status": "error", "code": "OCR_SEALED_COMMAND_FAILED"}
        exit_code = 1
    else:
        exit_code = 0
    print(json.dumps(payload, sort_keys=True, separators=(",", ":")))
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())


__all__ = ["OcrSealedCommandError", "execute", "main"]
