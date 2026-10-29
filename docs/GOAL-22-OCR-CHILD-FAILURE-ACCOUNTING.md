<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR child failure after confirmed read

The earlier composed OCR sealed attempt ended with a child-process failure
after a confirmed read and produced no accuracy aggregate. Its sealed use
remains counted. The cause of that particular child exit is still unknown.

The transport and admission regression fixtures now exercise this exact
accounting boundary: a synthetic child completes the authenticated first-read
handshake, sends a positive-read receipt, emits the worker's constant failure
diagnostic and exits nonzero without a result. Both legacy and composed
transports return the sanitized `OCR_SEALED_TRANSPORT_CHILD_FAILED` code.
The coordinator retains confirmed-read status, consumes the authorized
candidate and counts the reserve use once; it does not permit replay.

All 64 tests in the three transport and admission test files pass. These are
synthetic subprocess fixtures, not a reproduction of the original OCR model
failure. Previously retained open-archive and import/axis diagnostics remain
separate; no private or sealed input, model inference, model change, approval,
package or release was used in this check.
