<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Fixed Mirage development comparison

## Diagnostic scope

This fixed diagnostic compares malformed responses and unchanged baseline behavior on development examples and owned controls.

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

Freeze text-only Mirage options with no confidence override. Allow 45 seconds
per selected project and 900 seconds overall, with a 905-second process
watchdog. Do not replay timed-out cases. Preserve unattempted counts when the
deadline or a failed worker ends the pass. Report native Mirage time separately
from its complete decode/transport/OCR stage wall time; baseline wall timing
includes its worker call. No sealed run is authorized or consumed.

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

## Completed comparison and decision

The single fixed pass completed all 120 projects (118 embedded images) and
127 effective panels: 112 prepared panels plus 15 genuine no-panel fallbacks.
Both readers received the same 50 panels with automatic geometry. Geometry
failed on 77 panels: 73 ambiguous and four native-unavailable results. No
project/panel was unsupported, timed out, skipped or left unattempted.

| Measure | Unchanged 451 | Mirage |
| --- | ---: | ---: |
| OCR responses | 50 | 50 |
| Literal numeric rows | 367 | 574 |
| Associated label/tick pairs | 337 | 499 |
| Complete review proposals | 0 | 2 |
| Complete proposals using estimated positions | 0 | 1 |
| OCR-stage median | 2.501 s | 10.708 s |
| OCR-stage p95 | 6.337 s | 12.430 s |

All 50 Mirage responses were strict-valid, with zero repairs or unknown scores.
The score-recovery adapter contributed no successful inference. Process wall
was 706.6791657 seconds, with both readers capped to 0.4992 logical cores.
Two projects had a Mirage-only complete draft; 118 had neither. Counts describe
proposal availability, not correctness against manual answers. One complete
Mirage draft used estimated positions and must not be presented as wholly
measured calibration. The sealed split was not read.

**Decision:** retain the current reader and Rust/WPF architecture. Mirage's
two complete drafts across 120 projects and approximately 4.3-times longer
median OCR time do not establish a useful default replacement on this host.
Do not add this runtime/model to the application, promote it, or repeat the
private comparison to tune it. Continue the already tested device-label,
marker-history and orientation fixes in one meaningful integrated preview,
then finish the remaining native review interactions. Geometry and reliable
label association remain product limitations; no new training campaign follows
from this diagnostic.

The [pipeline outcome](MIRAGE-PAIRED-PIPELINE-20261115.md) records full failure
counts, timing definitions, fixed identities and commands. Aggregate result
SHA-256: `c4efe0547756a3725f0f03d86b208329784603a21c93bb1d18b873116895d50b`.

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

## Adapter evidence

The 10,378-byte adapter archive has SHA-256
`10bdf93bcb38853881b10e474a3c1c9dcc80bb322f1e0d140023cc6b7968b709`.
The eight extracted files total 23,263 bytes. The 600-by-400 owned PNG has SHA-256
`6f1e161799715d031cd8391263df264030dec2a1baecf3b56fff8ea146c00ca2`.
The helper redistribution license is not established.

The handoff reports a Linux CPU run, not Windows or paired development
validation. Its raw response has six planted axis strings, two spurious text
rows and a false wrench narrative. Its text entries use `string`, `bbox` and
`score`; the diagnostic adapter repairs only a `confidence` key. Inspect the
fresh Windows key and box convention before adapting that boundary. Never use
the scene narrative to supply scientific values or axes.

The supplied adapter's five local tests passed.
One Windows default-options call on the unchanged supplied PNG completed with
strict JSON, eight text rows, no repair and no nonfinite token. It retained the
six planted strings and two spurious rows; describe time was 13,825.0113 ms
under the verified half-core cap. Raw response SHA-256:
`75e16a632d5845aca8e32010b7a5e39882a3467989e896650663570988581592`.
The fixed text-only call also returned the same eight finite-score rows, with
no repairs, in 13,071.7198 ms native describe time. These were separate cold
processes, not a warm speed comparison.

The exact original 1200-by-1700 owned input was then tested once with unchanged
default `{}` options. It reproduced the historical response byte-for-byte:
7,099 bytes, status 326, SHA-256
`010bd2762cb8688acf9801168837f781bd0033fced3dc160caac18c645d69210`.
Strict JSON and the unchanged supplied adapter both failed at offset 4,944.
The actual invalid field is a text row's `score: -nan(ind)`, not `confidence`.
The call took 13,184.9531 ms native / 14.0977099 seconds process wall with the
same verified 0.4992-core cap. Diagnostic score recovery must retain null
confidence and strict failure; it cannot count as successful inference.
The [Windows record](MIRAGE-WINDOWS-ADAPTER-20261115.md) carries exact source
identities and parser evidence. Owned transport, mapping, full-pipeline and the
single paired development pass are now completed as recorded above.

Historical result hashes identify completed-run outputs that are no longer retained; this record does not imply those outputs remain available.
