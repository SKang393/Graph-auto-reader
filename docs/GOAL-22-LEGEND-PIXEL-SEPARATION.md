<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Measure legend separation between foreground pixels, 2026-09-30

Six saved owned-synthetic panel traces independently locate a closed frame,
one detached symbol and a complete text row. Four rows fail because the symbol
separation check subtracts an exclusive right edge, counting blank columns
instead of the distance between foreground pixels. For example, six blank
columns separate a 13-pixel first letter from its symbol: the pixel distance
is seven, which satisfies the existing 6.5-pixel requirement. One different
row fails the separate height restriction and is outside this repair.

Recovery now uses `text.Left - (glyph.Right - 1)`. It preserves the existing
half-height requirement and all closed-frame, single-symbol, component-count,
height, containment and protected-context checks. It still reads unchanged
original pixels and supplies no text answer. Its cache identity advances to
`original-pixel-framed-legend-text-recovery-and-assembly-v7-pixel-separation`.

Nine new tests cover odd/even heights, missing/partial detections, the exact
foreground-distance boundary, rejection one pixel below it, unchanged pixels
and idempotent recovery. All **594 OCR and 520 App tests pass**, with 16 existing
optional OCR skips. OCR, App and native-runner builds have zero warnings and
errors. Fixed synthetic train/dev sources, model weights, inference thresholds,
truth and export safeguards are unchanged within each comparison. All failed
sources remain scored; no private or sealed data is used.

| Measurement | V44/English before / after | V46/server before / after |
| --- | ---: | ---: |
| Native matched markers, 838 truth | 739 / 742 | 744 / 746 |
| Native extra markers | 26 / 26 | 20 / 20 |
| Exact text readings, 453 truth | 348 / 353 | 412 / 416 |
| Geometry-matched text regions | 392 / 397 | 434 / 435 |
| Extra text regions | 108 / 104 | 123 / 116 |
| Character edits, 3100 truth characters | 728 / 625 | 316 / 247 |
| Correct text roles | 360 / 365 | 399 / 403 |
| CSV exports, 23 sources | 15 / 15 | 16 / 16 |
| Correct unique values, 706 truth | 414 / 414 | 429 / 429 |
| Correct relational rows, 724 truth | 409 / 409 | 416 / 416 |

Paired exact-text accounting: `v44: 348 retained, 5 recovered, 0 lost, 100 missed_by_both; v46: 412 retained, 4 recovered, 0 lost, 37 missed_by_both`.

Paired marker accounting: `v44: 739 retained, 3 recovered, 0 lost, 96 missed_by_both; v46: 744 retained, 2 recovered, 0 lost, 92 missed_by_both`.

Both configurations retain all previous CSV source statuses and numeric
outputs. All 866 exported rows have finite x/y values, finite original
pixel coordinates and valid calibration. Wrong-scale, duplicate and failed-source
residual rows remain zero. Axis geometry is unchanged across all 140 compared
panels. Seven native-panel y fits change, with maximum full-panel value effect
0.000481694856; tick inlier identities and validity are unchanged.
One native marker moves from (1097,206), radius4.375, to (1097,205), radius5
in both configurations, retaining its truth match. Other observation changes
are retained in the paired evidence; recovered OCR can alter downstream marker
processing and recognition confidence can alter weighted calibration fits.

Checks took 126.387 seconds. Four serial native/CSV
comparisons took 797.453 seconds under the
shared 80 percent CPU backstop, with 12 processors eligible and Idle priority.

Retain the repair. Overall Goal 22 remains **incomplete**. Keep V44/English/V27/V5
as the retained model configuration. V46 and V31 remain closed and unpromoted.
Remaining legend height/containment cases, annotation-arrow handling, broader
accuracy, sealed/real acceptance, production activation and both final Windows
distributions still require work. Build 433 remains version 0.4.33.

The [diagnosis](GOAL-22-LEGEND-PIXEL-SEPARATION-DIAGNOSIS.json) and
[outcome](GOAL-22-LEGEND-PIXEL-SEPARATION.json) bind source, runtime, tests,
protocols and complete results. Reproduction uses
`artifacts/goal22-tools/Run-LegendPixelSeparationChecks.ps1` and
`artifacts/goal22-tools/Run-LegendPixelSeparationEvaluation.ps1`.
This is project-owned Apache-2.0 work with unchanged reviewed dependencies and
model license inputs. No new external data, optimizer, package or release.
