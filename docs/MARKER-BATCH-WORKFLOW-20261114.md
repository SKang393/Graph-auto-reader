<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Reviewed marker batch and owned workflow check

Base: `b2b378d9cf51ecdbec2c910ce95d26555f2d692b`. The outcome commit containing
this record owns the source changes. No packaged version was allocated; the
retained, uploaded preview is 0.4.40 and does not include this later batch action.

## Implementation

After reviewing outlines and removing incorrect positions, the user can add
the remaining eligible matches with one explicit action. The command checks
the current image/calibration/seed/series binding, excludes existing and
overlapping positions, preserves per-point correction audits, and records one
Undo/Redo operation and one autosave. A failed add restores the snapshot and
selection, clears stale proposals, and autosaves only the restored state.
Individual Add/Remove actions remain available. No model, threshold, dependency,
public contract or export policy changed.

The diagnostic `--marker-workflow-check` joins existing Rust geometry, OCR,
calibration, marker, export and project services on one owned control. It accepts
known proposals explicitly in code, rather than claiming native user interaction.
It verifies that the matching worker alone never inserts points. The existing
default CSV mode is observation order; calibrated graph X and CSV X are checked
separately, including the extended audit mode/source.

## Validation and timing

Both projects compiled with zero warnings/errors. The initial probe build
failed after 68.768 s on two CA1861 array-allocation diagnostics in the new
probe. Reusing a static expected-value array fixed them. Probe/App build then
took 8.772 s and App test compilation 10.709 s. Focused marker/localization
tests passed **24/24**, no skips, in 2.906 s test time (4.405 s command wall).
The four new STA regressions cover explicit acceptance, duplicates and series,
stale binding, failed second add with save/reopen, and 64 real workspace edits.
The 64-point action took **13.876 ms**, retaining 64 per-point audits with one
Undo and autosave. This is one measured small-project operation, not a UI
responsiveness percentile.

The actual owned workflow completed with `completed=true`, exit 0:

- 640 x 480 control, six observed tick-label pairs; anchors (140,380),
  (140,80), (540,380), first/final session 1/3 and Y maximum 10.
- Seed at (240,230), two accepted matches at (340,230)/(440,230).
  Calibrated X values 1.5/2/2.5 and Y 5 survived save/reopen, with one series
  and three points. CSV contained observation-order X 1/2/3 and Y 5; the audit
  explicitly identified `observation_order`.
- 4.293 s inside the workflow, **4.937 s process wall**. OCR took 2.289 s,
  including 1.003 s cold initialization and 1.090 s recognition. Marker work
  took 186.626 ms, including 157.311 ms matching.
- One OpenCV thread, BelowNormal priority, verified Windows CPU job rate
  416/10000 on 12 logical processors, equivalent to 0.4992 logical core.

The first invocation rejected PascalCase request keys before image processing.
The snake_case retry reached export but the diagnostic incorrectly expected
calibrated X in an observation-order CSV. Its actual audit established the
existing behavior; only the diagnostic expectation was corrected. That attempt
took 4.060 s. Probe-only recompilation took 7.087 s; the App DLL was unchanged
and its passing tests were not repeated. Before every actual worker invocation,
the reviewed OpenCV DLL was restaged after compilation.

## Reproduction and identities

Use the checkout-local .NET/NuGet caches and isolated
`OutDir=artifacts/hardware-final-build/`. Build
`tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj`,
then `tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj` with
`BuildProjectReferences=false`, using Release, `--no-restore -m:1 -nr:false
-p:NuGetAudit=false -p:UseSharedCompilation=false`. Run the App test project
with the same output directory, `--no-build --no-restore` and filter
`FullyQualifiedName~MarkerExemplar|FullyQualifiedName~Localization`.

Generate the owned input with `New-TickCalibrationFixture.ps1 -OutputPath`
under a fresh bounded scratch directory. Supply the existing snake_case
`ProbeRequest` fields with scope `owned-synthetic-review-only`, absolute image
and asset paths, full 640 x 480 crop, image hash and plot [100,80,580,380]. Run:

```text
GraphReader.CalibrationOcrProbe.exe --marker-workflow-check <request.json> <absolute-App-exe> <absolute-review-assets> <absolute-native/graphreader-engine.exe> <absolute-empty-output-directory>
```

SHA-256 identities:

| Item | SHA-256 |
| --- | --- |
| App DLL | `52a28fb87c8b1dc414fa0e7a2e3e8f9ff1a5a23a8116d566d0cb1fa29aa282a6` |
| Final probe DLL | `e69bac050bd8ffdc3849b2f4b2ac61c6849fae0ce2d59f263194f128758b18d3` |
| Rust worker | `0c8cbdb1533e71172bee38da9d39e684689d7f20b4d6eacd36efbc20a70b98b5` |
| Owned source PNG | `c1750cab62d79c96b75310cdb5419edcb91a1e99218b248894a26b731aa43dda` |
| Derived marker PNG | `0148c7eb9beb40cbf6614f4a6c49960b14272af23bce433781381c88a865de37` |
| Test TRX | `f2423238d1063dfc5d3bc29c00a5e4ac83627792e7597b0a7d035f6b46cb82f4` |
| Workflow JSON | `ea310157c318c5b6939eb6d04a7b60bcafefb961886fc16a207b90054c9805d8` |
| CSV | `a5cab504b91b88ae3c3f6ff976e298cc3d75e47139102400dc64c0e335635302` |

OCR model/manifest and reviewed OpenCV identities remain those in the
[0.4.40 delivery record](HOSTED-INTERNAL-PREVIEW-20261113.md#hosted-delivery-0440).
No private images, training, model selection or sealed evaluation were used.
The generated control uses installed Segoe UI through the existing fixture
script; no font binary was copied. Source remains Apache-2.0.

## Remaining work and retention

Native interaction remains waiting under REV-011 because Computer Use was
stopped. One control does not establish representative accuracy. Marker
templates currently must fit inside the confirmed plot, excluding useful
first/last-session and Y-zero examples. The next change will allow original
image context outside the plot while keeping every proposed center inside it;
no padding, fabricated pixels, threshold or model change is justified.

After source/outcome `0f449629adc1cb7d520ab3ad5c22f43204523a52` reached main,
all 26 files (80,225 bytes) under `artifacts/marker-workflow-check` were retired,
including failed attempt output, generated controls, CSV/project, TRX and logs.
Containment and active-worker checks passed. Keep the isolated compiler output and
hard-linked review assets only for the next boundary regression/worker check.
The retained package, shared user Data and the earlier open QA App are protected.

## Plot-boundary follow-up

Base: `0f449629adc1cb7d520ab3ad5c22f43204523a52`. A first/last-session
example previously failed because the full square had to fit inside the plot.
Workspace, UI and worker validation now require the full square to fit inside
the original panel, with its selected center inside the confirmed plot. Matching
uses the exact seed offset to calculate bounded integer template origins in the
original image. Candidate centers use the same inclusive pixel-boundary helper.
No padding, resizing, invented pixels, similarity threshold, model or protocol
change was introduced. Existing CPU/memory/count/time limits remain intact.

Five added methods (six cases) cover all four plot corners, excluded identical
outside markers, odd crops and fractional centers, tiny plots, and rejection of
original-image clipping. UI cases use the real workspace. Independent read-only
review found no material ROI-bound defect. Probe/App and test builds passed with
zero warnings/errors in 78.713 s and 11.717 s. The focused marker/localization
run passed **30/30**, no skips, in 5.229 s command wall time.

The actual owned workflow control now places the seed at (140,230) and matches
at (340,230)/(540,230), exercising both horizontal plot boundaries. All six
calibration tick pairs were observed, both proposals were explicitly accepted,
and three points with calibrated X 1/2/3 and Y 5 survived export/save/reopen.
Observation-order CSV/audit checks also passed. The workflow took 4.278 s
internally and **4.889 s process wall**, with OCR 2.095 s and marker work
116.326 ms (81.215 ms matching). One OpenCV thread and the verified 0.4992-core
budget remained in effect. This is a controlled execution check, not a latency
improvement claim or representative accuracy evidence.

SHA-256 identities for this follow-up:

| Item | SHA-256 |
| --- | --- |
| App DLL | `144f81553539ca592bd58eb3e33f1f330f9aae5efde9fcb6194c6f7869ce62cf` |
| Probe DLL | `32256ce1be037177bbe0cc7a49d35d9b79bd32edec53b976339c11a37bb6d019` |
| Derived boundary-control PNG | `e981c6c798a178cc8f8baeb89cee985bbd3b438611cfff3a1b5178e205726625` |
| Test TRX | `109a93ad482c66d60ce6ef6b5542fd0667ee8fdda595842a41bdad202a613c4a` |
| Workflow JSON | `e317b93fb477564a8e0fbb32c069d7ac29f053c3c58d83c27536951f39c40a38` |

The same commands above use scratch `artifacts/marker-boundary-check` and the
updated control coordinates. Rust, OCR, native DLL and CSV identities are
unchanged. After this outcome is pushed, retire its 16 files (74,051 bytes).
The isolated compiler output remains the active input for a two-control check
of changing connecting-line slopes. That diagnostic will measure utility of
the existing matcher without changing its threshold or selecting a model.
Native review remains waiting; this source increment does not allocate a package.
