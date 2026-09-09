# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic-only tests for real workflow first-read accounting."""

from __future__ import annotations

import hashlib
import json
import multiprocessing
from pathlib import Path
from typing import Any

import pytest

from ml.policy import real_workflow_ledger as ledger_module
from ml.policy.evidence_policy import evidence_policy_reference
from ml.policy.real_workflow_ledger import (
    RealWorkflowCandidateDescriptor,
    RealWorkflowLedgerError,
    complete_attempt,
    load_ledger,
    open_attempt,
    record_corpus_content,
    record_disclosure,
    record_attempt_failure,
    record_first_sealed_read,
    record_interrupted_attempt_failure,
)


def _digest(label: str) -> str:
    return hashlib.sha256(label.encode("utf-8")).hexdigest()


def _descriptor(
    revision: str = "revision-1",
    candidate: str = "candidate-1",
    **changes: str,
) -> RealWorkflowCandidateDescriptor:
    values = {
        "revision": revision,
        "candidate_id": candidate,
        "candidate_sha256": _digest(candidate),
        "protocol_sha256": _digest("protocol-1"),
        "evidence_policy_sha256": str(evidence_policy_reference()["sha256"]),
        "assignment_sha256": _digest("assignment-171"),
        "selected_inventory_sha256": _digest("selected-real-sealed-51"),
    }
    values.update(changes)
    return RealWorkflowCandidateDescriptor(**values)


def _open_existing_attempt(
    path: str,
    descriptor: RealWorkflowCandidateDescriptor,
    results: Any,
) -> None:
    try:
        attempt = open_attempt(Path(path), descriptor, attempt_id="attempt-concurrent")
        attempt.close()
        results.put("OPENED")
    except BaseException as error:  # pragma: no cover - surfaced to parent process
        results.put(f"ERROR:{type(error).__name__}:{error}")


def test_valid_first_read_is_durable_and_idempotent(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    descriptor = _descriptor()
    attempt = open_attempt(path, descriptor, attempt_id="attempt-1")

    first = record_first_sealed_read(attempt)
    second = record_first_sealed_read(attempt)
    document = load_ledger(path)

    assert first["status"] == second["status"] == "consumed"
    assert first["generation"] == second["generation"] == 2
    assert document["candidates"][0]["consumed_attempt_id"] == "attempt-1"
    assert document["candidates"][0]["attempts"][0]["first_read_utc"] is not None
    assert str(tmp_path) not in path.read_text(encoding="utf-8")
    attempt.close()


def test_first_observed_corpus_content_is_durable_and_bound_to_attempt(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    attempt = open_attempt(path, _descriptor(), attempt_id="attempt-content")
    record_first_sealed_read(attempt)
    content_sha256 = _digest("ordered selected project bytes")

    first = record_corpus_content(attempt, content_sha256)
    second = record_corpus_content(attempt, content_sha256)
    document = load_ledger(path)

    assert first == second
    assert first["status"] == "content-bound"
    assert first["content_sha256"] == content_sha256
    assert document["content_identity"] == {
        "algorithm": "graphreader.frozen-real-workflow-corpus-content.v1",
        "sha256": content_sha256,
        "first_observed_attempt_id": "attempt-content",
        "first_observed_utc": document["content_identity"]["first_observed_utc"],
        "first_observed_revision": "revision-1",
        "first_observed_candidate_id": "candidate-1",
        "legacy_unobserved_consumed_attempts": 0,
    }
    assert document["content_identity"]["first_observed_utc"]
    assert document["candidates"][0]["attempts"][0]["observed_content_sha256"] == content_sha256
    attempt.close()


def test_same_corpus_content_is_allowed_across_permitted_candidates(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    content_sha256 = _digest("stable selected project bytes")
    first = open_attempt(path, _descriptor("revision-1", "candidate-1"), attempt_id="attempt-1")
    record_first_sealed_read(first)
    record_corpus_content(first, content_sha256)
    complete_attempt(first, aggregate_result_sha256=_digest("result-1"))

    second = open_attempt(path, _descriptor("revision-2", "candidate-2"), attempt_id="attempt-2")
    record_first_sealed_read(second)
    record_corpus_content(second, content_sha256)
    complete_attempt(second, aggregate_result_sha256=_digest("result-2"))

    document = load_ledger(path)
    assert document["content_identity"]["sha256"] == content_sha256
    assert [
        item["attempts"][0]["observed_content_sha256"]
        for item in document["candidates"]
    ] == [content_sha256, content_sha256]


def test_corpus_content_mismatch_is_rejected_after_consumption_without_changing_pin(
    tmp_path: Path,
) -> None:
    path = tmp_path / "runtime-ledger.json"
    original_sha256 = _digest("first observed selected project bytes")
    first = open_attempt(path, _descriptor("revision-1", "candidate-1"), attempt_id="attempt-1")
    record_first_sealed_read(first)
    record_corpus_content(first, original_sha256)
    complete_attempt(first, aggregate_result_sha256=_digest("result-1"))

    second = open_attempt(path, _descriptor("revision-2", "candidate-2"), attempt_id="attempt-2")
    record_first_sealed_read(second)
    with pytest.raises(RealWorkflowLedgerError, match="content identity differs"):
        record_corpus_content(second, _digest("changed selected project bytes"))
    assert record_attempt_failure(second, RuntimeError("corpus content mismatch")) == "consumed"

    document = load_ledger(path)
    assert document["content_identity"]["sha256"] == original_sha256
    state = document["candidates"][1]["attempts"][0]
    assert state["status"] == "failed_consumed"
    assert state["observed_content_sha256"] is None


def test_completion_requires_a_durable_corpus_content_observation(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    attempt = open_attempt(path, _descriptor(), attempt_id="attempt-without-content")
    record_first_sealed_read(attempt)

    with pytest.raises(RealWorkflowLedgerError, match="content identity"):
        complete_attempt(attempt, aggregate_result_sha256=_digest("must-not-complete"))
    assert load_ledger(path)["candidates"][0]["attempts"][0]["status"] == "consumed"
    attempt.close()


def test_legacy_ledger_migrates_with_explicit_unobserved_content_state(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    legacy_attempt = open_attempt(path, _descriptor(), attempt_id="legacy-consumed")
    record_first_sealed_read(legacy_attempt)
    record_attempt_failure(legacy_attempt, RuntimeError("historical failure"))
    legacy = load_ledger(path)
    legacy["schema"] = "graphreader.real-workflow-sealed-use-ledger.v1"
    legacy.pop("content_identity")
    for candidate in legacy["candidates"]:
        for attempt in candidate["attempts"]:
            attempt.pop("observed_content_sha256")
    legacy["integrity_sha256"] = ledger_module._integrity(legacy)
    path.write_bytes(ledger_module._canonical_bytes(legacy))

    normalized = load_ledger(path)

    assert normalized["schema"] == ledger_module.LEDGER_SCHEMA
    assert normalized["content_identity"]["sha256"] is None
    assert normalized["content_identity"]["first_observed_attempt_id"] is None
    assert normalized["content_identity"]["legacy_unobserved_consumed_attempts"] == 1
    assert normalized["candidates"][0]["attempts"][0]["observed_content_sha256"] is None
    assert json.loads(path.read_text(encoding="utf-8"))["schema"].endswith(".v1")

    current = open_attempt(
        path,
        _descriptor("revision-2", "candidate-2"),
        attempt_id="first-observed",
    )
    record_first_sealed_read(current)
    content_sha256 = _digest("first observed after legacy ledger")
    record_corpus_content(current, content_sha256)
    complete_attempt(current, aggregate_result_sha256=_digest("current result"))

    persisted = json.loads(path.read_text(encoding="utf-8"))
    assert persisted["schema"] == ledger_module.LEDGER_SCHEMA
    assert persisted["content_identity"]["sha256"] == content_sha256
    assert persisted["content_identity"]["legacy_unobserved_consumed_attempts"] == 1


def test_pre_read_failure_is_void_and_same_authorization_can_retry(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    descriptor = _descriptor()
    first = open_attempt(path, descriptor, attempt_id="attempt-void")

    assert record_attempt_failure(first, RuntimeError("private path omitted")) == "void"
    retry = open_attempt(path, descriptor, attempt_id="attempt-retry")
    assert record_first_sealed_read(retry)["status"] == "consumed"

    attempts = load_ledger(path)["candidates"][0]["attempts"]
    assert [item["status"] for item in attempts] == ["void", "consumed"]
    assert attempts[0]["exception"]["type"] == "RuntimeError"
    assert "private path omitted" not in path.read_text(encoding="utf-8")


def test_post_read_failure_remains_consumed_and_cannot_retry(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    descriptor = _descriptor()
    attempt = open_attempt(path, descriptor, attempt_id="attempt-consumed")
    record_first_sealed_read(attempt)

    assert record_attempt_failure(attempt, OSError("decoder failed")) == "consumed"
    with pytest.raises(RealWorkflowLedgerError, match="already consumed"):
        open_attempt(path, descriptor, attempt_id="attempt-illegal-retry")
    state = load_ledger(path)["candidates"][0]["attempts"][0]
    assert state["status"] == "failed_consumed"
    assert state["first_read_utc"] is not None


def test_aggregate_completion_preserves_corpus_but_disclosure_retires(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    first = open_attempt(path, _descriptor(), attempt_id="aggregate-attempt")
    record_first_sealed_read(first)
    record_corpus_content(first, _digest("stable corpus"))
    assert complete_attempt(first, aggregate_result_sha256=_digest("aggregate")) == "completed_aggregate"
    assert load_ledger(path)["retirement"] is None

    second_descriptor = _descriptor("revision-2", "candidate-2")
    second = open_attempt(path, second_descriptor, attempt_id="disclosure-attempt")
    record_first_sealed_read(second)
    record_corpus_content(second, _digest("stable corpus"))
    assert complete_attempt(
        second,
        aggregate_result_sha256=_digest("case-output"),
        disclosures=["prediction"],
    ) == "completed_disclosed"
    assert load_ledger(path)["retirement"]["disclosures"] == ["prediction"]
    with pytest.raises(RealWorkflowLedgerError, match="retired"):
        open_attempt(path, _descriptor("revision-3", "candidate-3"), attempt_id="after-retirement")


def test_disclosure_is_immediate_and_survives_failure(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    attempt = open_attempt(path, _descriptor(), attempt_id="disclosure-then-failure")
    record_first_sealed_read(attempt)
    record_disclosure(
        attempt,
        evidence_sha256=_digest("case-level-evidence"),
        disclosures=["case_identity", "prediction"],
    )
    record_disclosure(
        attempt,
        evidence_sha256=_digest("case-level-evidence"),
        disclosures=["case_identity", "prediction"],
    )
    with pytest.raises(RealWorkflowLedgerError, match="retired"):
        record_first_sealed_read(attempt)
    assert record_attempt_failure(attempt, RuntimeError("failure after disclosure")) == "consumed"

    document = load_ledger(path)
    assert document["retirement"]["disclosures"] == ["case_identity", "prediction"]
    assert document["candidates"][0]["attempts"][0]["status"] == "failed_consumed"


def test_immediate_disclosure_can_finish_with_separate_aggregate_hash(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    attempt = open_attempt(path, _descriptor(), attempt_id="disclosure-then-complete")
    record_first_sealed_read(attempt)
    record_corpus_content(attempt, _digest("stable corpus"))
    disclosure_sha256 = _digest("case-level-evidence")
    aggregate_sha256 = _digest("aggregate-result")
    record_disclosure(
        attempt,
        evidence_sha256=disclosure_sha256,
        disclosures=["truth"],
    )

    assert complete_attempt(
        attempt,
        aggregate_result_sha256=aggregate_sha256,
        disclosures=["truth"],
    ) == "completed_disclosed"
    document = load_ledger(path)
    assert document["retirement"]["evidence_sha256"] == disclosure_sha256
    assert document["candidates"][0]["attempts"][0]["aggregate_result_sha256"] == aggregate_sha256


@pytest.mark.parametrize(
    "change",
    [
        {"candidate_sha256": _digest("different candidate")},
        {"protocol_sha256": _digest("different protocol")},
        {"assignment_sha256": _digest("different assignment")},
        {"selected_inventory_sha256": _digest("different inventory")},
        {"evidence_policy_sha256": "0" * 64},
    ],
)
def test_bound_identities_reject_drift(tmp_path: Path, change: dict[str, str]) -> None:
    path = tmp_path / "runtime-ledger.json"
    first = open_attempt(path, _descriptor(), attempt_id="attempt-1")

    with pytest.raises(RealWorkflowLedgerError, match="identity|policy|corpus"):
        open_attempt(path, _descriptor(**change), attempt_id="attempt-2")
    first.close()


def test_atomic_failure_preserves_retryable_pre_read_state(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    path = tmp_path / "runtime-ledger.json"
    attempt = open_attempt(path, _descriptor(), attempt_id="attempt-atomic")
    original = path.read_bytes()
    real_replace = ledger_module.os.replace

    def fail_replace(source: Path, destination: Path) -> None:
        raise OSError("fixture replace failure")

    monkeypatch.setattr(ledger_module.os, "replace", fail_replace)
    with pytest.raises(OSError, match="fixture replace failure"):
        record_first_sealed_read(attempt)
    assert path.read_bytes() == original
    assert list(tmp_path.glob("*.partial")) == []

    monkeypatch.setattr(ledger_module.os, "replace", real_replace)
    assert record_first_sealed_read(attempt)["status"] == "consumed"
    attempt.close()


def test_partial_or_coordinated_unknown_field_corruption_fails_closed(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    first = open_attempt(path, _descriptor(), attempt_id="attempt-1")
    first.close()
    path.write_bytes(path.read_bytes()[:31])
    with pytest.raises(RealWorkflowLedgerError, match="corrupt"):
        load_ledger(path)

    path.unlink()
    second = open_attempt(path, _descriptor(), attempt_id="attempt-2")
    second.close()
    document = load_ledger(path)
    document["unexpected"] = True
    document["integrity_sha256"] = ledger_module._integrity(document)
    path.write_bytes(ledger_module._canonical_bytes(document))
    with pytest.raises(RealWorkflowLedgerError, match="invalid shape"):
        load_ledger(path)


def test_distinct_revision_limit_comes_from_shared_policy(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    for ordinal in range(5):
        attempt = open_attempt(
            path,
            _descriptor(f"revision-{ordinal}", f"candidate-{ordinal}"),
            attempt_id=f"attempt-{ordinal}",
        )
        record_first_sealed_read(attempt)
        record_corpus_content(attempt, _digest("stable corpus"))
        complete_attempt(attempt, aggregate_result_sha256=_digest(f"result-{ordinal}"))

    sixth = open_attempt(
        path,
        _descriptor("revision-5", "candidate-5"),
        attempt_id="attempt-5",
    )
    with pytest.raises(RealWorkflowLedgerError, match="maximum distinct"):
        record_first_sealed_read(sixth)


def test_live_attempt_lease_rejects_same_id_in_another_process(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    descriptor = _descriptor()
    attempt = open_attempt(path, descriptor, attempt_id="attempt-concurrent")
    context = multiprocessing.get_context("spawn")
    results = context.Queue()
    worker = context.Process(
        target=_open_existing_attempt,
        args=(str(path), descriptor, results),
    )
    worker.start()
    observed = results.get(timeout=15)
    worker.join(timeout=15)

    assert worker.exitcode == 0
    assert observed.startswith("ERROR:RealWorkflowLedgerError:attempt is already owned")
    attempt.close()


def test_consumed_attempt_cannot_restart_after_live_lease_is_lost(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    descriptor = _descriptor()
    attempt = open_attempt(path, descriptor, attempt_id="attempt-concurrent")
    record_first_sealed_read(attempt)
    attempt.close()

    context = multiprocessing.get_context("spawn")
    results = context.Queue()
    worker = context.Process(
        target=_open_existing_attempt,
        args=(str(path), descriptor, results),
    )
    worker.start()
    observed = results.get(timeout=15)
    worker.join(timeout=15)

    assert worker.exitcode == 0
    assert "recover it without reopening evaluation" in observed
    assert record_interrupted_attempt_failure(
        path,
        descriptor,
        attempt_id="attempt-concurrent",
        exception=RuntimeError("runner interrupted after read"),
    ) == "consumed"
    assert load_ledger(path)["candidates"][0]["attempts"][0]["status"] == "failed_consumed"


def test_interrupted_pre_read_attempt_must_be_voided_before_retry(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    descriptor = _descriptor()
    interrupted = open_attempt(path, descriptor, attempt_id="attempt-interrupted")
    interrupted.close()

    with pytest.raises(RealWorkflowLedgerError, match="interrupted active"):
        open_attempt(path, descriptor, attempt_id="attempt-too-soon")
    assert record_interrupted_attempt_failure(
        path,
        descriptor,
        attempt_id="attempt-interrupted",
        exception=RuntimeError("process exited before callback"),
    ) == "void"
    retry = open_attempt(path, descriptor, attempt_id="attempt-retry")
    assert record_first_sealed_read(retry)["status"] == "consumed"
    retry.close()


def test_same_revision_different_candidates_has_no_hidden_reuse_budget(tmp_path: Path) -> None:
    path = tmp_path / "runtime-ledger.json"
    for candidate_id in ("candidate-a", "candidate-b"):
        attempt = open_attempt(
            path,
            _descriptor("revision-shared", candidate_id),
            attempt_id=f"attempt-{candidate_id}",
        )
        record_first_sealed_read(attempt)
        record_corpus_content(attempt, _digest("stable corpus"))
        complete_attempt(attempt, aggregate_result_sha256=_digest(candidate_id + "-result"))
    assert len(load_ledger(path)["candidates"]) == 2
