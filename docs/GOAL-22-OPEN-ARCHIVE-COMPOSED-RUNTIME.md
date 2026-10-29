<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Open archive composed OCR runtime diagnostic

The archive fixture check previously authenticated an open synthetic archive,
while the composed OCR diagnostic read loose development files separately. The
new `--diagnose-open-ocr-archive-composed-runtime` command passes the archive
reader's authenticated sources through the same composed source evaluator
used by the sealed worker. It is a separate open-only route and does not
change the sealed worker, its handshake or its reserve path restriction.

The command requires an explicitly open, unregistered development fixture,
rejects non-dev splits and nonzero private or sealed read claims, and emits
aggregate results only. Self-tests verify these boundaries and that the
inference callback receives image bytes and a hash rather than annotation
truth. A real child-process run rejected a reserve-marked request before
candidate runtime startup.

The authenticated open archive contains 21 sources. The real child completed
with 31 panels, 333 of 453 text regions matched and 284 exact readings; its
assembled-region precision was 80.63% and recall 73.51%. All 453 truth
regions remain in the denominator. These open-development results fail the
existing OCR bars and do not establish real-data accuracy or stage approval.

The Release module build had zero warnings or errors; composed-memory and
archive self-tests passed 69 and 13 checks. The child result and input/source
identities were checked before and after execution. Local evidence is under
`artifacts/goal22-runs/open-archive-composed-child-20261029-v1` in the
resume worktree. No private or sealed input was read, and no model was approved
or packaged. The earlier confirmed-read sealed child failure remains unresolved;
success on this explicit open fixture does not establish its cause or authorize
replaying that consumed attempt.
