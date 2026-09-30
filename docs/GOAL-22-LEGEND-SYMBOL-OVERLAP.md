<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Separate overlapping legend symbols from text crops, 2026-09-30

Three panels of an owned six-panel development graph retained legend symbols
as observations. Their OCR rectangles began at x954, x956 and x952, overlapping
the adjacent symbols. The original-pixel recovery independently measured the
same text row at x963, y112, width118, height13, but its containment rule refused
to replace the wider rectangles. Six geometry-only replays reproduce this in
14.311 seconds. Export correctly failed with `WORKFLOW_RECALIBRATION_REQUIRED`.

Recovery now permits a left overhang into the independently verified symbol
of a closed, single-row legend frame. The detector box must stay within the
measured text on its other three sides and cannot reach left of the symbol.
The strict existing frame, detached-symbol and component checks still apply.
Protected contexts, complete rows, broken frames, extra content and ambiguous
symbols remain unchanged. The resulting original-pixel crop is recognized
normally; no text or role answer is supplied. The recovery identity advances
to `original-pixel-framed-legend-text-recovery-and-assembly-v6-symbol-overhang`.

All **563 OCR and 520 App tests pass**, including 16 new cases, with 16 existing
optional OCR skips. OCR, App and native-runner builds have zero warnings and
errors. Checks take 121.025 seconds; four serial full
workflow comparisons take 698.312 seconds.
Model payloads, numerical thresholds, source bytes, truth and export guards
are unchanged within each arm. Every failed source remains in the denominator.

| Measurement | V44 before / after | V46 before / after |
| --- | ---: | ---: |
| Native matched markers, 838 truth | 739 / 739 | 743 / 743 |
| Native extra markers | 26 / 26 | 19 / 19 |
| Exact OCR readings, 453 truth | 348 / 348 | 387 / 392 |
| Geometry-matched text regions | 392 / 392 | 434 / 435 |
| Extra text regions | 108 / 108 | 129 / 125 |
| Character edits, 3100 truth characters | 728 / 728 | 382 / 342 |
| Correct native text roles | 360 / 360 | 394 / 395 |
| CSV exports, 23 sources | 15 / 15 | 14 / 15 |
| Correct unique CSV values, 706 truth | 414 / 414 | 281 / 414 |
| Correct relational CSV rows, 724 truth | 409 / 409 | 276 / 409 |
| Correct dev values, 206 truth | 156 / 156 | 23 / 156 |
| Correct dev rows, 224 truth | 156 / 156 | 23 / 156 |

V44 retains every source status, failure message and numeric export value.
V46 gains the blocked six-panel source with 139 exported rows, including
133 correct values and phase rows. Its previous exports are unchanged.
The three legend-symbol observations disappear. One plotted center also moves
0.003972 pixels and its radius changes; this is recorded rather than called
bit-identical. All native truth matches are retained. All axis geometry and
x/y calibration transforms remain unchanged; three panels update lattice,
anchor and confidence evidence after the false symbols are removed.

V46 has 11 extra CSV points, 13 extra rows and three wrong-phase rows. V44
has 12, 14 and three respectively. All 851 exported rows have valid calibration
and non-null x/y. Scored wrong-scale, duplicate and failed-source residual
rows remain zero. These results do not establish model or product acceptance.

Retain the runtime correction. V46 training remains **failed_dev_unconsumed**,
with no promotion. The [diagnosis](GOAL-22-LEGEND-SYMBOL-OVERLAP-DIAGNOSIS.json)
and [outcome evidence](GOAL-22-LEGEND-SYMBOL-OVERLAP.json) bind source, runtime,
tests, protocols and results. Reproduction commands are recorded in
`artifacts/goal22-tools/Run-LegendSymbolOverhangChecks.ps1` and
`artifacts/goal22-tools/Run-LegendSymbolOverhangEvaluation.ps1`. A generic run
directory already existed from September 21, so its exclusive creation failed
before any new build. It was preserved and the checks used a fresh dated path.

Only project-owned Apache-2.0 source and synthetic inputs were used. No new
dependency, model payload, training, private/sealed read, production activation,
packaged build or release. WPF interaction was not manually exercised.
Build 433 remains 0.4.33. Goal 22 remains incomplete. Next compare the existing
reviewed server recognizer on the settled V46 runtime, preserving all guards.
