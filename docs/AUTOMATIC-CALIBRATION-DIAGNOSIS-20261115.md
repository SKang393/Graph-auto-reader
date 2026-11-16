<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Calibration geometry and label-association diagnosis

## Fixed development scope

This diagnostic examines the geometry and
association failures already recorded in the [prepared 451 diagnostic](PREPARED-CALIBRATION-451-DIAGNOSTIC-20261114.md)
and [fixed Mirage comparison](MIRAGE-PAIRED-PIPELINE-20261115.md). It does not repeat
that comparison or select a reader, threshold, model, or candidate on private answers.

The sample was fixed before execution: the first 12 of the 120 frozen development
projects, ordered by anonymized case ID. All returned panels were attempted;
only genuine no-panel detection allowed a full-image fallback. The existing
selector excluded the 51 reserved project payloads. The corpus's prior exposure
and overlap limitations remain those in the [authorization record](GOAL-22-MANUAL-DATA-AUTHORIZATION-20261030.md).
Sealed-run budget: zero. No optimizer, truth scoring, Apply, export, or production
promotion occurred.

The unchanged delivered 0.4.52 runtime was pinned before input selection: App,
Rust worker, OCR model/manifest/notices, all 21 supporting libraries, and loaded
managed identities. Its source is `68781bcce22cf925bff2f99b21c139b622117c06`.
The diagnostic source is based on `6ff55ddb3a0c2bc4ad37b0340915512dbe8f4ec4`;
`RealDev452Pins.cs` records the exact package identities. The probe executes the
packaged panelizer, one Rust request with its existing diagnostic channel,
unchanged native fallback, persistent CPU OCR worker, and review-draft pairing.
An eight-control owned check established ordinary/diagnostic Rust response parity.
Supplied geometry in owned grouping tests is distinguished from automatic geometry
in this development trace.

Sensitive local output retains OCR literals, confidence, row geometry, tick
distances, proposal reasons and partial-axis availability. It contains no manual
anchor truth, source paths or copied images. Geometric distances are diagnostics;
the unchanged pairing result remains authoritative. Only aggregates are recorded
here. Per-project deadline is 45 seconds; overall/watchdog deadlines are 900/905
seconds. Parent and worker CPU budgets retain independent approximately half-core
limits and BelowNormal priority.

## Executed trace

```text
GraphReader.CalibrationOcrProbe.exe --real-dev-452-association-trace <absolute-corpus-root> <verified-452-App.exe> <verified-452-ocr-assets> <verified-452-Rust-worker> --explicit-opt-in
```

The single run completed at `2026-11-15T05:52:10.3629534Z`, exit 0, empty stderr,
in **23.9217936 seconds**. It attempted all 12 selected projects and all 12
effective panels: 11 prepared panels and one genuine no-panel fallback. No
execution exception, timeout, unattempted panel or unsupported dimension occurred;
the five geometry-stage failures below remained in the denominator.
Preparation returned full-image bounds in this small sample; the owned mapping
check separately verified one application of a nonzero crop offset.

| Outcome | Count |
| --- | ---: |
| Geometry and OCR responses | 7 / 12 panels |
| Geometry failures | 5 / 12 panels |
| OCR rows / literal numeric rows | 113 / 67 |
| Associated label/tick pairs | 58 |
| Independently available X / Y drafts | 0 / 2 |
| Complete review drafts | 0 |
| Native fallback calls / sealed payload reads | 0 / 0 |

All five geometry failures were Rust ambiguity results: four `NO_VALID_PAIR`
and one `THICK_COL_BAND`. The four pair searches examined 47 candidate pairs,
with 41 connection rejections, one span rejection and five rejections for each
tick axis. Tick rejection counts overlap. These are candidate-search diagnostics,
not counts of true axes or proof of multiple plots.

Overlapping pairing reasons were insufficient pairs 4, unpaired label 3,
invalid numeric label 2, invalid range 2, duplicate association 1, inconsistent
spacing 1 and nonmonotonic sequence 1. OCR median was 2289.7463 ms; its maximum
was 6954.5489 ms, including the first worker request. Seven responses cannot
establish representative latency or OCR accuracy. There was no post-fix private
candidate evaluation.

Review found that the original trace's `incomplete_or_failed_panels` field meant
unfinished execution, so it read zero despite five returned geometry failures.
The original per-panel statuses retained every failure. Source now names the
execution residual `unfinished_execution_panels` and reports separate terminal
status counts. The counts above were derived from the existing trace; it was
not overwritten or rerun to repair this reporting defect.

The accounting-only probe build passed with zero warnings/errors in 7.14 seconds
after correcting a relative MSBuild output-path error. Its owned self-test
covered all five terminal statuses without private reads. The resulting probe
DLL is `bc73f603ae62c578baeded152e8208ca8a4f8626154ab7277f6dff3c9a68fafb`;
all 21 staged runtime hashes still match the unchanged 452 pins. It was not used
to repeat the development run.

| Evidence | SHA-256 |
| --- | --- |
| Frozen assignment | `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a` |
| Selected metadata inventory | `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d` |
| Twelve-input source/image digest | `4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1` |
| Executed probe DLL | `332a808999f942bbe2a8235fd28625b6e94f0970cf567a2d2434b5b3c6accd71` |
| Sensitive trace, 50,261 bytes | `220970a70d75687667a6c5f54870e9dd0190b8c9ba6efa0055e4621f259fdb50` |

## Source defects and owned verification

The cached evidence includes a horizontal numeric sequence merged into one OCR
crop. Source inspection found that measured-tick grouping required a single
full-height ink component to cross the exact tick column. A tick centered in the
white gap between two digits provided no such support. The owned reproduction
used three pairs of glyph blocks with supplied ticks: it failed before the fix
with one region instead of three. Grouping now also accepts two full-height
components bracketing the tick within its midpoint-bounded lane and the existing
component-width gap bound. Decimal, short-dot, crossing-ink, minor-tick, caption,
explicit ungrouped behavior and immutable input checks remain in force.

The new control and focused OCR guards passed **33/33**, no skips, in 16.9735448
seconds including compilation. The failing-before run took 20.5195561 seconds.
This establishes detector grouping behavior with supplied geometry, not literal
OCR accuracy or complete automatic calibration.

Two independent reporting defects were also corrected:

- `NativeAxisWorker` maps the typed recoverable geometry-not-found exception to
  existing geometry ambiguity, instead of reporting a missing native runtime.
  Blank and single-orientation owned images verify the classification; valid
  image cancellation still propagates. This is not an attribution of the four
  historical native-unavailable cases.
- `CalibrationTickProposal` records a validated axis's zero/range failure even
  when the other axis has already failed. Insufficient evidence does not invent
  an endpoint conclusion. Acceptance and partial-draft rules are unchanged.

The focused App suite passed **64/64**, no skips, with 479 ms test time and
80.0600143 seconds including compilation. Its filter covered tick proposals,
native detection failure, OpenCV responses and Rust-first fallback.

After integrating the OCR fix, the complete App suite passed **1060/1060**, no
skips, in 39 seconds of tests and 94.7811663 seconds including compilation:

```text
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false
cargo test --offline --manifest-path native/graphreader-engine/Cargo.toml --target-dir artifacts/native-engine
```

## Gray-axis limitation and rejected experiment

One already selected development image was inspected through the same frozen
selector and locked source reader. A single 519,939-byte embedded PNG was
temporarily extracted for this active diagnosis. Sampling the visible neutral
gray axis strokes with the production WPF Gray8 conversion found stroke pixels
above the fixed `<128` dark-pixel predicate. This establishes an input-contrast
gap; it does not identify the rejected thick band as the actual axis.

An independent owned graph with gray axes, observed ticks and a black marker
reproduced the failure; its black-axis counterpart succeeded. One histogram-first
Otsu preprocessing candidate was evaluated only on owned controls. It passed
53/56 tests, still failed the gray positive, accepted a faint-stroke negative,
and missed a second gray plot. It was removed without threshold iteration or
private evaluation. The final Rust suite passed **55/55**, no ignored tests,
in 0.24 seconds after a 3.41-second compilation. Its only source addition is an
explicit test documenting the remaining gray-axis limitation. No Rust production
behavior, model or dependency changed.

## Integration, storage and acceptance

No package, tag, release or model activation is part of this source batch.
0.4.52 remains the sole verified packaged build. Originals, shared user Data,
current runtime models and independent evaluation remain preserved.

The completed diagnostic used `artifacts/real-dev-452-trace` for one probe
dependency set (42,886,486 bytes before tests), its bounded trace and the one
temporary inspection image. The owned grouping evidence uses
`artifacts/tick-gap-owned-check`. These disposable directories are now retired;
no private output was committed. Routine compiler dependencies still needed by
the next authorized batch are not historical result archives.

The reviewed closeout scope is 48 files / 45,107,347 bytes in the trace directory
and 173 files / 102,149,268 bytes in the owned grouping directory. Both roots and
their contents had no reparse points, and no diagnostic process remained. This
scope includes temporary runtime staging, raw results and the one inspection
PNG, not originals, the current package or shared user Data.

After source/outcome commit `bbde57d24c8ca064edadca7e4f31f7c1fd63f9b7` was verified
on `origin/main`, guarded removal completed at `2026-11-15T06:19:36.5350535Z`:
**221 files / 147,256,615 bytes**. Both target directories were confirmed absent;
the current package, shared Data and original corpus remained present. The next
Y-label grouping work uses the documented source defect and owned controls,
without retaining private prediction dumps or repeating inference.

Source controls passed as reported above. **Automatic-calibration acceptance
remains FAIL**: this fixed sample produced no complete draft, gray axes remain
unresolved, and the source fixes have no new private accuracy or native-package
acceptance claim. The next useful work is a source-backed defect reproduction,
not another unchanged corpus comparison or a threshold search.
