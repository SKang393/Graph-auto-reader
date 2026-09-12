# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Cross-module accounting checks with handwritten metadata and a fake child."""

from __future__ import annotations

import base64
from dataclasses import asdict
import json
from pathlib import Path
import sys

import pytest

from ml.markers.gate_seal import canonical_json_bytes, sha256_bytes
from ml.policy.ocr_sealed_transport import (
    OcrSealedRequestIdentity, OcrSealedTransportError, run_ocr_sealed_worker,
)
from ml.policy.sealed_first_read_admission import (
    bound_reserve_metadata, complete_admission, record_positive_read_receipt, send_ack,
    SealedFirstReadAdmissionError, fail_admission, recover_admission,
)
from ml.policy.tests import test_ocr_sealed_transport as wire_fixture
from ml.policy.tests import test_sealed_first_read_admission as admission_fixture


def test_real_coordinator_and_transport_share_one_confirmed_read(tmp_path: Path) -> None:
    admission, authorization, gate, registry_path = admission_fixture._prepare(tmp_path)
    metadata = bound_reserve_metadata(admission)
    expected = OcrSealedRequestIdentity(
        attempt_id="fixture-attempt", admission_binding_sha256=admission.admission_id,
        set_id=metadata["set_id"], candidate_sha256=admission_fixture.CANDIDATE_SHA256,
        archive_sha256=metadata["archive"]["sha256"],
        archive_manifest_sha256=metadata["chain"]["archive_manifest_sha256"],
        coverage_protocol_sha256=metadata["scope"]["coverage_protocol_sha256"],
        request_sha256="c" * 64, source_count=metadata["chain"]["case_count"],
    )
    envelope = wire_fixture.envelope()
    envelope.update({key: value for key, value in asdict(expected).items() if key != "source_count"})
    envelope["aggregate"]["source_count"] = expected.source_count
    envelope["aggregate"]["panel_count"] = expected.source_count
    encoded = base64.b64encode(json.dumps(envelope).encode()).decode("ascii")
    observed = []

    def acknowledge(attempt, nonce, writer):
        assert not authorization.consumed_path.exists()
        assert not gate.consumed_path.exists()

        def check_durable_intent(frame):
            assert admission_fixture._authority(admission)["read_status"] == "possible"
            assert not authorization.consumed_path.exists()
            writer(frame)

        send_ack(admission, attempt_id=attempt, request_nonce=nonce, write_and_flush=check_durable_intent)
        observed.append("ack")

    def confirm(attempt, candidate, nonce):
        receipt = record_positive_read_receipt(admission, attempt_id=attempt,
            candidate_sha256=candidate, request_nonce=nonce)
        assert receipt.read_status == "confirmed"
        assert authorization.consumed_path.is_file() and gate.consumed_path.is_file()
        observed.append("positive")

    result = run_ocr_sealed_worker(
        [sys.executable, "-u", "-c", wire_fixture.FAKE_CHILD, "valid",
            expected.attempt_id, expected.candidate_sha256, wire_fixture.NONCE, encoded],
        tmp_path, expected, acknowledge, confirm, timeout_seconds=5,
    )
    assert observed == ["ack", "positive"]
    assert result.envelope == envelope
    receipt = complete_admission(admission,
        aggregate_result_sha256=sha256_bytes(canonical_json_bytes(result.envelope)))
    assert (receipt.status, receipt.read_status) == ("completed", "confirmed")
    registry = json.loads(registry_path.read_text())
    assert sum(len(item["uses"]) for item in registry["sets"]) == 1
    with pytest.raises(SealedFirstReadAdmissionError):
        send_ack(admission, attempt_id="repeat", request_nonce="f" * 64,
            write_and_flush=lambda _frame: pytest.fail("completed admission must not send another ACK"))


@pytest.mark.parametrize(("scenario", "status", "read_status", "uses"), [
    ("wrong_first_identity", "void", "none", 0),
    ("missing_receipt", "possible_read", "possible", 0),
    ("duplicate_result", "failed", "confirmed", 1),
])
def test_transport_failure_recovers_only_proven_accounting(
    tmp_path: Path, scenario: str, status: str, read_status: str, uses: int,
) -> None:
    admission, authorization, gate, registry_path = admission_fixture._prepare(tmp_path)
    metadata = bound_reserve_metadata(admission)
    expected = OcrSealedRequestIdentity(
        attempt_id="fixture-attempt", admission_binding_sha256=admission.admission_id,
        set_id=metadata["set_id"], candidate_sha256=admission_fixture.CANDIDATE_SHA256,
        archive_sha256=metadata["archive"]["sha256"],
        archive_manifest_sha256=metadata["chain"]["archive_manifest_sha256"],
        coverage_protocol_sha256=metadata["scope"]["coverage_protocol_sha256"],
        request_sha256="c" * 64, source_count=metadata["chain"]["case_count"],
    )
    envelope = wire_fixture.envelope()
    envelope.update({key: value for key, value in asdict(expected).items() if key != "source_count"})
    envelope["aggregate"]["source_count"] = expected.source_count
    envelope["aggregate"]["panel_count"] = expected.source_count
    encoded = base64.b64encode(json.dumps(envelope).encode()).decode("ascii")

    with pytest.raises(OcrSealedTransportError) as failure:
        run_ocr_sealed_worker(
            [sys.executable, "-u", "-c", wire_fixture.FAKE_CHILD, scenario,
                expected.attempt_id, expected.candidate_sha256, wire_fixture.NONCE, encoded],
            tmp_path, expected,
            lambda attempt, nonce, writer: send_ack(admission, attempt_id=attempt,
                request_nonce=nonce, write_and_flush=writer),
            lambda attempt, candidate, nonce: record_positive_read_receipt(admission,
                attempt_id=attempt, candidate_sha256=candidate, request_nonce=nonce),
            timeout_seconds=5,
        )
    if read_status == "confirmed":
        fail_admission(admission, failure.value)
    recovered = recover_admission(registry_path, tmp_path,
        admission_id=admission.admission_id, training_authorization=authorization, gate_seal=gate)
    record = admission_fixture._authority(recovered)
    assert (record["status"], record["read_status"]) == (status, read_status)
    assert authorization.consumed_path.exists() == (read_status == "confirmed")
    assert gate.consumed_path.exists() == (read_status == "confirmed")
    registry = json.loads(registry_path.read_text())
    assert sum(len(item["uses"]) for item in registry["sets"]) == uses
    with pytest.raises(SealedFirstReadAdmissionError):
        bound_reserve_metadata(recovered)
