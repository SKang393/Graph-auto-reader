# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic child-process tests for the aggregate-only workflow supervisor."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import sys

import pytest

from ml.policy.evidence_policy import evidence_policy_reference
from ml.policy.real_workflow_ledger import (
    RealWorkflowCandidateDescriptor,
    RealWorkflowLedgerError,
    load_ledger,
    open_attempt,
)
from tools.private_acceptance.frozen_workflow_process import (
    FrozenWorkflowProcessError,
    run_bound_worker,
)
from tools.private_acceptance import frozen_workflow_process as process_module


_CHILD = r"""
import json
import sys
import time

sys.stdout.reconfigure(newline='\n')
sys.stderr.reconfigure(newline='\n')

mode, candidate, protocol, assignment, inventory, split, aggregate_json = sys.argv[1:8]
attempt_args = sys.argv[8:]
if len(attempt_args) != 2 or attempt_args[0] != '--attempt-id':
    raise SystemExit(19)
attempt_id = attempt_args[1]
nonce = 'e' * 64
content = ('a' if mode == 'different_content' else 'f') * 64

def first_read():
    print(f'G22_FIRST_READ/1 {attempt_id} {candidate} {nonce}', flush=True)
    ack = sys.stdin.readline()
    if ack != f'G22_READ_ACK/1 {nonce}\n':
        raise SystemExit(18)

def bind_content():
    print(f'G22_CORPUS/1 {attempt_id} {candidate} {content} {nonce}', flush=True)
    ack = sys.stdin.readline()
    if ack != f'G22_CORPUS_ACK/1 {nonce}\n':
        raise SystemExit(16)

if mode == 'timeout_pre':
    time.sleep(5)
    raise SystemExit(17)
if mode == 'fail_pre':
    print('G22_FAILURE/1 REAL_WORKFLOW_FAILED', flush=True)
    raise SystemExit(2)
if mode == 'dev_first':
    print(f'G22_FIRST_READ/1 {attempt_id} {candidate} {nonce}', flush=True)
    time.sleep(2)
    raise SystemExit(2)
if mode == 'ack_loss':
    print(f'G22_FIRST_READ/1 {attempt_id} {candidate} {nonce}', flush=True)
    sys.stdin.close()
    time.sleep(0.2)
    raise SystemExit(7)
if split == 'real-sealed':
    first_read()
    if mode != 'missing_content':
        bind_content()
if mode == 'crash_post':
    raise SystemExit(7)
if mode == 'fail_post':
    print('G22_FAILURE/1 REAL_WORKFLOW_FAILED', flush=True)
    raise SystemExit(2)
if mode == 'partial_post_hang':
    sys.stdout.buffer.write(b'synthetic-private-no-newline')
    sys.stdout.buffer.flush()
    time.sleep(5)
    raise SystemExit(7)
if mode == 'leak_post':
    print('synthetic-case-secret', flush=True)
    raise SystemExit(2)
if mode == 'stderr_post':
    print('synthetic-stderr-secret', file=sys.stderr, flush=True)
    raise SystemExit(2)

aggregate = json.loads(aggregate_json)
envelope = {
    'candidate_sha256': candidate,
    'protocol_sha256': protocol,
    'assignment_sha256': assignment,
    'selected_inventory_sha256': inventory,
    'corpus_content_sha256': content,
    'split': split,
    'aggregate': aggregate,
}
if mode == 'result_content_mismatch':
    envelope['corpus_content_sha256'] = 'a' * 64
if mode == 'malformed_post':
    envelope['case_name'] = 'synthetic-case-secret'
payload = json.dumps(envelope, separators=(',', ':'), allow_nan=False)
print('G22_RESULT/1 ' + payload, flush=True)
if mode == 'extra_post':
    print('synthetic-case-secret', flush=True)
if mode == 'flood_post':
    for _ in range(1000):
        print('synthetic-case-secret', flush=True)
"""


def _digest(label: str) -> str:
    return hashlib.sha256(label.encode("utf-8")).hexdigest()


def _descriptor() -> RealWorkflowCandidateDescriptor:
    return RealWorkflowCandidateDescriptor(
        revision="fixture-revision",
        candidate_id="fixture-candidate",
        candidate_sha256=_digest("candidate"),
        protocol_sha256=_digest("protocol"),
        evidence_policy_sha256=str(evidence_policy_reference()["sha256"]),
        assignment_sha256=_digest("assignment"),
        selected_inventory_sha256=_digest("inventory"),
    )


def _evaluation(*, failed: bool = False) -> dict[str, object]:
    return {
        "truth_cases": 1,
        "output_cases": 1,
        "completed_cases": 0 if failed else 1,
        "failed_cases": 1 if failed else 0,
        "unexpected_cases": 0,
        "integrity_failure_cases": 0,
        "truth_series": 1,
        "predicted_series": 0 if failed else 1,
        "matched_series": 0 if failed else 1,
        "truth_points": 2,
        "predicted_points": 0 if failed else 2,
        "matched_points": 0 if failed else 2,
        "unique_point_value_metrics_available": True,
        "relational_row_metrics_available": False,
        "relational_phase_metrics_available": False,
        "actual_rows": 0 if failed else 2,
        "residual_artifact_rows_from_failed_cases": 0,
        "expected_rows": None,
        "structurally_matched_rows": None,
        "correct_rows": None,
        "missing_rows": None,
        "extra_rows": None,
        "duplicate_rows": None,
        "wrong_scale_rows": None,
        "wrong_export_mode_rows": None,
        "wrong_phase_rows": None,
        "wrong_relation_rows": None,
        "unique_point_value_correct": 0 if failed else 2,
        "unique_point_value_incorrect": 0,
        "unique_point_missing": 2 if failed else 0,
        "unique_point_extra": 0,
        "unique_point_wrong_scale": 0,
        "unique_point_wrong_export_mode": 0,
        "unique_point_structural_precision": 0 if failed else 1,
        "unique_point_structural_coverage": 0 if failed else 1,
        "unique_point_value_precision": None if failed else 1,
        "unique_point_value_coverage": 0 if failed else 1,
        "matched_unique_point_value_accuracy": None if failed else 1,
        "relational_row_precision": None,
        "relational_row_coverage": None,
        "matched_relational_graph_value_accuracy": None,
        "matched_relational_phase_accuracy": None,
        "artifact_integrity_valid": True,
        "failure_kinds": {"workflow_failed": 1} if failed else {},
    }


def _aggregate(*, failed: bool = False) -> dict[str, object]:
    return {
        "schema": "graphreader.engauge-grouped-workflow-aggregate.v1",
        "image_groups": 1,
        "decoded_image_groups": 1,
        "project_files": 2,
        "workflow_invocations": 1,
        "workflow_succeeded_image_groups": 0 if failed else 1,
        "workflow_failed_image_groups": 1 if failed else 0,
        "workflow_succeeded_projects": 0 if failed else 2,
        "workflow_failed_projects": 2 if failed else 0,
        "truth_series": 1,
        "truth_points": 2,
        "coincident_cross_project_point_pairs": 0,
        "execution_failure_kinds": {"WORKFLOW_EXECUTION_FAILED": 1} if failed else {},
        "evaluation": _evaluation(failed=failed),
        "aggregate_only": True,
        "case_level_output": False,
        "truth_rows_output": False,
        "prediction_output": False,
        "paths_output": False,
        "names_output": False,
    }


def _command(mode: str, descriptor: RealWorkflowCandidateDescriptor, split: str, aggregate: dict[str, object] | None = None) -> list[str]:
    return [
        sys.executable,
        "-c",
        _CHILD,
        mode,
        descriptor.candidate_sha256,
        descriptor.protocol_sha256,
        descriptor.assignment_sha256,
        descriptor.selected_inventory_sha256,
        split,
        json.dumps(aggregate or _aggregate(), separators=(",", ":")),
    ]


def _run(
    tmp_path: Path,
    mode: str,
    split: str,
    *,
    aggregate: dict[str, object] | None = None,
    timeout: float = 5,
) -> tuple[dict[str, object], Path, Path, RealWorkflowCandidateDescriptor]:
    descriptor = _descriptor()
    ledger = tmp_path / "ledger.json"
    result = tmp_path / "result.json"
    value = run_bound_worker(
        _command(mode, descriptor, split, aggregate),
        tmp_path,
        descriptor,
        split,
        ledger if split == "real-sealed" else None,
        result,
        timeout,
    )
    return value, ledger, result, descriptor


def test_real_dev_writes_only_validated_canonical_aggregate(tmp_path: Path) -> None:
    value, ledger, result, descriptor = _run(tmp_path, "success", "real-dev")

    assert not ledger.exists()
    assert value["candidate_sha256"] == descriptor.candidate_sha256
    assert value["aggregate"]["evaluation"]["unique_point_value_precision"] == 1
    assert result.read_bytes().endswith(b"\n")
    assert result.read_bytes() == (
        json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")) + "\n"
    ).encode("utf-8")


def test_real_sealed_acknowledges_after_durable_read_and_completes(tmp_path: Path) -> None:
    value, ledger, result, _ = _run(tmp_path, "success", "real-sealed")

    document = load_ledger(ledger)
    state = document["candidates"][0]["attempts"][0]
    assert state["status"] == "completed_aggregate"
    assert state["first_read_utc"] is not None
    assert state["aggregate_result_sha256"] == hashlib.sha256(result.read_bytes()).hexdigest()
    assert state["observed_content_sha256"] == "f" * 64
    assert document["content_identity"]["sha256"] == "f" * 64
    assert document["retirement"] is None
    assert value["aggregate"]["aggregate_only"] is True


def test_acceptance_metric_failure_is_a_completed_aggregate(tmp_path: Path) -> None:
    value, ledger, _, _ = _run(
        tmp_path, "success", "real-sealed", aggregate=_aggregate(failed=True),
    )

    assert value["aggregate"]["workflow_failed_image_groups"] == 1
    assert load_ledger(ledger)["candidates"][0]["attempts"][0]["status"] == "completed_aggregate"


def test_pre_read_worker_failure_is_void(tmp_path: Path) -> None:
    descriptor = _descriptor()
    ledger = tmp_path / "ledger.json"
    result = tmp_path / "result.json"

    with pytest.raises(FrozenWorkflowProcessError, match="^REAL_WORKFLOW_FAILED$"):
        run_bound_worker(
            _command("fail_pre", descriptor, "real-sealed"), tmp_path, descriptor,
            "real-sealed", ledger, result, 5,
        )

    state = load_ledger(ledger)["candidates"][0]["attempts"][0]
    assert state["status"] == "void"
    assert state["first_read_utc"] is None
    assert not result.exists()


def test_post_read_crash_is_consumed_and_cannot_retry(tmp_path: Path) -> None:
    descriptor = _descriptor()
    ledger = tmp_path / "ledger.json"
    with pytest.raises(FrozenWorkflowProcessError, match="^REAL_WORKFLOW_RESULT_MISSING$"):
        run_bound_worker(
            _command("crash_post", descriptor, "real-sealed"), tmp_path, descriptor,
            "real-sealed", ledger, tmp_path / "result.json", 5,
        )

    state = load_ledger(ledger)["candidates"][0]["attempts"][0]
    assert state["status"] == "failed_consumed"
    with pytest.raises(RealWorkflowLedgerError, match="already consumed"):
        open_attempt(ledger, descriptor, attempt_id="retry-is-forbidden")


def test_ack_loss_after_durable_read_consumes_without_claiming_disclosure(tmp_path: Path) -> None:
    descriptor = _descriptor()
    ledger = tmp_path / "ledger.json"
    with pytest.raises(FrozenWorkflowProcessError):
        run_bound_worker(
            _command("ack_loss", descriptor, "real-sealed"), tmp_path, descriptor,
            "real-sealed", ledger, tmp_path / "result.json", 5,
        )

    document = load_ledger(ledger)
    assert document["candidates"][0]["attempts"][0]["status"] == "failed_consumed"
    assert document["retirement"] is None


def test_known_post_read_failure_frame_consumes_without_claiming_disclosure(tmp_path: Path) -> None:
    descriptor = _descriptor()
    ledger = tmp_path / "ledger.json"
    with pytest.raises(FrozenWorkflowProcessError, match="^REAL_WORKFLOW_FAILED$"):
        run_bound_worker(
            _command("fail_post", descriptor, "real-sealed"), tmp_path, descriptor,
            "real-sealed", ledger, tmp_path / "result.json", 5,
        )

    document = load_ledger(ledger)
    assert document["candidates"][0]["attempts"][0]["status"] == "failed_consumed"
    assert document["retirement"] is None


def test_content_mismatch_is_consumed_without_claiming_disclosure(tmp_path: Path) -> None:
    first_descriptor = _descriptor()
    ledger = tmp_path / "ledger.json"
    run_bound_worker(
        _command("success", first_descriptor, "real-sealed"), tmp_path, first_descriptor,
        "real-sealed", ledger, tmp_path / "first-result.json", 5,
    )
    second_descriptor = RealWorkflowCandidateDescriptor(
        revision="fixture-revision-2",
        candidate_id="fixture-candidate-2",
        candidate_sha256=_digest("candidate-2"),
        protocol_sha256=_digest("protocol-2"),
        evidence_policy_sha256=first_descriptor.evidence_policy_sha256,
        assignment_sha256=first_descriptor.assignment_sha256,
        selected_inventory_sha256=first_descriptor.selected_inventory_sha256,
    )

    with pytest.raises(FrozenWorkflowProcessError, match="^REAL_WORKFLOW_CORPUS_BINDING_FAILED$"):
        run_bound_worker(
            _command("different_content", second_descriptor, "real-sealed"),
            tmp_path, second_descriptor, "real-sealed", ledger,
            tmp_path / "second-result.json", 5,
        )

    document = load_ledger(ledger)
    assert document["content_identity"]["sha256"] == "f" * 64
    assert document["retirement"] is None
    assert document["candidates"][1]["attempts"][0]["status"] == "failed_consumed"


def test_missing_content_frame_fails_consumed_without_retirement(tmp_path: Path) -> None:
    descriptor = _descriptor()
    ledger = tmp_path / "ledger.json"
    with pytest.raises(FrozenWorkflowProcessError, match="^REAL_WORKFLOW_CORPUS_FRAME_MISSING$"):
        run_bound_worker(
            _command("missing_content", descriptor, "real-sealed"), tmp_path, descriptor,
            "real-sealed", ledger, tmp_path / "result.json", 5,
        )
    document = load_ledger(ledger)
    assert document["retirement"] is None
    assert document["candidates"][0]["attempts"][0]["status"] == "failed_consumed"


def test_result_content_identity_mismatch_retires_invalid_output(tmp_path: Path) -> None:
    descriptor = _descriptor()
    ledger = tmp_path / "ledger.json"
    with pytest.raises(FrozenWorkflowProcessError, match="^REAL_WORKFLOW_RESULT_INVALID$"):
        run_bound_worker(
            _command("result_content_mismatch", descriptor, "real-sealed"),
            tmp_path, descriptor, "real-sealed", ledger, tmp_path / "result.json", 5,
        )
    document = load_ledger(ledger)
    assert document["retirement"] is not None


def test_partial_output_released_only_during_timeout_drain_retires(tmp_path: Path) -> None:
    descriptor = _descriptor()
    ledger = tmp_path / "ledger.json"
    with pytest.raises(FrozenWorkflowProcessError, match="^REAL_WORKFLOW_PROCESS_TIMEOUT$"):
        run_bound_worker(
            _command("partial_post_hang", descriptor, "real-sealed"), tmp_path, descriptor,
            "real-sealed", ledger, tmp_path / "result.json", 0.5,
        )

    document = load_ledger(ledger)
    assert document["retirement"]["disclosures"] == ["case_identity", "pixel", "prediction", "truth"]
    assert document["candidates"][0]["attempts"][0]["status"] == "failed_consumed"
    assert "synthetic-private-no-newline" not in ledger.read_text(encoding="utf-8")


def test_event_queue_is_bounded_and_flood_does_not_deadlock(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    observed: list[int] = []
    real_queue = process_module.Queue

    def bounded_queue(*, maxsize: int):
        observed.append(maxsize)
        return real_queue(maxsize=maxsize)

    monkeypatch.setattr(process_module, "Queue", bounded_queue)
    descriptor = _descriptor()
    ledger = tmp_path / "ledger.json"
    with pytest.raises(FrozenWorkflowProcessError):
        run_bound_worker(
            _command("flood_post", descriptor, "real-sealed"), tmp_path, descriptor,
            "real-sealed", ledger, tmp_path / "result.json", 5,
        )

    assert observed == [process_module._EVENT_BUFFER_CAPACITY]
    assert load_ledger(ledger)["retirement"] is not None


@pytest.mark.parametrize("mode", ["leak_post", "stderr_post", "extra_post", "malformed_post"])
def test_unexpected_post_read_output_retires_without_persisting_raw_bytes(tmp_path: Path, mode: str) -> None:
    descriptor = _descriptor()
    ledger = tmp_path / "ledger.json"
    result = tmp_path / "result.json"

    with pytest.raises(FrozenWorkflowProcessError) as raised:
        run_bound_worker(
            _command(mode, descriptor, "real-sealed"), tmp_path, descriptor,
            "real-sealed", ledger, result, 5,
        )

    serialized = ledger.read_text(encoding="utf-8")
    document = load_ledger(ledger)
    assert document["retirement"]["disclosures"] == ["case_identity", "pixel", "prediction", "truth"]
    assert document["candidates"][0]["attempts"][0]["status"] == "failed_consumed"
    assert "synthetic-case-secret" not in serialized
    assert "synthetic-stderr-secret" not in serialized
    assert "synthetic-case-secret" not in str(raised.value)
    assert not result.exists()


def test_real_dev_rejects_first_read_without_creating_a_ledger(tmp_path: Path) -> None:
    descriptor = _descriptor()
    result = tmp_path / "result.json"
    with pytest.raises(FrozenWorkflowProcessError, match="^REAL_WORKFLOW_DEV_FIRST_READ_FORBIDDEN$"):
        run_bound_worker(
            _command("dev_first", descriptor, "real-dev"), tmp_path, descriptor,
            "real-dev", None, result, 1,
        )
    assert not result.exists()
    assert not (tmp_path / "ledger.json").exists()


def test_timeout_before_first_read_is_void(tmp_path: Path) -> None:
    descriptor = _descriptor()
    ledger = tmp_path / "ledger.json"
    with pytest.raises(FrozenWorkflowProcessError, match="^REAL_WORKFLOW_PROCESS_TIMEOUT$"):
        run_bound_worker(
            _command("timeout_pre", descriptor, "real-sealed"), tmp_path, descriptor,
            "real-sealed", ledger, tmp_path / "result.json", 0.1,
        )
    assert load_ledger(ledger)["candidates"][0]["attempts"][0]["status"] == "void"


def test_existing_result_rejects_before_process_or_ledger(tmp_path: Path) -> None:
    descriptor = _descriptor()
    result = tmp_path / "result.json"
    result.write_text("owned", encoding="utf-8")
    ledger = tmp_path / "ledger.json"

    with pytest.raises(FrozenWorkflowProcessError, match="^REAL_WORKFLOW_RESULT_PATH_INVALID$"):
        run_bound_worker(
            ["executable-that-must-not-run"], tmp_path, descriptor,
            "real-sealed", ledger, result, 1,
        )
    assert result.read_text(encoding="utf-8") == "owned"
    assert not ledger.exists()


@pytest.mark.parametrize("mutator", [
    lambda value: value.update({"case_name": "forbidden"}),
    lambda value: value["aggregate"].update({"aggregate_only": False}),
    lambda value: value["aggregate"]["evaluation"].update({"truth_points": float("nan")}),
])
def test_result_parser_rejects_case_fields_scope_drift_and_nonfinite_numbers(
    tmp_path: Path,
    mutator,
) -> None:
    descriptor = _descriptor()
    aggregate = _aggregate()
    envelope = {
        "candidate_sha256": descriptor.candidate_sha256,
        "protocol_sha256": descriptor.protocol_sha256,
        "assignment_sha256": descriptor.assignment_sha256,
        "selected_inventory_sha256": descriptor.selected_inventory_sha256,
        "corpus_content_sha256": "f" * 64,
        "split": "real-dev",
        "aggregate": aggregate,
    }
    mutator(envelope)
    payload = json.dumps(envelope, separators=(",", ":"), allow_nan=True)
    command = [
        sys.executable, "-c",
        "import sys; sys.stdout.buffer.write(b'G22_RESULT/1 '+sys.argv[1].encode()+b'\\n'); sys.stdout.buffer.flush()", payload,
    ]
    with pytest.raises(FrozenWorkflowProcessError, match="^REAL_WORKFLOW_RESULT_INVALID$"):
        run_bound_worker(
            command, tmp_path, descriptor, "real-dev", None,
            tmp_path / "result.json", 5,
        )


def test_duplicate_json_key_is_rejected(tmp_path: Path) -> None:
    descriptor = _descriptor()
    aggregate = json.dumps(_aggregate(), separators=(",", ":"))
    payload = (
        '{"candidate_sha256":"' + descriptor.candidate_sha256 + '",'
        '"candidate_sha256":"' + descriptor.candidate_sha256 + '",'
        '"protocol_sha256":"' + descriptor.protocol_sha256 + '",'
        '"assignment_sha256":"' + descriptor.assignment_sha256 + '",'
        '"selected_inventory_sha256":"' + descriptor.selected_inventory_sha256 + '",'
        '"corpus_content_sha256":"' + ('f' * 64) + '",'
        '"split":"real-dev","aggregate":' + aggregate + '}'
    )
    command = [
        sys.executable, "-c",
        "import sys; sys.stdout.buffer.write(b'G22_RESULT/1 '+sys.argv[1].encode()+b'\\n'); sys.stdout.buffer.flush()", payload,
    ]
    with pytest.raises(FrozenWorkflowProcessError, match="^REAL_WORKFLOW_RESULT_INVALID$"):
        run_bound_worker(
            command, tmp_path, descriptor, "real-dev", None,
            tmp_path / "result.json", 5,
        )
