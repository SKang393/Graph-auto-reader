<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preparation and geometry diagnostic, 0.4.47

## Scope and import-path finding

This diagnostic extends parent source `542a9d77`; the delivered 0.4.47
application/Rust runtime remains unchanged. The actual raster import stage
`ProductionWorkflowImportStage` invokes `ProductionRasterPanelizer`. The prior
120-item geometry/calibration diagnostic evaluated raw full-source images and
bypassed this panelizer. The owned preparation self-test uses the production
panelizer: its two-plot control returned two valid panels, while raw whole-image
geometry found no axis. Single-plot and caption-margin controls each returned
one panel with successful geometry. The self-test also rejected missing
capability and read zero source payloads.

An initial owned self-test took `57.4576391 s` process wall. Its later
instrumented run took `9.8229503 s` process wall, `5.7950142 s` internally, and
`859.375 ms` parent CPU. This narrows measured work but does not identify the
initial delay's cause or prove it fixed.

## Fixed real-dev preparation result

The run from 2026-11-14T17:34:53.4079041Z to 2026-11-14T17:37:53.0605361Z
checked the fixed 0.4.47 package/runtime over the same frozen 120-item
assignment and identity as the preceding confirmations. All 120 payloads were
hashed and images bound. The diagnostic performed no OCR calls, source-image
writes, per-case output, private copies, sealed reads, model/threshold
selection, or optimizer work. It reported review-only and production approval
false.

| Stage/result | Count |
| --- | ---: |
| Source images attempted / hashed / bound | 120 / 120 / 120 |
| Raw geometry success | 37 |
| Preparation: zero / one / multiple panels | 15 / 98 / 7 |
| Returned panels attempted / geometry success | 112 / 36 |
| Preparation failures / unclassified / unsupported panels | 0 / 0 / 0 |
| Effective import panels with full-image fallback | 127 (112 + 15) |
| Sources with at least one effective geometry success | 38 |

Among 37 raw successes, 2 came from zero-panel sources, 34 from one-panel
sources, and 1 from a multiple-panel source. Effective import had 38 source
successes compared with 37 raw successes. This is a net availability difference;
paired recovery/loss counts were not recorded. It does not establish that
preparation explains zero complete calibrations or establish accuracy. No OCR
ran in this diagnostic.

Preparation time across 120 cases had median `43.69325 ms`, p95 `445.298 ms`,
and maximum `812.4604 ms`. Total process wall was `179.642708 s`; aggregate
pipeline time was `173581.9371 ms`. The case totals include raw and prepared
geometry, and are not OCR latency. Peak sampled parent memory was 90,836,992
bytes and peak parent working set was 141,348,864 bytes; sampling is not a hard
memory ceiling. Parent preprocessing and child workers were independently
capped at 0.4992 logical core each, not jointly.

The reason counts are nonexclusive: prepared `AXIS_AMBIGUOUS` 72,
`AXIS_NOT_FOUND` 2, `AXIS_NO_INTERSECTION` 2; raw `AXIS_AMBIGUOUS` 80,
`AXIS_NOT_FOUND` 1, `AXIS_NO_INTERSECTION` 2. These counts do not prove
particular source layouts or general panelizer accuracy.

## Identity and reproduction

0.4.47 portable package SHA-256 is
`8cdb6e9e523f0c157cc5114d7ed699bbbf765c7a26df068800a3b80803f4a319`. App EXE SHA-256 is
`a2824381f3b9d19caad083b19f031e45dd50f8f02bdda41a01eb38433177d12f`, App DLL
`5fa629e7a01b40ec58fb4ab136dd21b0f5b3ff5cc61e286b1c45a7642317d039`, and Rust
worker `44258191be06bae2fa9536ebd84cca69631d7732fd3237bc6b001d71440a673d`.
Probe DLL SHA-256 is
`800219f7c196457c20919f5852e61bcb269b21d71150c14a3c4a094e23ee14b2`; the
diagnostic adapter hash is recorded below.
The complete 21-file runtime list is pinned in the diagnostic adapter at this
source. Assignment SHA-256 is
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`, inventory
SHA-256 `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`,
and source identity digest
`bbe77dd52fc4a08dd2e2a039de234677bbf0e9267d6b812864c8e87e16e5931f`.
Aggregate report SHA-256 is
`a49f8d7485123ba0605661abc1f46425a5147c6fce7c76b0823a86365fe811d1`;
diagnostic adapter SHA-256 is
`6b093911f8917c9bb047f482462c966e9c39ba1c3cea7f50f31bd60612de8631`.

```powershell
$probe = 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\hardware-final-build\GraphReader.CalibrationOcrProbe.exe'
$build = 'E:\Graph-auto-reader\artifacts\dev-portable\builds\0.4.47-20261114T093850726Z-76204ba4'
& $probe --preparation-self-test (Join-Path $build 'native\graphreader-engine.exe')
& $probe --real-dev-preparation-check 'E:\Graph-auto-reader\data\manual data' (Join-Path $build 'GraphReader.App.exe') (Join-Path $build 'review-assistance\ocr') (Join-Path $build 'native\graphreader-engine.exe') --explicit-opt-in
```

The first owned invocation's report SHA-256 is
`118e1746f8128574bd19e995c48315901165c3b60fa0ce9b444ba3ee5349fec8`; the
instrumented report is
`5128c5f8945ccaea2e2f1c7d102f4631fdb821f3ce8c9d4fe4b684e39aac017b`. The
aggregate run exited 0 with empty stderr; its report SHA-256 is
`a49f8d7485123ba0605661abc1f46425a5147c6fce7c76b0823a86365fe811d1`. Raw
diagnostic scratch under `artifacts/preparation-diagnostic-check` was retired
after source/outcome commit `4992c7f239d6b1dcb8555fe0e02b9dbbd9934696`
reached `origin/main` and scoped consumers had exited: 15 files / 8,437 bytes
at `2026-11-14T17:50:19.9584891Z`. Current dependencies and the sole 0.4.47
package remain available.
These results neither approve automatic calibration nor establish accuracy.

The probe-only Release compilation passed with zero warnings/errors: initial
11.403 seconds and instrumented 17.184 seconds. Both used --no-restore and
BuildProjectReferences=false with the existing hardware-final-build output;
all 21 delivered libraries were restaged and verified at 26 destinations.
No application package was rebuilt.
