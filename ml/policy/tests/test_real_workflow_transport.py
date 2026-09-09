# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic process-boundary tests, without model or corpus access."""

from pathlib import Path

import pytest

from ml.policy.evidence_policy import evidence_policy_reference
from ml.policy.real_workflow_ledger import (
    RealWorkflowCandidateDescriptor,
    RealWorkflowLedgerError,
    load_ledger,
    open_attempt,
    record_attempt_failure,
)
from ml.policy.real_workflow_transport import FirstReadAccountingBridge


def descriptor() -> RealWorkflowCandidateDescriptor:
    return RealWorkflowCandidateDescriptor(
        "fixture-revision", "fixture-candidate", "a" * 64, "b" * 64,
        str(evidence_policy_reference()["sha256"]), "c" * 64, "d" * 64,
    )


def request() -> str:
    return f"G22_FIRST_READ/1 attempt-1 {'a' * 64} {'e' * 64}"


def test_ack_is_emitted_only_after_persistent_consumption(tmp_path: Path) -> None:
    path = tmp_path / "ledger.json"
    with open_attempt(path, descriptor(), attempt_id="attempt-1") as attempt:
        replies = []

        def write_ack(line: str) -> None:
            persisted = load_ledger(path)["candidates"][0]
            assert persisted["consumed_attempt_id"] == "attempt-1"
            assert persisted["attempts"][0]["status"] == "consumed"
            replies.append(line)

        bridge = FirstReadAccountingBridge(attempt)
        bridge.handle_request(request(), write_ack)
        assert replies == [f"G22_READ_ACK/1 {'e' * 64}\n"]
        with pytest.raises(RealWorkflowLedgerError, match="already received"):
            bridge.handle_request(request(), write_ack)
        assert len(replies) == 1


@pytest.mark.parametrize("invalid", [
    "", request() + "\n", request() + " extra",
    request().replace("attempt-1", "attempt-other"),
    request().replace("a" * 64, "f" * 64),
    request().replace("e" * 64, "E" * 64), "x" * 513,
])
def test_invalid_frame_never_consumes_or_acknowledges(tmp_path: Path, invalid: str) -> None:
    path = tmp_path / "ledger.json"
    with open_attempt(path, descriptor(), attempt_id="attempt-1") as attempt:
        replies = []
        with pytest.raises(RealWorkflowLedgerError):
            FirstReadAccountingBridge(attempt).handle_request(invalid, replies.append)
        assert replies == []
        assert load_ledger(path)["candidates"][0]["consumed_attempt_id"] is None


def test_failed_ack_cannot_reopen_candidate_budget(tmp_path: Path) -> None:
    path = tmp_path / "ledger.json"
    with open_attempt(path, descriptor(), attempt_id="attempt-1") as attempt:
        bridge = FirstReadAccountingBridge(attempt)

        def broken_pipe(_: str) -> None:
            raise BrokenPipeError("fixture disconnected")

        with pytest.raises(BrokenPipeError):
            bridge.handle_request(request(), broken_pipe)
        assert record_attempt_failure(attempt, BrokenPipeError()) == "consumed"
        assert load_ledger(path)["candidates"][0]["attempts"][0]["status"] == "failed_consumed"
    with pytest.raises(RealWorkflowLedgerError):
        open_attempt(path, descriptor(), attempt_id="attempt-2")


def test_closed_lease_cannot_acknowledge(tmp_path: Path) -> None:
    path = tmp_path / "ledger.json"
    attempt = open_attempt(path, descriptor(), attempt_id="attempt-1")
    attempt.close()
    replies = []
    with pytest.raises(RealWorkflowLedgerError):
        FirstReadAccountingBridge(attempt).handle_request(request(), replies.append)
    assert replies == []
    assert load_ledger(path)["candidates"][0]["consumed_attempt_id"] is None
