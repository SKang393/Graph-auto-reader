<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR workflow context integration

The complete workflow now passes detected phase boundaries to OCR in original
panel coordinates. Previously, the diagnostic supplied these boundaries but the
workflow omitted them, skipping inside-plot and heading word assembly.

The frozen candidate factory can explicitly load the repaired composition,
`original-db-head-tick-and-header-lanes-v2`, with the required
`original_pixel_context_regions` geometry identity. Other composition/geometry
pairings are rejected. Existing production approval checks remain in force.
The public OCR overload remains compatible with existing adapters.

Validation passes all 332 application tests without skips, including candidate
and approved-path geometry forwarding, immutable sorted boundaries, and rejection
of invalid coordinates. The Windows x64 tool build has no warnings or errors and
takes 20.88 seconds. Three runner self-tests pass, covering binding integrity,
grouped import through actual CSV writing with fictitious adapters, and the
private runner's fail-closed admission checks. No private data is read.

Earlier compiler and launch errors are retained. The native binding check
correctly rejected a non-RID build without top-level native libraries; rebuilding
for Windows x64 fixed the invocation without weakening the check.

This is mechanical integration evidence. It does not establish model accuracy,
real acceptance, or approval. No inference, training, model revision, packaged
build, release, or approval switch occurs in this repair. Next is actual complete
synthetic workflow inference with the frozen models and original source PNGs,
without supplied axis or panel answers. All four Goal 22 outcomes remain open.

The [evidence record](GOAL-22-OCR-WORKFLOW-CONTEXT.json) binds the tested source,
test results, build, and runner checks by checksum.
