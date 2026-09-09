# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Identity-only first-read handshake, with durable accounting before acknowledgement.

The enclosing runner must authenticate the candidate and protocol before opening
the attempt. This transport is not an authorization or a corpus reader.
"""

from __future__ import annotations

from collections.abc import Callable
import re

from ml.policy.real_workflow_ledger import (
    RealWorkflowAttempt,
    RealWorkflowLedgerError,
    record_first_sealed_read,
)


REQUEST_PREFIX = "G22_FIRST_READ/1"
ACK_PREFIX = "G22_READ_ACK/1"
_NONCE = re.compile(r"[0-9a-f]{64}\Z")


class FirstReadAccountingBridge:
    """One handshake per live evaluation process; never returns a reusable permit."""

    def __init__(self, attempt: RealWorkflowAttempt) -> None:
        self._attempt = attempt
        self._request_received = False

    def handle_request(self, line: str, write_and_flush: Callable[[str], None]) -> None:
        if self._request_received:
            raise RealWorkflowLedgerError("first-read transport request was already received")
        if not isinstance(line, str) or len(line) > 512 or "\r" in line or "\n" in line:
            raise RealWorkflowLedgerError("invalid first-read transport frame")
        parts = line.split(" ")
        if (
            len(parts) != 4
            or parts[0] != REQUEST_PREFIX
            or parts[1] != self._attempt.attempt_id
            or parts[2] != self._attempt.descriptor.candidate_sha256
            or _NONCE.fullmatch(parts[3]) is None
        ):
            raise RealWorkflowLedgerError("first-read transport identity mismatch")
        # Mark this transport spent even when persistence or acknowledgement
        # fails. A retry must obtain a new attempt through the enclosing runner.
        self._request_received = True
        record_first_sealed_read(self._attempt)
        write_and_flush(f"{ACK_PREFIX} {parts[3]}\n")
