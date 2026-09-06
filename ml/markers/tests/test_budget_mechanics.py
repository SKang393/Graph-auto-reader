# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused tests for first sealed-read budget consumption and void retries."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from ml.markers.gate_seal import (
    acquire_gate_seal,
    canonical_json_bytes,
    complete_gate_seal,
    consume_sealed_split as consume_gate_split,
    sha256_bytes,
    sha256_file,
    source_bundle_sha256,
    void_candidate as void_gate_candidate,
)
from ml.markers.training_budget import (
    CANONICAL_LEDGER_PATH,
    acquire_training_candidate,
    complete_training_candidate,
    consume_sealed_split as consume_training_split,
    void_candidate as void_training_candidate,
)


def _training_fixture(root: Path) -> tuple[Path, Path, Path]:
    config_path = Path("candidate.json")
    runner_path = Path("runner.py")
    ledger_path = root / CANONICAL_LEDGER_PATH
    for policy_path in (
        Path("ml/policy/evidence-policy.json"),
        Path("ml/policy/acceptance-bars.json"),
    ):
        target = root / policy_path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text('{"schema_version": 1}\n', encoding="utf-8")
    runner = root / runner_path
    config = root / config_path
    runner.write_text("# fixed runner\n", encoding="utf-8")
    runner_hash = source_bundle_sha256(root, (runner_path,))
    config.parent.mkdir(parents=True, exist_ok=True)
    config.write_bytes(
        canonical_json_bytes(
            {
                "task": "marker-center",
                "revision": "test-v1",
                "candidate_id": "P1",
                "expected_runner_source_bundle_sha256": runner_hash,
            }
        )
    )
    ledger_path.parent.mkdir(parents=True, exist_ok=True)
    ledger_path.write_bytes(
        canonical_json_bytes(
            {
                "revisions": [
                    {
                        "task": "marker-center",
                        "revision": "test-v1",
                        "status": "candidate_1_preregistered",
                        "execution_authorized": True,
                        "authorized_candidate_id": "P1",
                        "preregistered_candidate_ids": ["P1"],
                        "consumed_candidate_ids": [],
                        "candidate_config_paths": {"P1": config_path.as_posix()},
                        "candidate_config_sha256": {"P1": sha256_file(config)},
                    }
                ]
            }
        )
    )
    return config_path, runner_path, config


def _gate_fixture(root: Path) -> tuple[Path, Path, dict[str, object]]:
    source_path = Path("evaluator.py")
    split_path = Path("split.json")
    retired_path = root / "ml/markers/gate-seals/retired-historical-pairs.json"
    for policy_path in (
        Path("ml/policy/evidence-policy.json"),
        Path("ml/policy/acceptance-bars.json"),
    ):
        target = root / policy_path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text('{"schema_version": 1}\n', encoding="utf-8")
    (root / source_path).write_text("value = 1\n", encoding="utf-8")
    retired_path.parent.mkdir(parents=True, exist_ok=True)
    retired_path.write_text('{"schema_version": 1, "pairs": []}\n', encoding="utf-8")
    gate_config = {"threshold": 0.5}
    split = {
        "task": "marker-center",
        "revision": "test-v1",
        "expected_candidate_hash_keys": ["onnx_sha256"],
        "expected_dataset_manifest_sha256": "a" * 64,
        "expected_evaluator_source_bundle_sha256": source_bundle_sha256(root, (source_path,)),
    }
    split["expected_gate_config_sha256"] = sha256_bytes(canonical_json_bytes(gate_config))
    (root / split_path).write_bytes(canonical_json_bytes(split))
    return source_path, split_path, gate_config


def test_injected_type_error_voids_training_candidate_without_consuming(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    config_path, runner_path, _ = _training_fixture(tmp_path)
    monkeypatch.setattr("ml.markers.gate_seal._current_base_commit", lambda *_a, **_k: "a" * 40)
    authorization = acquire_training_candidate(
        tmp_path,
        task="marker-center",
        revision="test-v1",
        candidate_id="P1",
        config_path=config_path,
        runner_source_paths=(runner_path,),
    )
    try:
        raise TypeError("injected runner failure")
    except TypeError as error:
        void_path = void_training_candidate(authorization, error)

    payload = json.loads(void_path.read_text(encoding="utf-8"))
    assert payload["status"] == "void"
    assert payload["exception_type"] == "TypeError"
    assert payload["exception_message"] == "injected runner failure"
    assert not authorization.consumed_path.exists()
    assert not authorization.opened_path.exists()

    retry = acquire_training_candidate(
        tmp_path,
        task="marker-center",
        revision="test-v1",
        candidate_id="P1",
        config_path=config_path,
        runner_source_paths=(runner_path,),
    )
    assert retry.opened_path.exists()


def test_training_candidate_consumes_only_when_explicitly_reading_sealed_split(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    config_path, runner_path, _ = _training_fixture(tmp_path)
    monkeypatch.setattr("ml.markers.gate_seal._current_base_commit", lambda *_a, **_k: "a" * 40)
    authorization = acquire_training_candidate(
        tmp_path,
        task="marker-center",
        revision="test-v1",
        candidate_id="P1",
        config_path=config_path,
        runner_source_paths=(runner_path,),
    )
    assert not authorization.consumed_path.exists()
    consume_training_split(authorization)
    assert json.loads(authorization.consumed_path.read_text(encoding="utf-8"))["status"] == "consumed"
    with pytest.raises(RuntimeError, match="after sealed-split read"):
        void_training_candidate(authorization, TypeError("too late"))


def test_training_snapshot_binds_preregistration_and_rejects_changed_source(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    config_path, runner_path, _ = _training_fixture(tmp_path)
    monkeypatch.setattr("ml.markers.gate_seal._current_base_commit", lambda *_a, **_k: "a" * 40)

    authorization = acquire_training_candidate(
        tmp_path,
        task="marker-center",
        revision="test-v1",
        candidate_id="P1",
        config_path=config_path,
        runner_source_paths=(runner_path,),
    )

    snapshot = json.loads(authorization.snapshot_path.read_text(encoding="utf-8"))
    assert snapshot["base_commit"] == "a" * 40
    assert snapshot["preregistered_ledger_entry"]["authorized_candidate_id"] == "P1"
    assert {row["path"] for row in snapshot["sources"]} == {
        "candidate.json",
        "runner.py",
        "ml/markers/training-budgets/production-repair-v1.json",
        "ml/policy/acceptance-bars.json",
        "ml/policy/evidence-policy.json",
    }
    assert authorization.binding["source_binding_mode"] == "immutable_pre_run_snapshot"

    (tmp_path / runner_path).write_text("# changed runner\n", encoding="utf-8")
    with pytest.raises(RuntimeError, match="changed after snapshot capture: runner.py"):
        consume_training_split(authorization)
    assert not authorization.consumed_path.exists()


def test_completed_dev_attempt_does_not_consume_or_block_retry(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    config_path, runner_path, _ = _training_fixture(tmp_path)
    monkeypatch.setattr("ml.markers.gate_seal._current_base_commit", lambda *_a, **_k: "a" * 40)
    authorization = acquire_training_candidate(
        tmp_path,
        task="marker-center",
        revision="test-v1",
        candidate_id="P1",
        config_path=config_path,
        runner_source_paths=(runner_path,),
    )
    complete_training_candidate(authorization, status="dev_pass", report_sha256="c" * 64)

    assert not authorization.consumed_path.exists()
    retry = acquire_training_candidate(
        tmp_path,
        task="marker-center",
        revision="test-v1",
        candidate_id="P1",
        config_path=config_path,
        runner_source_paths=(runner_path,),
    )
    assert retry.opened_path.exists()
    assert len(list((authorization.directory / "dev-attempts").glob("*/result.json"))) == 1


def test_injected_type_error_voids_gate_without_consuming(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    source_path, split_path, gate_config = _gate_fixture(tmp_path)
    monkeypatch.setattr("ml.markers.gate_seal._current_base_commit", lambda *_a, **_k: "a" * 40)
    seal = acquire_gate_seal(
        repo_root=tmp_path,
        task="marker-center",
        revision="test-v1",
        candidate_hashes={"onnx_sha256": "b" * 64},
        dataset_manifest_sha256="a" * 64,
        split_config_path=split_path,
        evaluator_source_paths=(source_path,),
        gate_config=gate_config,
    )
    try:
        raise TypeError("injected gate runner failure")
    except TypeError as error:
        void_path = void_gate_candidate(seal, error)

    payload = json.loads(void_path.read_text(encoding="utf-8"))
    assert payload["status"] == "void"
    assert payload["exception_type"] == "TypeError"
    assert not seal.consumed_path.exists()
    assert not seal.opened_path.exists()

    retry = acquire_gate_seal(
        repo_root=tmp_path,
        task="marker-center",
        revision="test-v1",
        candidate_hashes={"onnx_sha256": "b" * 64},
        dataset_manifest_sha256="a" * 64,
        split_config_path=split_path,
        evaluator_source_paths=(source_path,),
        gate_config=gate_config,
    )
    with pytest.raises(RuntimeError, match="at first read"):
        complete_gate_seal(retry, status="pass", report_sha256="d" * 64)
    consume_gate_split(retry)
    complete_gate_seal(retry, status="pass", report_sha256="d" * 64)
    assert retry.consumed_path.exists()


def test_completed_dev_gate_does_not_consume_or_block_retry(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    source_path, split_path, gate_config = _gate_fixture(tmp_path)
    monkeypatch.setattr("ml.markers.gate_seal._current_base_commit", lambda *_a, **_k: "a" * 40)
    seal = acquire_gate_seal(
        repo_root=tmp_path,
        task="marker-center",
        revision="test-v1",
        candidate_hashes={"onnx_sha256": "b" * 64},
        dataset_manifest_sha256="a" * 64,
        split_config_path=split_path,
        evaluator_source_paths=(source_path,),
        gate_config=gate_config,
        evidence_split="dev",
    )
    complete_gate_seal(seal, status="pass", report_sha256="d" * 64)

    assert not seal.consumed_path.exists()
    retry = acquire_gate_seal(
        repo_root=tmp_path,
        task="marker-center",
        revision="test-v1",
        candidate_hashes={"onnx_sha256": "b" * 64},
        dataset_manifest_sha256="a" * 64,
        split_config_path=split_path,
        evaluator_source_paths=(source_path,),
        gate_config=gate_config,
        evidence_split="dev",
    )
    assert retry.opened_path.exists()
    assert len(list((seal.directory / "dev-attempts").glob("*/result.json"))) == 1


def test_gate_snapshot_rejects_split_change_before_sealed_read(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    source_path, split_path, gate_config = _gate_fixture(tmp_path)
    monkeypatch.setattr("ml.markers.gate_seal._current_base_commit", lambda *_a, **_k: "b" * 40)
    seal = acquire_gate_seal(
        repo_root=tmp_path,
        task="marker-center",
        revision="test-v1",
        candidate_hashes={"onnx_sha256": "b" * 64},
        dataset_manifest_sha256="a" * 64,
        split_config_path=split_path,
        evaluator_source_paths=(source_path,),
        gate_config=gate_config,
    )

    assert seal.binding["source_binding_mode"] == "immutable_pre_run_snapshot"
    (tmp_path / split_path).write_text("{}\n", encoding="utf-8")
    with pytest.raises(RuntimeError, match="changed after snapshot capture: split.json"):
        consume_gate_split(seal)
    assert not seal.consumed_path.exists()
