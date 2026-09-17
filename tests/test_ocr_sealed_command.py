# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Handwritten command-boundary checks. No sealed or private corpus is read."""
from __future__ import annotations

import argparse
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace

from ml.markers.gate_seal import GateSeal, canonical_json_bytes, sha256_file
from ml.markers.training_budget import TrainingAuthorization


SCRIPT = (
    Path(__file__).parents[1]
    / "tools"
    / "GraphReader.SyntheticRuntimeEvidence"
    / "run_ocr_sealed_evaluation.py"
)
SPEC = importlib.util.spec_from_file_location("run_ocr_sealed_evaluation", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
command = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(command)


def _write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(canonical_json_bytes(value))


def _fixture(root: Path) -> argparse.Namespace:
    candidate = root / "artifacts" / "candidate.onnx"
    score = root / "artifacts" / "full-score.json"
    apphost = root / "artifacts" / "runtime" / "GraphReader.SyntheticRuntimeEvidence.exe"
    registry = root / "ml" / "policy" / "registry.json"
    for path, content in (
        (candidate, b"candidate"),
        (score, b"score"),
        (apphost, b"apphost"),
        (registry, b"registry"),
    ):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    project_root = Path(__file__).parents[1]
    source_contents = {
        "ml/policy/evidence-policy.json": (project_root / "ml/policy/evidence-policy.json").read_bytes(),
        "ml/policy/acceptance-bars.json": (project_root / "ml/policy/acceptance-bars.json").read_bytes(),
        "ml/markers/training-budgets/production-repair-v1.json": b"training-ledger",
        "ml/ocr/revision/p1.json": b"candidate-config",
        "ml/ocr/revision/train.py": b"runner",
        "ml/ocr/revision/evaluate.py": b"evaluator",
        "ml/ocr/revision/split.json": b"split",
        "ml/markers/gate-seals/retired-historical-pairs.json": b"retired",
    }
    for relative, content in source_contents.items():
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    def source_rows(paths):
        return [
            {"path": relative, "sha256": sha256_file(root / relative)}
            for relative in sorted(paths)
        ]

    policy = command.evidence_policy_reference()
    training_directory = root / "ml" / "markers" / "training-seals" / "ocr-detection" / "revision" / "P1"
    training_runner_paths = ["ml/ocr/revision/train.py"]
    training_source_paths = {
        *training_runner_paths,
        "ml/ocr/revision/p1.json",
        "ml/markers/training-budgets/production-repair-v1.json",
        "ml/policy/evidence-policy.json",
        "ml/policy/acceptance-bars.json",
    }
    preregistration = {
        "task": "ocr-detection",
        "revision": "revision",
        "status": "candidate_1_preregistered",
        "execution_authorized": True,
        "authorized_candidate_id": "P1",
        "preregistered_candidate_ids": ["P1"],
        "consumed_candidate_ids": [],
        "candidate_config_paths": {"P1": "ml/ocr/revision/p1.json"},
        "candidate_config_sha256": {"P1": sha256_file(root / "ml/ocr/revision/p1.json")},
        "evidence_policy": policy,
    }
    training_snapshot = training_directory / "source-snapshot.json"
    training_snapshot_document = {
        "schema_version": 1,
        "captured_utc": "2026-09-17T00:00:00+00:00",
        "base_commit": "a" * 40,
        "identity": {"task": "ocr-detection", "revision": "revision", "candidate_id": "P1"},
        "sources": source_rows(training_source_paths),
        "inline_hashes": {},
        "preregistered_ledger_entry": preregistration,
    }
    _write_json(training_snapshot, training_snapshot_document)
    training_binding = {
        "task": "ocr-detection",
        "revision": "revision",
        "candidate_id": "P1",
        "candidate_config_path": "ml/ocr/revision/p1.json",
        "candidate_config_sha256": sha256_file(root / "ml/ocr/revision/p1.json"),
        "runner_source_paths": training_runner_paths,
        "runner_source_bundle_sha256": command.gate_authority.source_bundle_sha256(
            root, tuple(Path(path) for path in training_runner_paths)
        ),
        "training_budget_ledger_sha256": sha256_file(
            root / "ml/markers/training-budgets/production-repair-v1.json"
        ),
        "evidence_policy": policy,
        "source_snapshot_path": training_snapshot.relative_to(root).as_posix(),
        "source_snapshot_sha256": sha256_file(training_snapshot),
        "source_binding_mode": "immutable_pre_run_snapshot",
        "base_commit": "a" * 40,
    }
    training_opened = training_directory / "opened.json"
    _write_json(training_opened, {
        "schema_version": 1,
        "status": "opened",
        "budget_status": "pending_sealed_read",
        "opened_utc": "2026-09-17T00:00:01+00:00",
        "binding": training_binding,
    })

    candidate_hashes = {"detector": "5" * 64, "recognizer": "6" * 64}
    gate_replay = {
        "task": "ocr-detection",
        "revision": "revision",
        "candidate_hashes": dict(sorted(candidate_hashes.items())),
    }
    gate_key = command.sha256_bytes(command.canonical_json_bytes(gate_replay))
    gate_directory = root / "ml" / "markers" / "gate-seals" / "ocr-detection" / gate_key
    evaluator_paths = ["ml/ocr/revision/evaluate.py"]
    gate_source_paths = {
        *evaluator_paths,
        "ml/ocr/revision/split.json",
        "ml/markers/gate-seals/retired-historical-pairs.json",
        "ml/policy/evidence-policy.json",
        "ml/policy/acceptance-bars.json",
    }
    dataset_manifest_sha256 = "7" * 64
    gate_config_sha256 = "8" * 64
    gate_snapshot = gate_directory / "source-snapshot.json"
    _write_json(gate_snapshot, {
        "schema_version": 1,
        "captured_utc": "2026-09-17T00:00:02+00:00",
        "base_commit": "a" * 40,
        "identity": {"task": "ocr-detection", "revision": "revision", "gate_key": gate_key},
        "sources": source_rows(gate_source_paths),
        "inline_hashes": {
            "candidate_hashes": command.sha256_bytes(
                command.canonical_json_bytes(dict(sorted(candidate_hashes.items())))
            ),
            "dataset_manifest": dataset_manifest_sha256,
            "gate_config": gate_config_sha256,
        },
    })
    gate_binding = {
        "task": "ocr-detection",
        "revision": "revision",
        "candidate_hashes": dict(sorted(candidate_hashes.items())),
        "candidate_hash_key_schema": ["detector", "recognizer"],
        "dataset_manifest_sha256": dataset_manifest_sha256,
        "split_config_path": "ml/ocr/revision/split.json",
        "split_config_sha256": sha256_file(root / "ml/ocr/revision/split.json"),
        "evaluator_source_paths": evaluator_paths,
        "evaluator_source_bundle_sha256": command.gate_authority.source_bundle_sha256(
            root, tuple(Path(path) for path in evaluator_paths)
        ),
        "gate_config_sha256": gate_config_sha256,
        "evidence_split": "sealed",
        "evidence_policy": policy,
        "ledger_mode": "canonical_repository",
        "ledger_root": "ml/markers/gate-seals",
        "retired_policy_sha256": sha256_file(
            root / "ml/markers/gate-seals/retired-historical-pairs.json"
        ),
        "source_snapshot_path": gate_snapshot.relative_to(root).as_posix(),
        "source_snapshot_sha256": sha256_file(gate_snapshot),
        "source_binding_mode": "immutable_pre_run_snapshot",
        "base_commit": "a" * 40,
    }
    gate_opened = gate_directory / "opened.json"
    _write_json(gate_opened, {
        "schema_version": 1,
        "status": "opened",
        "evaluation_count": 1,
        "opened_utc": "2026-09-17T00:00:03+00:00",
        "budget_status": "pending_sealed_read",
        "key": gate_key,
        "binding": gate_binding,
    })
    return argparse.Namespace(
        repository_root=root,
        registry=registry.relative_to(root),
        expected_registry_sha256="2" * 64,
        set_id="3" * 64,
        candidate=candidate.relative_to(root),
        candidate_sha256=sha256_file(candidate),
        training_opened=training_opened.relative_to(root),
        training_opened_sha256=sha256_file(training_opened),
        gate_opened=gate_opened.relative_to(root),
        gate_opened_sha256=sha256_file(gate_opened),
        full_ocr_score=score.relative_to(root),
        full_ocr_score_sha256=sha256_file(score),
        apphost=apphost.relative_to(root),
        timeout_seconds=27.0,
    )


def test_exact_authenticator_and_existing_metadata_reach_parent(tmp_path: Path) -> None:
    args = _fixture(tmp_path)
    captured: dict[str, object] = {}

    def fake_parent(repository_root, registry_path, **kwargs):
        captured.update(kwargs)
        captured["repository_root"] = repository_root
        captured["registry_path"] = registry_path
        output = tmp_path / "artifacts" / "ocr-sealed-evaluations" / "attempt"
        output.mkdir(parents=True)
        return SimpleNamespace(
            status="fail",
            read_status="not_read",
            admission_id="attempt",
            request_path=output / "request.json",
            request_sha256=None,
            outcome_path=output / "aggregate-outcome.json",
            outcome_sha256="4" * 64,
        )

    payload = command.execute(args, evaluator=fake_parent)

    assert payload["status"] == "fail"
    assert payload["read_status"] == "not_read"
    assert captured["expected_registry_sha256"] == "2" * 64
    assert captured["worker_command_prefix"] == (
        str((tmp_path / args.apphost).resolve()),
    )
    assert type(captured["full_ocr_evidence_authenticator"]) is command.TextExtentFullOcrAuthenticator
    assert isinstance(captured["training_authorization"], TrainingAuthorization)
    assert isinstance(captured["gate_seal"], GateSeal)
    assert captured["training_authorization"].binding["candidate_id"] == "P1"
    assert payload["outcome"]["path"].startswith("artifacts/")


def test_changed_live_sources_preserve_frozen_metadata_for_parent_recovery(tmp_path: Path) -> None:
    args = _fixture(tmp_path)
    training_opened = tmp_path / args.training_opened
    gate_opened = tmp_path / args.gate_opened
    original_training = json.loads(training_opened.read_bytes())["binding"]
    original_gate = json.loads(gate_opened.read_bytes())["binding"]
    (tmp_path / command.training_authority.CANONICAL_LEDGER_PATH).write_bytes(b"closed-ledger")
    (tmp_path / "ml/ocr/revision/evaluate.py").write_bytes(b"later-evaluator")
    # Historical source inventory remains usable after a source is moved too.
    runner = tmp_path / "ml/ocr/revision/train.py"
    runner.rename(runner.with_name("archived-train.py"))
    captured = {}

    def recover(_root, _registry, **kwargs):
        captured.update(kwargs)
        output = tmp_path / "artifacts/recovered"
        return SimpleNamespace(
            status="failed", read_status="confirmed", admission_id="original-attempt",
            request_path=output / "request.json", request_sha256="5" * 64,
            outcome_path=output / "aggregate-outcome.json", outcome_sha256="6" * 64,
        )

    assert command.execute(args, evaluator=recover)["admission_id"] == "original-attempt"
    assert captured["training_authorization"].binding == original_training
    assert captured["gate_seal"].binding == original_gate
    assert sha256_file(training_opened) == args.training_opened_sha256
    assert sha256_file(gate_opened) == args.gate_opened_sha256


def test_tampered_opened_record_is_rejected_before_parent(tmp_path: Path) -> None:
    args = _fixture(tmp_path)
    opened = tmp_path / args.training_opened
    opened.write_bytes(opened.read_bytes() + b" ")
    called = False

    def fake_parent(*_args, **_kwargs):
        nonlocal called
        called = True
        raise AssertionError("parent must not run")

    try:
        command.execute(args, evaluator=fake_parent)
    except command.OcrSealedCommandError as error:
        assert error.code == "OCR_SEALED_COMMAND_IDENTITY_INVALID"
    else:
        raise AssertionError("tampered opened metadata was accepted")
    assert called is False


def test_snapshot_path_escape_is_rejected(tmp_path: Path) -> None:
    args = _fixture(tmp_path)
    opened = tmp_path / args.gate_opened
    document = json.loads(opened.read_text(encoding="utf-8"))
    outside = tmp_path.parent / "outside-snapshot.json"
    _write_json(outside, {"kind": "outside"})
    document["binding"]["source_snapshot_path"] = str(outside)
    document["binding"]["source_snapshot_sha256"] = sha256_file(outside)
    _write_json(opened, document)
    args.gate_opened_sha256 = sha256_file(opened)

    try:
        command.execute(args, evaluator=lambda *_args, **_kwargs: None)
    except command.OcrSealedCommandError as error:
        assert error.code == "OCR_SEALED_COMMAND_METADATA_INVALID"
    else:
        raise AssertionError("escaped snapshot binding was accepted")


def test_self_consistent_extra_opened_field_is_rejected(tmp_path: Path) -> None:
    args = _fixture(tmp_path)
    opened = tmp_path / args.training_opened
    document = json.loads(opened.read_text(encoding="utf-8"))
    document["unrecognized_authority"] = "injected"
    _write_json(opened, document)
    args.training_opened_sha256 = sha256_file(opened)

    try:
        command.execute(args, evaluator=lambda *_args, **_kwargs: None)
    except command.OcrSealedCommandError as error:
        assert error.code == "OCR_SEALED_COMMAND_METADATA_INVALID"
    else:
        raise AssertionError("extra opened-record field was accepted")


def test_self_consistent_noncanonical_opened_bytes_are_rejected(tmp_path: Path) -> None:
    args = _fixture(tmp_path)
    opened = tmp_path / args.training_opened
    document = json.loads(opened.read_text(encoding="utf-8"))
    opened.write_text(json.dumps(document), encoding="utf-8")
    args.training_opened_sha256 = sha256_file(opened)

    try:
        command.execute(args, evaluator=lambda *_args, **_kwargs: None)
    except command.OcrSealedCommandError as error:
        assert error.code == "OCR_SEALED_COMMAND_METADATA_INVALID"
    else:
        raise AssertionError("noncanonical opened-record bytes were accepted")


def test_self_consistent_gate_binding_cannot_replace_gate_key(tmp_path: Path) -> None:
    args = _fixture(tmp_path)
    opened = tmp_path / args.gate_opened
    document = json.loads(opened.read_text(encoding="utf-8"))
    document["binding"]["candidate_hashes"]["detector"] = "9" * 64
    snapshot = opened.parent / "source-snapshot.json"
    snapshot_document = json.loads(snapshot.read_text(encoding="utf-8"))
    snapshot_document["inline_hashes"]["candidate_hashes"] = command.sha256_bytes(
        command.canonical_json_bytes(dict(sorted(document["binding"]["candidate_hashes"].items())))
    )
    _write_json(snapshot, snapshot_document)
    document["binding"]["source_snapshot_sha256"] = sha256_file(snapshot)
    _write_json(opened, document)
    args.gate_opened_sha256 = sha256_file(opened)

    try:
        command.execute(args, evaluator=lambda *_args, **_kwargs: None)
    except command.OcrSealedCommandError as error:
        assert error.code == "OCR_SEALED_COMMAND_IDENTITY_INVALID"
    else:
        raise AssertionError("re-keyed gate binding was accepted under the old gate path")


def test_self_consistent_training_preregistration_tamper_is_rejected(tmp_path: Path) -> None:
    args = _fixture(tmp_path)
    opened = tmp_path / args.training_opened
    document = json.loads(opened.read_text(encoding="utf-8"))
    snapshot = opened.parent / "source-snapshot.json"
    snapshot_document = json.loads(snapshot.read_text(encoding="utf-8"))
    snapshot_document["preregistered_ledger_entry"]["execution_authorized"] = False
    _write_json(snapshot, snapshot_document)
    document["binding"]["source_snapshot_sha256"] = sha256_file(snapshot)
    _write_json(opened, document)
    args.training_opened_sha256 = sha256_file(opened)

    try:
        command.execute(args, evaluator=lambda *_args, **_kwargs: None)
    except command.OcrSealedCommandError as error:
        assert error.code == "OCR_SEALED_COMMAND_METADATA_INVALID"
    else:
        raise AssertionError("tampered preregistration was accepted")


def test_main_sanitizes_unexpected_failure(tmp_path: Path, monkeypatch, capsys) -> None:
    args = _fixture(tmp_path)
    argv = []
    for name, value in vars(args).items():
        option = "--" + name.replace("_", "-")
        if name == "timeout_seconds":
            continue
        argv.extend((option, str(value)))

    def fail(_args):
        raise RuntimeError("private/path/that/must/not/escape")

    monkeypatch.setattr(command, "execute", fail)
    assert command.main(argv) == 1
    payload = json.loads(capsys.readouterr().out)
    assert payload == {"status": "error", "code": "OCR_SEALED_COMMAND_FAILED"}
