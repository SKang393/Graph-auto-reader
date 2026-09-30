<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Keep verified legend frames out of plotted data, 2026-09-30

Initial detector centers inside a verified legend frame remained eligible as
plot points. Template and enclosed-center recovery already excluded those
frames. The initial path now uses the same measured original-pixel bounds
before recovery, calibration, grouping and export. Right and bottom edges
remain exclusive. Original candidates remain in the projection audit, with an
explicit rejection reason in stage provenance. The adapter/cache identity
advances with the legend-input version.

All **510 App tests** pass. App and native-runner builds have zero warnings
and errors. The added cases prove a classifier-accepted frame corner cannot
enter CSV, remains rejected in the audit, and does not remove a plotted center
at the exclusive outer edge. The initial launcher used the wrong output folder
after a clean build; its failure is retained and the passed build was reused.

The four complete runs keep all 88 source executions and 140 panel observations.
Each marker configuration is compared with its own prior runtime, using exactly
the same models and operating thresholds.

| Measurement | V27 before / after | V30 before / after |
| --- | ---: | ---: |
| Native matched markers | 733 / 733 of 838 | 762 / 762 of 838 |
| Native extra markers | 25 / 25 | 21 / 21 |
| Exact OCR strings | 348 / 348 of 453 | 348 / 348 of 453 |
| Correct axis corners | 85 / 85 of 93 | 85 / 85 of 93 |
| CSV source exports | 12 / 12 of 23 | 6 / 6 of 23 |
| Correct unique CSV values | 409 / 409 of 706 | 99 / 99 of 706 |
| Correct relational rows | 405 / 405 of 724 | 98 / 98 of 724 |

The CSV path excludes 83 classified frame candidates
with V27 and 154 with V30. Of these,
0 and 5,
respectively, were previously accepted plot points; the remainder were already
rejected by other checks. Every excluded center is more than five pixels from
every authored truth point. No native-frame candidate is affected. Initial
center proposals, OCR, axes and all remaining classifications are unchanged.

Every source status, failure message and exported numeric value is unchanged.
Other false detections on connecting lines and arrows still block the six
regressed V30 sources. Keep V27/V5 and leave the V30 training revision closed.
This repair does not restore those exports or meet the remaining accuracy bars.
V27 still has 11 failed sources and three explicit unknown-phase rows. Neither
arm emits a scored wrong-scale, duplicate or failed-source residual row; the
runtime calibration guard remains authoritative.

The four-run sequence takes 850.54 seconds.
V27 native/CSV inference takes 176.21/
252.89 seconds; V30 takes
167.16/219.80 seconds.
All observed models use CPU under the shared resource guard.

The [evidence record](GOAL-22-LEGEND-FRAME-EXCLUSION.json) binds sources,
runtimes, protocols, inputs, tests and all scores. Existing Apache-2.0 source
and reviewed dependencies remain unchanged in licensing. No training,
private/sealed access, new model, production switch, packaged build or release
occurred. WPF interaction was not manually exercised. Build 433 remains 0.4.33.
Goal 22 remains incomplete; next, verify the stale pre-recovery tick-warning
finding without changing any numeric reading or dropping other review causes.
