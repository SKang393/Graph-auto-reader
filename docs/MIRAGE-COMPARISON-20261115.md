<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Fixed Mirage development comparison

## Current assignment

The maintainer reopened Mirage testing on 2026-11-15, including inspection of
the invalid `-nan(ind)` response and comparison with the unchanged baseline on
supplied development examples and owned controls. Reuse the isolated adapter
being prepared by Dot worker `/root/usage_reset`; do not duplicate or overwrite
it. This is one fixed diagnostic comparison, not permission to search models,
thresholds or private answers. No paid credits are authorized. The current task
stops at 4% weekly usage remaining; the earlier reset instruction is superseded.

Source checkpoint `b6f14f5d8b34b7b3979f61d36332967cf146f222` preserves the
completed orientation repair and its 85 OCR / 200 App tests. The comparison
baseline is the unchanged retained **0.4.51** package from source
`e54917917d1acdc3aed2c556f3d2faf0bcbff253`, not newly compiled source. Its exact
runtime pins remain in
[`RealDevPrepared451CalibrationCheck.cs`](../tools/GraphReader.CalibrationOcrProbe/RealDevPrepared451CalibrationCheck.cs)
and the [delivery record](INTERNAL-PREVIEW-451-VERIFICATION-20261114.md).

## Single paired pass and accounting

The supplied corpus already has a frozen 120-project development selection and
51 sealed projects. Use only the locked development selector; any narrower
supplied-example list must be identified and frozen before payload reads.
Preserve sealed payloads and original files. Do not inspect manual answers to
choose cases, panels, parameters or candidates. No optimizer or training runs.

The earlier 451 aggregate has no retained per-panel predictions and cannot
stand in for a matched comparison. One paired forward pass may feed identical
in-memory prepared-panel pixels to the frozen baseline and Mirage adapter.
Account for every selected project and every returned panel, including genuine
no-panel fallbacks, unsupported inputs, errors, timeouts and unattempted work.
Retain separate counts for preparation, automatic geometry, literal OCR,
numeric-label/tick association and complete review proposals. A successful OCR
call alone is not successful calibration. No scientific Apply is automatic.

Distinguish these control paths:

| Path | Geometry source | What it can establish |
| --- | --- | --- |
| Existing owned corner control | Explicit fixture axes and ticks | OCR and label association only |
| Owned full workflow | Actual panel preparation and Rust-first axis/tick detection | Automatic proposal availability, followed by simulated review |
| Supplied development examples | Actual preparation and automatic geometry, with all failures counted | Paired diagnostic coverage and timing; no independent accuracy or production approval |

If Mirage replaces only text detection/reading, say so. Reusing baseline axis
geometry isolates that stage; it does not demonstrate a new axis detector.
Map Mirage's image-relative 0-1000 boxes to the actual supplied crop pixels,
then add the reversible crop-to-original transform exactly once.

Record model/runtime hashes, selected options, CPU/GPU backend, verified resource
cap, cold initialization, per-stage timing and failures. Compare like workloads
under the same resource budget; do not compare phone vendor numbers with this
Windows host. Report missing stages instead of discarding failed examples.
Use aggregate-only committable development output. Never commit source pixels,
private names, truth rows or per-case predictions.

## Mirage interface prerequisites

The [previous probe](CACTUS-WINDOWS-PROBE-20261113.md) pinned Cactus 4.1.0 and
Mirage, but its completed downloads and raw response were retired after the
record was pushed. It reported invalid JSON at a confidence field, not buffer
exhaustion. Reuse Dot's verified assets and response evidence when available.

The [pinned Python interface](https://github.com/cactus-compute/cactus/blob/bafcf7348607c89f30ff9fde8e59d20b76baa86f/cactus/agent/mirage.py)
passes packed RGB and default `{}` options to `cactus_describe`, then parses its
NUL-terminated response. A nonnegative status is not proof of valid JSON and
must not be interpreted as a verified response byte count. The wrapper exposes
`sections`; a fixed text-only owned control can use `{"sections":["text"]}`
without changing confidence thresholds. This is not an exposed recognition-only
crop API; externally cropped RGB still invokes ordinary `describe`.

Preserve status, response hash, parser offset and invalid-token category.
Reject nonfinite coordinates and scores, including NaN/Infinity accepted by
permissive JSON parsers. Never turn invalid confidence into a valid-looking zero
or one. Any diagnostic recovery must remain explicitly invalid/review-only and
separate from strict successful inference counts. Do not repair bad output by
changing private-data parameters or quietly dropping failed rows.

Before a development pass, verify the adapter's exact runtime/model/license
identities, bounded response buffer, cancellation/deadline, resource cap,
telemetry disabled, and owned controls. No new runtime is added to the product.

## Coordination and retention

The maintainer transferred the adapter to
`C:/Users/Sungwoo/mirage-handoff-20261115/files`. The lead read `HANDOFF.md`
first, then all supplied source and response files. The 10,378-byte archive
SHA-256 was independently verified:
`10bdf93bcb38853881b10e474a3c1c9dcc80bb322f1e0d140023cc6b7968b709`.
The eight extracted files total 23,263 bytes. The 600-by-400 owned PNG has SHA-256
`6f1e161799715d031cd8391263df264030dec2a1baecf3b56fff8ea146c00ca2`.
Keep these supplied originals unchanged. Local diagnostic use is explicitly
authorized by the transfer; no redistribution of the handoff helpers is claimed.

The handoff reports a Linux CPU run, not Windows or paired development
validation. Its raw response has six planted axis strings, two spurious text
rows and a false wrench narrative. Its text entries use `string`, `bbox` and
`score`; the diagnostic adapter repairs only a `confidence` key. Inspect the
fresh Windows key and box convention before adapting that boundary. Never use
the scene narrative to supply scientific values or axes.

The comparison work is split between a Windows runtime/adapter diagnostic and
the existing C# frozen-baseline orchestrator. Reuse the handoff rather than
creating a competing adapter. The supplied adapter's five local tests passed.
One Windows default-options call on the unchanged supplied PNG completed with
strict JSON, eight text rows, no repair and no nonfinite token. It retained the
six planted strings and two spurious rows; describe time was 13,825.0113 ms
under the verified half-core cap. Raw response SHA-256:
`75e16a632d5845aca8e32010b7a5e39882a3467989e896650663570988581592`.
This smaller input did not reproduce the historical invalid token. The fixed
text-only owned check and paired development comparison are not yet completed.

Temporarily retain `artifacts/corner-tick-worker-451-check` (42,716,164 bytes)
for the named unchanged-baseline comparison consumer. Its prepared retirement
has not been executed. The 451 package, its model assets, shared Data and native
QA inputs remain unchanged. Retire the probe copies after comparison consumers
finish and the concise outcome is pushed. New outputs remain bounded in one
ignored comparison directory; do not duplicate models or source images.
