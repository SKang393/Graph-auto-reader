# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from copy import deepcopy
import json
from pathlib import Path

import pytest

from ml.markers.gate_seal import (
    FIRST_READ_INTENT_NAME,
    GateSeal,
    acquire_gate_seal,
    canonical_json_bytes,
    consume_sealed_split as consume_gate,
    sha256_bytes,
    sha256_file,
    source_bundle_sha256,
)
from ml.markers.training_budget import (
    CANONICAL_LEDGER_PATH,
    TrainingAuthorization,
    acquire_training_candidate,
    consume_sealed_split as consume_training,
)
from ml.policy.evidence_policy import evidence_policy_reference
from ml.policy import sealed_first_read_admission as admission_module
from ml.policy import sealed_reserve
from ml.policy.sealed_first_read_admission import (
    SealedFirstReadAdmissionError,
    bound_reserve_metadata,
    complete_admission,
    fail_admission,
    prepare_admission,
    record_positive_read_receipt,
    recover_admission,
    send_ack,
    void_pre_ack,
)
from ml.synthetic import ocr_sealed_acceptance


CANDIDATE_SHA256 = "a" * 64
ATTEMPT_ID = "attempt-1"
NONCE = "b" * 64
MANIFEST_SHA256 = "d" * 64
REGISTRY = Path("artifacts/test-sealed-reserve/registry.json")


def _write(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(canonical_json_bytes(value))


def _source_bound_objects(
    root: Path,
    *,
    suffix: str = "1",
    dataset_manifest_sha256: str = MANIFEST_SHA256,
) -> tuple[TrainingAuthorization, GateSeal]:
    source = root / f"source-{suffix}.py"
    source.write_text(f"VALUE = {suffix!r}\n", encoding="utf-8")
    source_relative = source.relative_to(root).as_posix()

    training_dir = (
        root / "ml" / "markers" / "training-seals" / "ocr-test"
        / f"r{suffix}" / f"P{suffix}"
    )
    training_snapshot = training_dir / "source-snapshot.json"
    training_snapshot.parent.mkdir(parents=True, exist_ok=True)
    _write(training_snapshot, {
        "sources": [{"path": source_relative, "sha256": sha256_file(source)}]
    })
    training_binding = {
        "task": "ocr-test",
        "revision": f"r{suffix}",
        "candidate_id": f"P{suffix}",
        "candidate_config_path": f"ml/ocr/candidate-{suffix}.json",
        "candidate_config_sha256": "1" * 64,
        "runner_source_paths": [source_relative],
        "runner_source_bundle_sha256": "2" * 64,
        "training_budget_ledger_sha256": "3" * 64,
        "evidence_policy": evidence_policy_reference(),
        "source_snapshot_path": training_snapshot.relative_to(root).as_posix(),
        "source_snapshot_sha256": sha256_file(training_snapshot),
        "source_binding_mode": "immutable_pre_run_snapshot",
        "base_commit": "4" * 40,
    }
    training_opened = training_dir / "opened.json"
    _write(training_opened, {
        "schema_version": 1, "status": "opened", "binding": training_binding
    })
    authorization = TrainingAuthorization(
        training_dir, training_opened, training_binding, root, training_snapshot
    )

    gate_dir = root / "ml" / "markers" / "gate-seals" / "ocr-test" / f"gate-{suffix}"
    gate_snapshot = gate_dir / "source-snapshot.json"
    gate_snapshot.parent.mkdir(parents=True, exist_ok=True)
    _write(gate_snapshot, {
        "sources": [{"path": source_relative, "sha256": sha256_file(source)}]
    })
    gate_binding = {
        "task": "ocr-test",
        "revision": f"r{suffix}",
        "candidate_hashes": {"model": CANDIDATE_SHA256},
        "candidate_hash_key_schema": ["model"],
        "dataset_manifest_sha256": dataset_manifest_sha256,
        "split_config_path": "ml/policy/fake-split.json",
        "split_config_sha256": "5" * 64,
        "evaluator_source_paths": [source_relative],
        "evaluator_source_bundle_sha256": "6" * 64,
        "gate_config_sha256": "7" * 64,
        "evidence_split": "sealed",
        "evidence_policy": evidence_policy_reference(),
        "ledger_mode": "canonical_repository",
        "ledger_root": "ml/markers/gate-seals",
        "retired_policy_sha256": "8" * 64,
        "source_snapshot_path": gate_snapshot.relative_to(root).as_posix(),
        "source_snapshot_sha256": sha256_file(gate_snapshot),
        "source_binding_mode": "immutable_pre_run_snapshot",
        "base_commit": "9" * 40,
    }
    gate_key = sha256_bytes(canonical_json_bytes({
        "task": "ocr-test",
        "revision": f"r{suffix}",
        "candidate_hashes": gate_binding["candidate_hashes"],
    }))
    gate_opened = gate_dir / "opened.json"
    _write(gate_opened, {
        "schema_version": 1,
        "status": "opened",
        "key": gate_key,
        "binding": gate_binding,
    })
    gate = GateSeal(gate_key, gate_dir, gate_opened, gate_binding, root, gate_snapshot)
    return authorization, gate


def _registry(root: Path, *, count: int = 3) -> tuple[Path, list[str]]:
    path = root / REGISTRY
    scope = {
        "purpose": sealed_reserve.ACCEPTANCE_PURPOSE,
        "acceptance_scope": ocr_sealed_acceptance.ACCEPTANCE_SCOPE,
        "coverage_protocol_path": ocr_sealed_acceptance.PROTOCOL_PATH.as_posix(),
        "coverage_protocol_sha256": ocr_sealed_acceptance.PROTOCOL_SHA256,
    }
    sets: list[dict[str, object]] = []
    set_ids: list[str] = []
    for index in range(count):
        config_sha256 = f"{index + 1:064x}"
        source_rows = [{
            "path": f"ml/synthetic/fake-source-{index}.py",
            "sha256": f"{index + 101:064x}",
        }]
        source_bundle_sha256 = sealed_reserve._source_bundle(source_rows)
        archive_sha256 = f"{index + 201:064x}"
        set_id = sealed_reserve._set_identity(
            config_sha256, source_bundle_sha256, archive_sha256
        )
        set_ids.append(set_id)
        sets.append({
            "set_id": set_id,
            "scope": deepcopy(scope),
            "generator": {
                "config_path": f"ml/synthetic/fake-config-{index}.json",
                "config_sha256": config_sha256,
                "source_paths": [row["path"] for row in source_rows],
                "source_sha256": [row["sha256"] for row in source_rows],
                "source_bundle_sha256": source_bundle_sha256,
            },
            "archive": {
                "path": f"artifacts/synthetic-sealed-reserves/fake-{index}.zip",
                "sha256": archive_sha256,
                "byte_count": index + 1,
            },
            "chain": {
                "config_schema": sealed_reserve.GENERATION_SCHEMA,
                "archive_schema": sealed_reserve.ARCHIVE_SCHEMA,
                "archive_manifest_sha256": MANIFEST_SHA256,
                "source_snapshot_manifest_sha256": f"{index + 301:064x}",
                "payload_bundle_sha256": f"{index + 401:064x}",
                "case_identity_sha256": [f"{index * 10 + item + 501:064x}" for item in range(4)],
                "case_count": 4,
            },
            "state": "unused",
            "uses": [],
            "retirement": None,
        })
    _write(path, {
        "schema": sealed_reserve.REGISTRY_SCHEMA,
        "evidence_policy": evidence_policy_reference(),
        "generation": 0,
        "sets": sets,
    })
    return path, set_ids


@pytest.fixture(autouse=True)
def _prove_coordinator_never_reads_archive_payloads(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    original_admission_hash = admission_module.sha256_file
    original_registry_hash = sealed_reserve.sha256_file

    def reject_archive_hash(path: Path) -> str:
        if Path(path).suffix == ".zip":
            raise AssertionError("coordinator must not hash reserve archives")
        return original_admission_hash(Path(path))

    def reject_registry_archive_hash(path: Path) -> str:
        if Path(path).suffix == ".zip":
            raise AssertionError("coordinator must not hash reserve archives")
        return original_registry_hash(Path(path))

    monkeypatch.setattr(admission_module, "sha256_file", reject_archive_hash)
    monkeypatch.setattr(sealed_reserve, "sha256_file", reject_registry_archive_hash)
    monkeypatch.setattr(
        sealed_reserve,
        "load_registry",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(
            AssertionError("coordinator must not invoke full registry validation")
        ),
    )
    monkeypatch.setattr(
        sealed_reserve.zipfile,
        "ZipFile",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(
            AssertionError("coordinator must not open reserve archives")
        ),
    )


def _prepare(root: Path, *, count: int = 3, suffix: str = "1"):
    registry, set_ids = _registry(root, count=count)
    authorization, gate = _source_bound_objects(root, suffix=suffix)
    admission = prepare_admission(
        REGISTRY,
        root,
        expected_registry_sha256=sha256_file(registry),
        set_id=set_ids[0],
        candidate_sha256=CANDIDATE_SHA256,
        training_authorization=authorization,
        gate_seal=gate,
        required_acceptance_scope=ocr_sealed_acceptance.ACCEPTANCE_SCOPE,
        required_coverage_protocol_sha256=ocr_sealed_acceptance.PROTOCOL_SHA256,
    )
    return admission, authorization, gate, registry


def _authority(admission) -> dict[str, object]:
    return json.loads(admission.authority_path.read_text(encoding="utf-8"))


def _confirm(admission) -> None:
    send_ack(
        admission,
        attempt_id=ATTEMPT_ID,
        request_nonce=NONCE,
        write_and_flush=lambda _frame: None,
    )
    record_positive_read_receipt(
        admission,
        attempt_id=ATTEMPT_ID,
        candidate_sha256=CANDIDATE_SHA256,
        request_nonce=NONCE,
    )


def test_ack_intent_is_durable_before_ack_without_claiming_a_read(tmp_path: Path) -> None:
    admission, authorization, gate, registry = _prepare(tmp_path)
    observed: list[str] = []

    def write_ack(frame: str) -> None:
        record = _authority(admission)
        assert record["status"] == "ack_intent"
        assert record["read_status"] == "possible"
        assert all(not item["uses"] for item in json.loads(registry.read_text())["sets"])
        assert not authorization.consumed_path.exists()
        assert not gate.consumed_path.exists()
        observed.append(frame)

    receipt = send_ack(
        admission,
        attempt_id=ATTEMPT_ID,
        request_nonce=NONCE,
        write_and_flush=write_ack,
    )
    assert observed == [f"G22_READ_ACK/1 {NONCE}\n"]
    assert (receipt.status, receipt.read_status) == ("ack_intent", "possible")
    with pytest.raises(RuntimeError, match="pending coordinated"):
        consume_training(authorization)
    with pytest.raises(RuntimeError, match="pending coordinated"):
        consume_gate(gate)


def test_positive_read_receipt_is_only_confirmed_read_transition(tmp_path: Path) -> None:
    admission, authorization, gate, registry = _prepare(tmp_path)
    _confirm(admission)
    assert _authority(admission)["status"] == "confirmed_read"
    assert authorization.consumed_path.is_file()
    assert gate.consumed_path.is_file()
    uses = [
        use
        for item in json.loads(registry.read_text(encoding="utf-8"))["sets"]
        for use in item["uses"]
    ]
    assert len(uses) == 1
    assert uses[0]["read_binding_sha256"] == admission.admission_id
    repeated = record_positive_read_receipt(
        admission,
        attempt_id=ATTEMPT_ID,
        candidate_sha256=CANDIDATE_SHA256,
        request_nonce=NONCE,
    )
    assert repeated.status == "confirmed_read"
    assert sum(len(item["uses"]) for item in json.loads(registry.read_text())["sets"]) == 1


def test_ack_exception_is_possible_read_and_never_voidable(tmp_path: Path) -> None:
    admission, authorization, gate, registry = _prepare(tmp_path)
    with pytest.raises(BrokenPipeError):
        send_ack(
            admission,
            attempt_id=ATTEMPT_ID,
            request_nonce=NONCE,
            write_and_flush=lambda _frame: (_ for _ in ()).throw(BrokenPipeError("uncertain")),
        )
    assert _authority(admission)["status"] == "possible_read"
    assert not authorization.consumed_path.exists()
    assert not gate.consumed_path.exists()
    assert all(not item["uses"] for item in json.loads(registry.read_text())["sets"])
    with pytest.raises(SealedFirstReadAdmissionError, match="cannot be voided"):
        void_pre_ack(admission, RuntimeError("too late"))


def test_recovery_of_ack_intent_becomes_possible_without_relaunch(tmp_path: Path) -> None:
    admission, authorization, gate, _registry_path = _prepare(tmp_path)
    send_ack(
        admission,
        attempt_id=ATTEMPT_ID,
        request_nonce=NONCE,
        write_and_flush=lambda _frame: None,
    )
    recovered = recover_admission(
        REGISTRY,
        tmp_path,
        admission_id=admission.admission_id,
        training_authorization=authorization,
        gate_seal=gate,
    )
    assert _authority(recovered)["status"] == "possible_read"
    with pytest.raises(SealedFirstReadAdmissionError, match="prepared admission"):
        send_ack(
            recovered,
            attempt_id="attempt-2",
            request_nonce="c" * 64,
            write_and_flush=lambda _frame: pytest.fail("must not ACK again"),
        )


@pytest.mark.parametrize("missing_role", ["both", "gate"])
def test_recovery_voids_interrupted_pre_ack_preparation(
    tmp_path: Path, missing_role: str
) -> None:
    admission, authorization, gate, registry = _prepare(tmp_path)
    (gate.directory / FIRST_READ_INTENT_NAME).unlink()
    if missing_role == "both":
        (authorization.directory / FIRST_READ_INTENT_NAME).unlink()
    recovered = recover_admission(
        REGISTRY,
        tmp_path,
        admission_id=admission.admission_id,
        training_authorization=authorization,
        gate_seal=gate,
    )
    assert _authority(recovered)["status"] == "void"
    assert (authorization.directory / "void.json").is_file()
    assert (gate.directory / "void.json").is_file()
    assert not authorization.consumed_path.exists()
    assert not gate.consumed_path.exists()
    assert all(not item["uses"] for item in json.loads(registry.read_text())["sets"])


def test_recovery_finishes_partial_void_projection_cleanup(tmp_path: Path) -> None:
    admission, authorization, gate, _registry_path = _prepare(tmp_path)
    training_intent = authorization.directory / FIRST_READ_INTENT_NAME
    gate_intent = gate.directory / FIRST_READ_INTENT_NAME
    training_intent_payload = training_intent.read_bytes()
    gate_intent_payload = gate_intent.read_bytes()
    void_pre_ack(admission, RuntimeError("interrupted cleanup"))

    training_archive = authorization.directory / "void-attempts" / admission.admission_id
    gate_archive = gate.directory / "void-attempts" / admission.admission_id
    training_snapshot = training_archive / "source-snapshot.json"
    gate_snapshot = gate_archive / "source-snapshot.json"
    training_snapshot.replace(authorization.snapshot_path)
    gate_snapshot.replace(gate.snapshot_path)
    training_intent.write_bytes(training_intent_payload)
    gate_intent.write_bytes(gate_intent_payload)

    recovered = recover_admission(
        REGISTRY,
        tmp_path,
        admission_id=admission.admission_id,
        training_authorization=authorization,
        gate_seal=gate,
    )
    assert _authority(recovered)["status"] == "void"
    assert not training_intent.exists()
    assert not gate_intent.exists()
    assert (training_archive / "source-snapshot.json").is_file()
    assert (gate_archive / "source-snapshot.json").is_file()


def test_bound_metadata_is_available_only_before_ack(tmp_path: Path) -> None:
    admission, _authorization, _gate, _registry_path = _prepare(tmp_path)
    assert bound_reserve_metadata(admission)["set_id"] == _authority(admission)["set_id"]
    send_ack(
        admission,
        attempt_id=ATTEMPT_ID,
        request_nonce=NONCE,
        write_and_flush=lambda _frame: None,
    )
    with pytest.raises(SealedFirstReadAdmissionError, match="only before ACK"):
        bound_reserve_metadata(admission)


@pytest.mark.parametrize("failure_point", ["before_training_intent", "after_training_intent"])
def test_prepare_failure_after_authority_is_deterministically_void(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    failure_point: str,
) -> None:
    registry, set_ids = _registry(tmp_path)
    authorization, gate = _source_bound_objects(tmp_path)
    if failure_point == "before_training_intent":
        monkeypatch.setattr(
            admission_module,
            "prepare_training_intent",
            lambda *_args, **_kwargs: (_ for _ in ()).throw(RuntimeError("fault")),
        )
    else:
        monkeypatch.setattr(
            admission_module,
            "prepare_gate_intent",
            lambda *_args, **_kwargs: (_ for _ in ()).throw(RuntimeError("fault")),
        )
    with pytest.raises(RuntimeError, match="fault"):
        prepare_admission(
            REGISTRY,
            tmp_path,
            expected_registry_sha256=sha256_file(registry),
            set_id=set_ids[0],
            candidate_sha256=CANDIDATE_SHA256,
            training_authorization=authorization,
            gate_seal=gate,
            required_acceptance_scope=ocr_sealed_acceptance.ACCEPTANCE_SCOPE,
            required_coverage_protocol_sha256=ocr_sealed_acceptance.PROTOCOL_SHA256,
        )
    authorities = list(
        sealed_reserve.first_read_admission_directory(registry, tmp_path).glob("*.json")
    )
    assert len(authorities) == 1
    assert json.loads(authorities[0].read_text())["status"] == "void"
    assert (authorization.directory / "void.json").is_file()
    assert (gate.directory / "void.json").is_file()


def test_pre_ack_void_is_retryable_and_does_not_claim_read(tmp_path: Path) -> None:
    admission, authorization, gate, registry = _prepare(tmp_path)
    receipt = void_pre_ack(admission, RuntimeError("preflight failed"))
    assert (receipt.status, receipt.read_status) == ("void", "none")
    assert (authorization.directory / "void.json").is_file()
    assert (gate.directory / "void.json").is_file()
    assert not authorization.consumed_path.exists()
    assert not gate.consumed_path.exists()
    assert all(not item["uses"] for item in json.loads(registry.read_text())["sets"])


def test_two_unused_sets_reject_ack_before_callback(tmp_path: Path) -> None:
    admission, _authorization, _gate, _registry_path = _prepare(tmp_path, count=2)
    called = False

    def write_ack(_frame: str) -> None:
        nonlocal called
        called = True

    with pytest.raises(SealedFirstReadAdmissionError, match="minimum unused reserve"):
        send_ack(
            admission,
            attempt_id=ATTEMPT_ID,
            request_nonce=NONCE,
            write_and_flush=write_ack,
        )
    assert called is False
    assert _authority(admission)["status"] == "prepared"


def test_receipt_identity_mismatch_cannot_confirm_read(tmp_path: Path) -> None:
    admission, _authorization, _gate, _registry_path = _prepare(tmp_path)
    send_ack(
        admission,
        attempt_id=ATTEMPT_ID,
        request_nonce=NONCE,
        write_and_flush=lambda _frame: None,
    )
    with pytest.raises(SealedFirstReadAdmissionError, match="identity mismatch"):
        record_positive_read_receipt(
            admission,
            attempt_id=ATTEMPT_ID,
            candidate_sha256="c" * 64,
            request_nonce=NONCE,
        )
    assert _authority(admission)["status"] == "ack_intent"


def test_send_ack_revalidates_sources_and_bound_reserve_metadata(tmp_path: Path) -> None:
    admission, authorization, _gate, registry_path = _prepare(tmp_path)
    source_path = tmp_path / authorization.binding["runner_source_paths"][0]
    source_path.write_text("changed\n", encoding="utf-8")
    with pytest.raises(RuntimeError, match="source changed"):
        send_ack(
            admission,
            attempt_id=ATTEMPT_ID,
            request_nonce=NONCE,
            write_and_flush=lambda _frame: pytest.fail("must not ACK"),
        )
    source_path.write_text("VALUE = '1'\n", encoding="utf-8")
    registry = json.loads(registry_path.read_text())
    registry["sets"][0]["archive"]["byte_count"] += 1
    _write(registry_path, registry)
    with pytest.raises(SealedFirstReadAdmissionError, match="metadata changed"):
        send_ack(
            admission,
            attempt_id=ATTEMPT_ID,
            request_nonce=NONCE,
            write_and_flush=lambda _frame: pytest.fail("must not ACK"),
        )


def test_reserve_projection_rejects_exact_binding_on_wrong_set(tmp_path: Path) -> None:
    admission, _authorization, _gate, registry_path = _prepare(tmp_path)
    send_ack(
        admission,
        attempt_id=ATTEMPT_ID,
        request_nonce=NONCE,
        write_and_flush=lambda _frame: None,
    )
    registry = json.loads(registry_path.read_text())
    registry["sets"][1]["uses"] = [{
        "revision": "r1",
        "candidate_id": "P1",
        "gate_identity_sha256": _authority(admission)["gate_identity_sha256"],
        "read_binding_sha256": admission.admission_id,
        "aggregate_only": True,
        "disclosures": [],
    }]
    registry["sets"][1]["state"] = "reusable"
    _write(registry_path, registry)
    with pytest.raises(SealedFirstReadAdmissionError, match="differs"):
        record_positive_read_receipt(
            admission,
            attempt_id=ATTEMPT_ID,
            candidate_sha256=CANDIDATE_SHA256,
            request_nonce=NONCE,
        )


def test_completed_set_can_back_a_distinct_revision(tmp_path: Path) -> None:
    first, _authorization, _gate, registry_path = _prepare(tmp_path, count=3, suffix="1")
    _confirm(first)
    complete_admission(first, aggregate_result_sha256="e" * 64)
    selected_set = json.loads(registry_path.read_text())["sets"][0]["set_id"]
    authorization, gate = _source_bound_objects(tmp_path, suffix="2")
    second = prepare_admission(
        REGISTRY,
        tmp_path,
        expected_registry_sha256=sha256_file(registry_path),
        set_id=selected_set,
        candidate_sha256=CANDIDATE_SHA256,
        training_authorization=authorization,
        gate_seal=gate,
        required_acceptance_scope=ocr_sealed_acceptance.ACCEPTANCE_SCOPE,
        required_coverage_protocol_sha256=ocr_sealed_acceptance.PROTOCOL_SHA256,
    )
    send_ack(
        second,
        attempt_id="attempt-2",
        request_nonce="c" * 64,
        write_and_flush=lambda _frame: None,
    )
    record_positive_read_receipt(
        second,
        attempt_id="attempt-2",
        candidate_sha256=CANDIDATE_SHA256,
        request_nonce="c" * 64,
    )
    assert len(json.loads(registry_path.read_text())["sets"][0]["uses"]) == 2


def test_confirmed_read_can_complete_or_fail_with_exact_status(tmp_path: Path) -> None:
    admission, _authorization, _gate, _registry_path = _prepare(tmp_path)
    _confirm(admission)
    completed = complete_admission(admission, aggregate_result_sha256="e" * 64)
    assert (completed.status, completed.read_status) == ("completed", "confirmed")

    second_root = tmp_path / "second"
    second, _authorization, _gate, _registry_path = _prepare(second_root)
    _confirm(second)
    failed = fail_admission(second, RuntimeError("aggregate failed"))
    assert (failed.status, failed.read_status) == ("failed", "confirmed")


def test_actual_canonical_acquisitions_are_admitted_with_unsorted_hash_schema(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(
        "ml.markers.gate_seal._current_base_commit", lambda *_args, **_kwargs: "f" * 40
    )
    for policy_path in (
        Path("ml/policy/evidence-policy.json"),
        Path("ml/policy/acceptance-bars.json"),
    ):
        _write(tmp_path / policy_path, {"schema_version": 1})

    runner_path = Path("runner.py")
    (tmp_path / runner_path).write_text("RUNNER = 1\n", encoding="utf-8")
    runner_bundle = source_bundle_sha256(tmp_path, (runner_path,))
    candidate_path = Path("candidate.json")
    _write(tmp_path / candidate_path, {
        "task": "ocr-test",
        "revision": "ractual",
        "candidate_id": "P1",
        "expected_runner_source_bundle_sha256": runner_bundle,
    })
    ledger_path = tmp_path / CANONICAL_LEDGER_PATH
    _write(ledger_path, {"revisions": [{
        "task": "ocr-test",
        "revision": "ractual",
        "status": "candidate_1_preregistered",
        "execution_authorized": True,
        "authorized_candidate_id": "P1",
        "preregistered_candidate_ids": ["P1"],
        "consumed_candidate_ids": [],
        "candidate_config_paths": {"P1": candidate_path.as_posix()},
        "candidate_config_sha256": {"P1": sha256_file(tmp_path / candidate_path)},
    }]})
    authorization = acquire_training_candidate(
        tmp_path,
        task="ocr-test",
        revision="ractual",
        candidate_id="P1",
        config_path=candidate_path,
        runner_source_paths=(runner_path,),
    )

    evaluator_path = Path("evaluator.py")
    (tmp_path / evaluator_path).write_text("EVALUATOR = 1\n", encoding="utf-8")
    retired_path = tmp_path / "ml/markers/gate-seals/retired-historical-pairs.json"
    _write(retired_path, {"schema_version": 1, "pairs": []})
    candidate_hashes = {"z_model": CANDIDATE_SHA256, "a_config": "c" * 64}
    gate_config = {"threshold": 0.5}
    split_path = Path("split.json")
    _write(tmp_path / split_path, {
        "task": "ocr-test",
        "revision": "ractual",
        "expected_candidate_hash_keys": ["z_model", "a_config"],
        "expected_dataset_manifest_sha256": MANIFEST_SHA256,
        "expected_evaluator_source_bundle_sha256": source_bundle_sha256(
            tmp_path, (evaluator_path,)
        ),
        "expected_gate_config_sha256": sha256_bytes(canonical_json_bytes(gate_config)),
    })
    gate = acquire_gate_seal(
        repo_root=tmp_path,
        task="ocr-test",
        revision="ractual",
        candidate_hashes=candidate_hashes,
        dataset_manifest_sha256=MANIFEST_SHA256,
        split_config_path=split_path,
        evaluator_source_paths=(evaluator_path,),
        gate_config=gate_config,
    )
    registry, set_ids = _registry(tmp_path)
    admission = prepare_admission(
        REGISTRY,
        tmp_path,
        expected_registry_sha256=sha256_file(registry),
        set_id=set_ids[0],
        candidate_sha256=CANDIDATE_SHA256,
        training_authorization=authorization,
        gate_seal=gate,
        required_acceptance_scope=ocr_sealed_acceptance.ACCEPTANCE_SCOPE,
        required_coverage_protocol_sha256=ocr_sealed_acceptance.PROTOCOL_SHA256,
    )
    assert _authority(admission)["status"] == "prepared"


def test_duplicate_json_keys_are_rejected(tmp_path: Path) -> None:
    admission, _authorization, _gate, registry_path = _prepare(tmp_path)
    authority_text = admission.authority_path.read_text(encoding="utf-8")
    admission.authority_path.write_text(
        authority_text.replace(
            '{\n  "acceptance_scope"',
            '{\n  "status": "prepared",\n  "acceptance_scope"',
        ),
        encoding="utf-8",
    )
    with pytest.raises(SealedFirstReadAdmissionError, match="missing or corrupt"):
        send_ack(
            admission,
            attempt_id=ATTEMPT_ID,
            request_nonce=NONCE,
            write_and_flush=lambda _frame: pytest.fail("must not ACK"),
        )

    registry_text = registry_path.read_text(encoding="utf-8")
    registry_path.write_text(
        registry_text.replace(
            '{\n  "evidence_policy"',
            '{\n  "generation": 0,\n  "evidence_policy"',
        ),
        encoding="utf-8",
    )
    with pytest.raises(sealed_reserve.SealedReserveError, match="valid JSON"):
        sealed_reserve.load_registry_metadata_only(registry_path, tmp_path)


def test_identity_only_sidecar_loader_recomputes_content_identity(tmp_path: Path) -> None:
    admission, _authorization, _gate, registry_path = _prepare(tmp_path)
    authority = _authority(admission)
    authority["revision"] = "edited-revision"
    _write(admission.authority_path, authority)
    with pytest.raises(sealed_reserve.SealedReserveError, match="content identity differs"):
        sealed_reserve.first_read_admission_identities(registry_path, tmp_path)


def test_identity_only_sidecar_loader_rejects_different_read_binding(tmp_path: Path) -> None:
    admission, _authorization, _gate, registry_path = _prepare(tmp_path)
    authority = _authority(admission)
    authority["read_binding_sha256"] = "f" * 64
    _write(admission.authority_path, authority)
    with pytest.raises(sealed_reserve.SealedReserveError, match="file identity differs"):
        sealed_reserve.first_read_admission_identities(registry_path, tmp_path)


def test_metadata_only_loader_matches_case_disclosure_retirement_consistency(
    tmp_path: Path,
) -> None:
    registry_path, _set_ids = _registry(tmp_path)
    registry = json.loads(registry_path.read_text())
    registry["sets"][0]["state"] = "retired"
    registry["sets"][0]["uses"] = [{
        "revision": "r1",
        "candidate_id": "P1",
        "gate_identity_sha256": "e" * 64,
        "read_binding_sha256": "f" * 64,
        "aggregate_only": False,
        "disclosures": ["truth"],
    }]
    registry["sets"][0]["retirement"] = {
        "reason": "case_level_disclosure",
        "evidence_sha256": "a" * 64,
        "disclosures": ["truth"],
    }
    _write(registry_path, registry)
    with pytest.raises(
        sealed_reserve.SealedReserveError,
        match="inconsistent metadata-only state",
    ):
        sealed_reserve.load_registry_metadata_only(registry_path, tmp_path)
