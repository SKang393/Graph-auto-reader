<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Classify the supplied-axis-box refusal

## Failure, scope and evidence boundary

The [fixed development affine trace](AFFINE-CALIBRATION-RECTIFICATION-20261115.md)
reached rectification and OCR but returned `AXIS_BOX_NOT_OBSERVED` from tick
measurement. Eleven owned raster controls did not reproduce that refusal.
Source review found no demonstrated coordinate mismatch: the existing error
combines missing origin ink, disconnected X/Y spans and bridge-work exhaustion.
Changing a threshold from this evidence would be unjustified.

Baseline source is `472b715f29c5ca54ef268e5d8d7d0b31681d5c1e`. Add only an
explicit opt-in native diagnostic, preserving the normal request/result,
failure and exit contracts, resource caps, pixels, tick scanners and thresholds.
Acceptance is unchanged normal results on owned controls and truthful bounded
stage reporting. This is diagnosis, not a new accuracy gate or model candidate.
There is no optimizer, weight selection, production activation or sealed run.

## Native implementation and owned validation

`native/graphreader-engine/src/main.rs` accepts exactly
`--measure-ticks --measure-ticks-diagnostics`. It runs the existing GRT1 parser
and supplied-box measurement once, preserving the first `TICKS1` or `ERR` line
and exit code. A second `TICKDIAG1` line reports the reached predicate, raw origin
channel sum/count and bounded bridge-work count. Invalid framing or unattempted
measurement retains unknown fields. Normal `--measure-ticks` emits no trace;
the older `--measure-ticks --diagnostics` combination remains invalid.

One command passed **75/75 Rust tests**, zero failures, in **0.19 s** test time,
**3.21 s** compilation and **4.77 s** wall time:

```text
cargo test --manifest-path native/graphreader-engine/Cargo.toml --target-dir artifacts/axis-box-diagnostic-check --offline --locked
```

Independent review found no material wire, threshold or coordinate change.
The subsequent standalone build exposed a test-only wrapper compiled into
production under `-D warnings`: `measure_box_ticks` was unused. That attempt
exited **101 in 0.8181335 s**, before any subprocess check. Adding `#[cfg(test)]`
to that wrapper repaired the build; one repair build passed in **2.5189816 s**.
The unit suite was not repeated for this annotation. Toolchain: rustc
`1.99.0 (b940084d7)`.

```text
cargo build --offline --locked -j1 --manifest-path native/graphreader-engine/Cargo.toml --target-dir artifacts/axis-box-diagnostic-check
```

The actual Windows wire check used three immutable RAM packets totaling
**65,615 bytes**: Gray8 observed axes/ticks, RGB white-origin refusal, and a
truncated frame. Each ran the unchanged normal worker, candidate normal worker
and candidate diagnostic worker. **All nine calls passed**, with zero failed,
timed-out or unattempted calls and empty stderr. Candidate normal output/exit
matched the unchanged worker; diagnostic output preserved that exact first
response/exit and added exactly one correct trace. The three traces were
`TICKS_MEASURED` (seed 0, one channel), `ORIGIN_NOT_INK` (seed 765, three channels),
and `UNATTEMPTED` (unknown fields). All child handles were disposed.

Wall time was **0.5884515 s**. Per-call samples ranged **51.42 to 110.79 ms**;
these are single smoke measurements, not a performance comparison. The actual
Windows inherited job cap was queried: 12 logical CPUs, rate 416, equivalent to
**0.4992 logical core**, with BelowNormal priority and a five-second child
deadline. The worker's independent self-budget remains unchanged.

| Identity | SHA-256 |
| --- | --- |
| Unchanged normal worker, preserved before/after | `2cc6f263a00544243291f2605031c4df7e147175bd345480bd0a44f92fde0b7a` |
| Diagnostic Debug executable | `922bbc579e7cb593527c3c0a9bb9bb5274f6b164625bb5f2715b1aa49bb68f84` |
| Native source after test-only build repair | `255777829383fe0b7ba387241db1f94acf499c4fed0e124a725b6d0e679ddf49` |
| Owned wire report | `07fd7201097d2a21c457b62ff3222d4d8191fa2fdec82d3ff715a83ad3981918` |

The diagnostic target and evidence occupy **36 files / 28,626,518 bytes** at
this handoff. They remain active inputs for the bounded diagnosis below, then
are retired after the result is pushed and consumers finish. No App/probe
runtime, original image, user Data or package was changed by the owned check.
Bridge-budget diagnostic labels and an actual self-budget setup failure were
not forced; that remains an explicit coverage limit.

## Recoverable selection and next bounded diagnosis

The original unique-affine selector was derived from the now-retired report
`b77aaafc2c16e39e526373888c77a0e55b228a97821551d200e83471581d0993`.
The retained outcome and source pins do not contain its first-twelve ordinal,
panel crop or dimensions. A bounded read-only check of the exact current-chat
rollout found no authoritative copy. Its owned self-test index is not the real
selector. No index or crop is guessed, and a report hash is not claimed to
reconstruct deleted data.

The next new-information pass therefore reconstructs selection once using the
unchanged geometry prefix on the same fixed first twelve development projects,
then invokes the new refusal diagnostic on the single accepted affine frame.
This prefix is recomputed and fully counted, not called cached. All existing
assignment/inventory/source locks and the 120-development/51-excluded boundary
remain. No OCR, fitting, private truth, accuracy comparison, model selection,
Apply, export or image write is needed. Require a unique accepted affine frame
and verify the complete source digest before its rectification/diagnostic call.
Retain its compact local selector as a named active diagnosis input until the
investigation closes. Owned runner/parser controls and source review precede
that one private invocation. No private diagnosis had run at that initial handoff.

The diagnostic is outside the frozen 0.4.56 preview. There are no new dependencies
or license terms; source retains Apache-2.0 notices. Owned classification and
wire validation pass. The real refusal's cause remained unresolved at that handoff.

## One completed development diagnosis

The separate probe route/pin partial uses the existing locked image-only reader,
panelizer, geometry providers and current rectifier. Source comparison against
`4fcf0b69` found no automatic geometry or panelizer change. The twenty frozen
libraries, App and two worker identities are verified before private source
access. Incomplete prefix/digest or zero/multiple affine frames refuse. A typed
outcome returns nonzero for runner failure and preserves completed observed
axis refusals. The local selector is created-new before rectification.

Probe-only builds used `dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Debug --no-restore -m:1 -nr:false -p:BuildProjectReferences=false -p:UseSharedCompilation=false -p:NuGetAudit=false`.
The initial build passed in **12.5200128 s**. Review added a missing deadline
reason and unsuccessful runner exit; the final build passed in **11.2644731 s**.
Both had zero warnings/errors. The initial/final owned self-tests passed in
**0.3202748 / 0.3320407 s**; sanitized missing-argument checks returned expected
exit 1 in **0.2264528 / 0.2675379 s**. No App/Rust rebuild or private execution
occurred in those checks. Independent review found no material issue. CI now
invokes `--real-dev-affine-tick-refusal-self-test` without models or private data.

The **single** authorized `--real-dev-affine-tick-refusal` run executed from
`2026-11-16T01:16:51.5158518Z` to `01:17:11.0003123Z`, exit 0, empty stderr,
**19.4683122 s** wrapper wall time. All **12 projects, sources, preparations and
panels** completed, with **zero failed/unattempted** entries and the expected
source digest verified. There were eight rectangles, one accepted affine frame,
three unavailable geometries and one genuine whole-image fallback. Five native
attempts returned two geometries and three unavailable outcomes. This newly
executed prefix is not cached evidence or an accuracy comparison.

One rectification and diagnostic completed. `AXIS_BOX_NOT_OBSERVED` is now
classified as **`X_SPAN_UNOBSERVED`**. The origin passed the unchanged ink
predicate; bridge work was **427**, below the existing 16,777,216 limit.
**Y-span and tick scanning were never reached**: zero returned tick counts are
not measured misses. Terminal gap, gap length and clipped-window connectivity
remain undistinguished. No production repair or threshold change follows from
this classification alone. No OCR, association, truth scoring, reserved/sealed
payload, Apply, export, image write or model selection occurred.

Stage milliseconds: source **1,060.7419**, preparation **2,312.0683**, geometry
**14,787.3907**, rectification **561.8533**, diagnostic **91.3242**. Parent wall
time was **18.8427571 s**. The verified parent cap remained **0.4992 logical
core**, with 45-second project, five-second worker and 120/125-second overall
cancellation/watchdog limits. Cumulative parent allocation **751,261,840 bytes**
is allocation churn, not resident/peak memory.

Probe SHA-256: `e0702184beb938130432d2d8593c184905a876a88f497b0888d01e6c6fd6050f`;
report: `ab503d592c58c1e1ab1ac315dfe5511bfa4b4b89ae9bea04be39a851f2cea717`;
run receipt: `c22b85d0a1632648eb95f28427911560fb2435b4db1ad7677a55a071515c3220`.
The raw 99-byte response stayed in memory, hash
`262f2eb14520e482e571943026a33b135d0a970c5a4649234265059d8ede390f`.
The **1,257-byte local selector**, hash
`689e013d039971febc72a31b50442b314bcdb7729217e061c387aca0dbea8781`, remains an
active input with the runtime manifest and report binding. Its geometry and raw
pixel samples are not committed. Further diagnosis must use this selector,
without rediscovering the case through another twelve-project geometry pass.

Owned validation and this bounded diagnosis pass. Exact-source Windows CI
[43786351072](https://github.com/SKang393/Graph-auto-reader/actions/runs/43786351072)
on `c286ed72f5a166624192c26b1bbb76cc8547b623` passed in **10 min 48 s**:
**3,180 .NET tests**, **30 expected skips**, **75 Rust tests** and **68 packaging
checks**, zero failures. The refusal selector/wire self-test also passed with
zero private reads and worker calls; release jobs were skipped. The horizontal
span's cause still needs the selected-frame property audit. No additional
package is required.

## Selected-frame property audit

The next diagnostic used only the retained, hash-pinned selector and completed
report. It verified the frozen runtime before reading one locked development
source, matched the saved source/panel/crop/frame binding, then prepared and
rectified once. It did not rerun the twelve-project geometry prefix. The earlier
whole-prefix source digest is inherited from the pinned report, not remeasured.
The scalar result is withheld unless the existing horizontal-span refusal,
origin and bridge-work count reproduce exactly.

`RealDevAffineSpanProperties.cs` mirrors the Rust gap guards, BFS traversal and
work accounting. Only for a short disconnected gap does it perform one bounded
counterfactual: same pixels, radius and along-axis limits, with perpendicular
clipping relaxed to the raster boundary. Review caught an initially broader
window and narrowed it before the first compilation or private invocation.
This search never accepts geometry, ticks or calibration. No production code,
threshold, resource cap, model or packaged runtime changed.

One probe-only build, using the command above, passed with zero warnings/errors
in **12.0030624 s**. The owned self-test passed in **0.3725908 s** with **seven
controls**: continuous ink, an interior bridge, a bridge below the baseline,
disconnected ink, a terminal gap, an overlong gap and a route that leaves the
supplied along-span. The below-baseline route connected only in the diagnostic
relaxation; the along-span excursion remained refused. Cancellation, metadata
tampering and unchanged input checks passed. No private reads or worker calls
occurred. Missing arguments produced the expected sanitized exit 1 in
**0.2493902 s**. There were no failed build or execution attempts in this batch.
CI now includes `--real-dev-affine-span-properties-self-test`.

The **single** `--real-dev-affine-span-properties` invocation completed at
`2026-11-16T01:52:01.2012163Z` through `01:52:04.6028336Z`, exit 0, empty stderr,
**3.3959477 s** wrapper wall time. All **four stages completed**, zero failed or
unattempted. The replay matched the prior refusal and **427** search steps. The
first failure was a **three-pixel disconnected gap**, below the unchanged
maximum gap length **36**; search radius was **24**. Only the bottom plot clip
truncated that local search. Relaxing perpendicular clipping while preserving
the supplied along-span still found no connection, after **51** additional
steps: **`DISCONNECTED_WITH_PERPENDICULAR_RELAXATION`**.

This result rules out clipping alone as the cause of the first gap. It does not
support a production clipping relaxation or a different ink threshold. It does
not establish why the raster is disconnected. The next bounded investigation
is whether rectification and subpixel sampling preserve thin connected strokes,
starting with owned controls and the existing raster transform implementation.
No unchanged private comparison is authorized by this result.

Source, preparation, rectification and replay took **63.1627 / 255.3109 /
674.0338 / 7.893 ms**. Method wall time **1.0007796 s** excludes preflight.
Cumulative parent allocation was **24,270,352 bytes**, not peak/resident memory;
the source/panel were **36,239 bytes** each, with **24,130 encoded / 328,770 gray
derivative bytes** in memory. The verified CPU allowance remained **0.4992
logical core**, with 30-second cancellation and a 35-second watchdog. All four
protected source `Data` files retained their lengths and SHA-256 before/after.
There were zero worker, OCR, fit, truth, reserved/sealed, image-write, Apply or
export operations. This is property diagnosis, not calibration accuracy or
native UI acceptance.

| Identity | SHA-256 |
| --- | --- |
| Probe assembly | `97c5a597d8111d78ff1a1f478ee821fd76bdd4588492f7635abdc2eb1d432ad3` |
| Owned self-test report | `ce0b58907eaaa549f8f4edb1caecf3c126ec78ddc8e7206d7a3209f4939525b5` |
| Selected-frame property report | `fda891cc12335ba5ca7fe6b42a2f7a7f91f67d11832b907b42e83bcb4e63e31c` |

The local selector, prior refusal report, compact property report and frozen
runtime pins remain named inputs to the active rectification diagnosis. Remove
them only after that consumer finishes and its concise result is pushed. The
completed raw build/self-test/run logs and redundant receipt copies become
eligible for retirement after this outcome is pushed. The 0.4.56 package remains
the sole delivered build. No new dependency or license change is introduced;
the diagnostic source retains Apache-2.0 notices. Owned and bounded diagnostic
checks pass; exact-source CI for this new probe route remains pending.
