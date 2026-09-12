# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import base64
from copy import deepcopy
import hashlib
import json
from pathlib import Path
import sys

import pytest

from ml.policy.ocr_sealed_transport import (
    ACCEPTANCE_SCOPE,
    COVERAGE_PROTOCOL_SHA256,
    METRIC_REFERENCE_SHA256,
    OcrSealedRequestIdentity,
    OcrSealedTransportError,
    RESULT_SCHEMA,
    run_ocr_sealed_worker,
    validate_result_envelope,
)


HASH = "0123456789abcdef" * 4
NONCE = "abcdef0123456789" * 4
ROLES = ("annotation", "axistitle", "legendtext", "participant", "phaseheading", "xtick", "ytick")

FAKE_CHILD = r"""
import base64
import sys
import time

scenario, attempt, candidate, nonce, encoded = sys.argv[1:]
result = base64.b64decode(encoded).decode("utf-8")
first = f"G22_FIRST_READ/1 {attempt} {candidate} {nonce}"
if scenario == "wrong_first_identity":
    first = f"G22_FIRST_READ/1 wrong-attempt {candidate} {nonce}"
print(first, flush=True)
if scenario == "duplicate_request":
    print(first, flush=True)
ack = sys.stdin.readline().strip()
if ack != f"G22_READ_ACK/1 {nonce}":
    raise SystemExit(7)
if scenario == "timeout":
    time.sleep(5)
    raise SystemExit(8)
if scenario == "stderr_flood":
    sys.stderr.write("x" * (1024 * 1024))
    sys.stderr.flush()
if scenario == "unsafe_output":
    print("secret-case-output", flush=True)
    raise SystemExit(9)
if scenario != "missing_receipt":
    receipt_nonce = ("0" * 64) if scenario == "bad_receipt" else nonce
    print(f"G22_POSITIVE_READ/1 {attempt} {candidate} {receipt_nonce}", flush=True)
print(result, flush=True)
if scenario == "duplicate_result":
    print(result, flush=True)
"""


def identity() -> OcrSealedRequestIdentity:
    return OcrSealedRequestIdentity(
        attempt_id="fixture-attempt",
        admission_binding_sha256=HASH,
        set_id=HASH,
        candidate_sha256=HASH,
        archive_sha256=HASH,
        archive_manifest_sha256=HASH,
        coverage_protocol_sha256=COVERAGE_PROTOCOL_SHA256,
        request_sha256=HASH,
        source_count=2,
    )


def geometry(truth: int, predicted: int, matched: int) -> dict[str, object]:
    return {
        "truth_region_count": truth,
        "predicted_region_count": predicted,
        "true_positives": matched,
        "false_positives": predicted - matched,
        "false_negatives": truth - matched,
        "precision": matched / max(1, predicted),
        "recall": matched / max(1, truth),
        "intersection_over_union_minimum": 0.5,
    }


def envelope() -> dict[str, object]:
    expected = identity()
    role_truth = {role: 1 for role in ROLES}
    role_truth["phaseheading"] = 2
    role_correct = {role: 1 for role in ROLES}
    role_correct["annotation"] = 0
    return {
        "schema": RESULT_SCHEMA,
        "status": "completed",
        "split": "sealed",
        "acceptance_scope": ACCEPTANCE_SCOPE,
        "attempt_id": expected.attempt_id,
        "admission_binding_sha256": expected.admission_binding_sha256,
        "set_id": expected.set_id,
        "candidate_sha256": expected.candidate_sha256,
        "archive_sha256": expected.archive_sha256,
        "archive_manifest_sha256": expected.archive_manifest_sha256,
        "coverage_protocol_sha256": expected.coverage_protocol_sha256,
        "request_sha256": expected.request_sha256,
        "metric_reference_sha256": METRIC_REFERENCE_SHA256,
        "execution_provider": "CPUExecutionProvider",
        "cpu_threads": 1,
        "graph_optimization": "ORT_DISABLE_ALL",
        "model_inference": True,
        "case_output": False,
        "production_approved": False,
        "elapsed_ms": 12.5,
        "aggregate": {
            "source_count": 2,
            "panel_count": 2,
            "metrics": {
                "raw_detector_geometry": geometry(8, 8, 7),
                "successfully_recognized_region_geometry": geometry(8, 7, 7),
                "recognition_failures": {"raw_regions_without_successful_recognition": 1},
                "full_ocr_metrics": {
                    "truth_region_count": 8,
                    "predicted_region_count": 7,
                    "geometry_matched_region_count": 7,
                    "geometry_false_positive_count": 0,
                    "geometry_false_negative_count": 1,
                    "recognition_exact_count": 6,
                    "recognition_exact_accuracy": 6 / 8,
                    "truth_character_count": 56,
                    "matched_pair_edit_count": 2,
                    "unmatched_truth_deletion_edit_count": 7,
                    "unmatched_prediction_insertion_edit_count": 0,
                    "character_error_count": 9,
                    "character_error_rate": 9 / 56,
                    "role_correct_count": 6,
                    "role_accuracy": 6 / 8,
                    "by_expected_runtime_role": {
                        role: {
                            "truth_count": role_truth[role],
                            "correct_count": role_correct[role],
                            "accuracy": role_correct[role] / role_truth[role],
                        }
                        for role in ROLES
                    },
                    "intersection_over_union_minimum": 0.5,
                },
            },
        },
    }


def command(scenario: str, value: str | None = None) -> list[str]:
    payload = value if value is not None else json.dumps(envelope(), separators=(",", ":"))
    encoded = base64.b64encode(payload.encode("utf-8")).decode("ascii")
    return [sys.executable, "-u", "-c", FAKE_CHILD, scenario, identity().attempt_id, HASH, NONCE, encoded]


def callbacks() -> tuple[list[tuple[object, ...]], object, object]:
    events: list[tuple[object, ...]] = []

    def send_ack(attempt: str, nonce: str, write_and_flush) -> None:
        events.append(("ack", attempt, nonce))
        write_and_flush(f"G22_READ_ACK/1 {nonce}\n")

    def positive(attempt: str, candidate: str, nonce: str) -> None:
        events.append(("positive", attempt, candidate, nonce))

    return events, send_ack, positive


def run(tmp_path: Path, scenario: str = "valid", value: str | None = None):
    events, send_ack, positive = callbacks()
    result = run_ocr_sealed_worker(
        command(scenario, value), tmp_path, identity(), send_ack, positive, timeout_seconds=2,
    )
    return result, events


def expect_error(tmp_path: Path, scenario: str, code: str, value: str | None = None) -> None:
    events, send_ack, positive = callbacks()
    with pytest.raises(OcrSealedTransportError) as caught:
        run_ocr_sealed_worker(
            command(scenario, value), tmp_path, identity(), send_ack, positive, timeout_seconds=1,
        )
    assert str(caught.value) == code
    assert "secret-case-output" not in str(caught.value)


def test_valid_exchange_and_stderr_flood(tmp_path: Path) -> None:
    result, events = run(tmp_path)
    assert result.envelope == envelope()
    assert result.stderr_byte_count == 0
    assert events == [
        ("ack", identity().attempt_id, NONCE),
        ("positive", identity().attempt_id, HASH, NONCE),
    ]
    flooded, _ = run(tmp_path, "stderr_flood")
    assert flooded.stderr_byte_count == 1024 * 1024
    assert flooded.stderr_sha256 == hashlib.sha256(b"x" * (1024 * 1024)).hexdigest()


@pytest.mark.parametrize(
    ("scenario", "code"),
    [
        ("bad_receipt", "OCR_SEALED_TRANSPORT_POSITIVE_READ_INVALID"),
        ("missing_receipt", "OCR_SEALED_TRANSPORT_POSITIVE_READ_MISSING"),
        ("duplicate_request", "OCR_SEALED_TRANSPORT_PROTOCOL_INVALID"),
        ("duplicate_result", "OCR_SEALED_TRANSPORT_PROTOCOL_INVALID"),
        ("wrong_first_identity", "OCR_SEALED_TRANSPORT_PROTOCOL_INVALID"),
        ("unsafe_output", "OCR_SEALED_TRANSPORT_UNSAFE_OUTPUT"),
        ("timeout", "OCR_SEALED_TRANSPORT_TIMEOUT"),
    ],
)
def test_protocol_failures_are_sanitized(tmp_path: Path, scenario: str, code: str) -> None:
    expect_error(tmp_path, scenario, code)


def test_pre_ack_callback_failure_kills_child(tmp_path: Path) -> None:
    def fail(_attempt: str, _nonce: str, _write) -> None:
        raise RuntimeError("private callback detail")

    with pytest.raises(OcrSealedTransportError) as caught:
        run_ocr_sealed_worker(
            command("valid"), tmp_path, identity(), fail, lambda *_: None, timeout_seconds=1,
        )
    assert str(caught.value) == "OCR_SEALED_TRANSPORT_ACK_FAILED"
    assert "private callback detail" not in str(caught.value)


def test_ack_writer_requires_the_exact_single_frame(tmp_path: Path) -> None:
    def wrong_ack(_attempt: str, _nonce: str, write_and_flush) -> None:
        write_and_flush(f"G22_READ_ACK/1 {'0' * 64}\n")

    with pytest.raises(OcrSealedTransportError) as caught:
        run_ocr_sealed_worker(
            command("valid"), tmp_path, identity(), wrong_ack, lambda *_: None, timeout_seconds=1,
        )
    assert str(caught.value) == "OCR_SEALED_TRANSPORT_ACK_FAILED"


def test_duplicate_json_keys_and_wrong_result_identity_are_rejected(tmp_path: Path) -> None:
    duplicate = json.dumps(envelope(), separators=(",", ":"))
    duplicate = duplicate[:-1] + ',"schema":"duplicate"}'
    expect_error(tmp_path, "valid", "OCR_SEALED_TRANSPORT_RESULT_INVALID", duplicate)
    wrong = envelope()
    wrong["candidate_sha256"] = "0" * 64
    expect_error(
        tmp_path, "valid", "OCR_SEALED_TRANSPORT_RESULT_INVALID",
        json.dumps(wrong, separators=(",", ":")),
    )


@pytest.mark.parametrize(
    "mutation",
    [
        lambda value: value["aggregate"].update({"source_count": 1}),
        lambda value: value["aggregate"]["metrics"]["raw_detector_geometry"].update({"precision": float("nan")}),
        lambda value: value.update({"elapsed_ms": 10**10000}),
        lambda value: value["aggregate"]["metrics"]["raw_detector_geometry"].update({"truth_region_count": 2**63}),
        lambda value: value["aggregate"]["metrics"]["raw_detector_geometry"].update({"false_positives": 2}),
        lambda value: value["aggregate"]["metrics"]["full_ocr_metrics"].update({"character_error_rate": 0.0}),
        lambda value: value["aggregate"]["metrics"]["full_ocr_metrics"]["by_expected_runtime_role"].pop("xtick"),
        lambda value: value["aggregate"]["metrics"]["full_ocr_metrics"].update({"case_text": "secret"}),
    ],
)
def test_pure_validator_rejects_incomplete_or_nonaggregate_metrics(mutation) -> None:
    value = deepcopy(envelope())
    mutation(value)
    with pytest.raises(OcrSealedTransportError) as caught:
        validate_result_envelope(value, identity())
    assert str(caught.value) == "OCR_SEALED_TRANSPORT_RESULT_INVALID"
