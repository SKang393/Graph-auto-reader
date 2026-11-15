<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native review geometry and exact-box contrast

## Failure and bounded diagnosis

The [cached binary-property diagnosis](CACHED-AXIS-PIXEL-PROPERTIES-20261115.md)
showed few pixels below 128 along the rejected native box. Those fields alone
could not distinguish gray ink from white background. A distinct image-only
follow-up added raw Gray8 intensities and complete per-baseline histograms at
the same fixed offsets. It made no axis/OCR calls, selected no new box, and
read no calibration or point values.

The single intensity run started `2026-11-15T17:49:02.2435770Z`, finished
`2026-11-15T17:49:03.5864869Z`, exit 0, empty stderr, wall **1.3381565 seconds**.
All **12/12** fixed development sources completed digest verification, with
zero failed or unattempted sources. The one cached failure completed its crop
and property work, with zero failures. The frozen assignment, inventory and
source digest match the previous report. All 51 independent cases remained
excluded; axis, OCR, truth scoring, image writes and sealed payload reads were
zero. No baseline/model comparison, training, Apply or export occurred.

Image-property aggregates for the affected proposal:

- Origin and horizontal endpoint: Gray8 **134**; vertical endpoint: **236**.
- Horizontal baseline: **505/507** pixels at 134, one at 0 and one at 91.
  The adjacent row above had the same distribution.
- Vertical baseline: **221/222** pixels at 134 and one at 236. The adjacent
  column inside had 221 pixels at 134 and one at 255.

This establishes a contrast mismatch with the fixed Rust `<128` predicate.
It also identifies a pale endpoint that the contrast change below intentionally
does not accommodate. It does not establish calibration accuracy or authorize
a threshold search against private answers.

## Owned cross-stage evidence before the change

The five fixed controls used the actual native worker's returned geometry as
input to the actual Rust exact-box service on the same encoded image. No
supplied fixture geometry entered detection or measurement. There was no OCR.

| Owned control | Native geometry | Previous exact-box result |
| --- | --- | --- |
| Black axes/ticks and black marker | Exact box | X `30,70,110`; Y `20,50,80` |
| Gray-192 axes/ticks and black marker | Same exact box | `AXIS_BOX_NOT_OBSERVED` |
| Gray-128 axes without ticks | Exact box | `AXIS_BOX_NOT_OBSERVED` |
| Blank | No line evidence | Not attempted |
| Disconnected black corner | Box proposed; origin 255 | `AXIS_BOX_NOT_OBSERVED` |

All five native calls completed; four measurement calls completed. There were
zero stage exceptions. Wall time was **3.697 seconds**, exit 0, empty stderr.
Native times were 622.098, 642.7496, 632.9563, 531.5923 and 600.2653 ms in table
order. Measurement times were 173.0917, 45.0719, 49.6754 and 45.9632 ms for
attempted cases. These bounded cold-worker timings are not throughput claims.

The preceding probe-only build passed with zero warnings/errors in 7.05 seconds
of MSBuild time / 8.003 seconds wall. Its owned property self-test passed in
0.492 seconds with no private reads. All 20 runtime files and the reviewed
OpenCV DLL were verified. The executed binary emitted dense 256-bin histograms;
a subsequent source-only formatting change switched them to lossless sparse
bins. Extraction, selection, offsets and measured values did not change. The
source-file hash captured at private execution reflects that formatting update,
not the dense binary's source snapshot. The binary/report hashes below identify
what actually ran; no private rerun was made to change serialization.

## Implementation and owned checks

Only `measure_box_ticks`, the `GRT1` path, changes its ink admission. It borrows
the existing native line provider's strong-ink cutoff of **200**, which predates
this diagnosis. After validating dimensions, channels, exact buffer length and
box bounds, it creates a mask of at most one byte per input pixel, **16 MiB**.
Gray8 `<200`, or the equivalent RGB arithmetic mean, becomes observed ink.
Original bytes and exact box coordinates remain unchanged.

All existing connected-span, bridge-work, tick-length, width, adjacency, spacing
and absent-versus-rejected rules consume that mask. No pixel position or numeric
label is inferred. Global Rust proposal and review paths retain `<128` and their
previous ambiguity behavior. This is scoped to already warned native review
geometry, not a uniqueness claim or automatic calibration approval.

The complete Rust suite passed **72/72**, zero failures, in **3.548 seconds**
wall, including 3.06 seconds compilation and 0.18 seconds test execution. New
owned controls cover black/gray-128/gray-192, RGB, immutable input, isolated
measurement of two gray plots, unchanged global refusal, blank/uniform fields,
no-tick glyph outlines and disconnected corners. Existing long/grid/adjacent
stroke guards passed. Independent review found no material blocker.

The release-profile test-worker compile passed in **3.2821373 seconds** wall
(3.17 seconds compilation). This updates the existing unpackaged test worker;
it is not a packaged application build or a version increment.

## Actual worker verification after the change

The final probe compile passed with zero warnings/errors in **7.34 seconds**
MSBuild / **8.0141525 seconds** wall. All 20 pinned libraries were verified and
restaged. The sparse-format owned self-test passed, exit 0, in **0.4095569
seconds**; it made no private read. All tool source was frozen for this build.

One actual worker run from `2026-11-15T17:55:48.6367104Z` to
`2026-11-15T17:55:55.8866414Z` completed **9/9 native calls** and **8/8 attempted
measurement calls**, with zero stage exceptions, exit 0 and empty stderr. Wall
time was **7.2492699 seconds**. The first five image hashes and returned native
geometries matched the before run exactly:

- Black control retained all six tick positions.
- Gray-192 control now returned the same six positions, previously unavailable.
- Gray-128 no-tick control returned empty evidence, with no fabricated ticks.
- Blank remained a native failure; measurement was not attempted.
- Disconnected corner remained `AXIS_BOX_NOT_OBSERVED`.

Four additional fixed antialias controls reuse the existing Rust test raster,
but obtain geometry from the actual native worker. Expected fixture bounds are
report annotations only. All four reached measurement; the incomplete results
remain in the accounting:

| Axis phase intensities | Measurement result | Observed limitation |
| --- | --- | --- |
| 0 / 255 | `AXIS_BOX_NOT_OBSERVED` | Native right endpoint is one white pixel beyond the row's ink |
| 63 / 191 | `AXIS_BOX_NOT_OBSERVED` | Same endpoint extent defect |
| 127 / 127 | Three Y ticks; no X ticks | Native right extent ends before the last X tick |
| 191 / 63 | `AXIS_BOX_NOT_OBSERVED` | Native right endpoint is one white pixel beyond observed ink |

This reproduces a separate native endpoint-extent defect entirely on owned
pixels. It does not justify moving the exact-box scanner to another plot or
relaxing its tick evidence. The next geometry correction must address this
owned defect before any changed-stage development confirmation.

Commands used for this batch:

```text
cargo test --manifest-path native/graphreader-engine/Cargo.toml --locked --offline --jobs 1
cargo build --manifest-path native/graphreader-engine/Cargo.toml --release --locked --offline --jobs 1
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Debug --no-restore -m:1 -nr:false -p:BuildProjectReferences=false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet <probe.dll> --real-dev-cached-axis-properties-self-test
dotnet <probe.dll> --owned-exact-box-contrast-check <source-App.exe> <source-Rust.exe>
```

`CARGO_TARGET_DIR` was the existing `artifacts/native-engine`. Both workers keep
their existing approximately half-core, BelowNormal budgets. No package or UI
acceptance check is implied by these commands.

## Identities

| Item | SHA-256 |
| --- | --- |
| Before owned report, 4,534 bytes | `efe0a7e23e845b1756c8e9057999c620181b852f87f628d001dddc89e7581cca` |
| Intensity report, 7,365 bytes | `a6f7b2bea596cc42397e1599ac1bcaab75fb5f5a942276800731b5ca867a5e40` |
| Executed before/intensity probe DLL | `b3e50f4f037a55e7936708e300a581368ccee62a60666e3ee177ad076ae1a428` |
| Unchanged App EXE | `843b614c434aafd98d9818ea0e3aed0cd309fb433f5261553300b3d0f9710933` |
| Unchanged App DLL | `33a0047728c9184273bf7a3ce45ce304f36eae9f4b03e66657194dd561b403bd` |
| Before Rust worker | `958a14dd74db3cef3c3c726d46581f8a4b35c04b01d71b4349a0dc04c5263e52` |
| Corrected Rust worker | `2cc6f263a00544243291f2605031c4df7e147175bd345480bd0a44f92fde0b7a` |
| Rust source at correction | `eb350f81f2f6d05cf9d66a74827b5aa9149bb551d2c0d4da9fd565bce988386a` |
| Final after-check probe DLL | `ddc4273e6d73941fd7ccdc004821673ee8bdb049da23081bab5462d932d1165e` |
| After owned report, 9,171 bytes | `0ec94cb19ec27cb2ae9b91361c6535f58b2e9e790ad4c1b98df38b1683bd4a19` |

## Limits and retention

The existing pale endpoint remains outside `<200`; no post-change private
inference has been run. Exact-box measurement can still return unavailable or
empty evidence. Production automatic calibration remains incomplete. Original
data, independent evaluation boundaries, unchanged451/Mirage outcomes and the
sole delivered 0.4.54 package remain unchanged. Source remains Apache-2.0 with
no new dependency, model, license or frozen public schema.

After source/outcome `0642f21f42894cfc2997dab40dcce4ae932ad480` reached
`origin/main` and the associated mapping/paired-check consumers finished,
**13 files / 173,061 bytes** were retired at `2026-11-15T18:03:37.7595538Z`:

- `artifacts/cached-axis-pixel-properties`: three files / 2,343 bytes.
- `artifacts/cached-axis-intensity-properties`: three files / 7,830 bytes.
- `artifacts/owned-exact-box-contrast-check`: four files / 4,895 bytes.
- Three completed TRXs in `artifacts/exact-box-ticks-check`: 157,993 bytes.

Resolved paths, exact report/TRX hashes, inventories and absence of reparse
points/running consumers were checked before deletion; the listed targets are
absent. `artifacts/owned-exact-box-contrast-after` remains only as the fixed
nine-case baseline for the native endpoint repair. The cached exact-box trace
remains only for the changed-stage development confirmation; its runtime pin
manifest remains for the current runtime transition. Retire these once those
consumers finish and their outcomes are pushed. Original user data, independent
inputs, active runtime assets and the sole 0.4.54 package remain intact.

The scoped contrast repair and owned checks pass. Full Windows CI
[43744182879](https://github.com/SKang393/Graph-auto-reader/actions/runs/43744182879)
passed on `0642f21f42894cfc2997dab40dcce4ae932ad480`: 13 .NET suites with
3,021 passed, 30 skipped and zero failed, all 72 Rust tests, and applicable
policy/packaging checks including 68 packaging verification tests. Release jobs
were skipped. Complete automatic calibration acceptance remains incomplete,
including the owned endpoint failures above.
