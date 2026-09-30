<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Existing V29 after runtime repairs, 2026-09-30

Retain V27/V5. Reusing V29 after verified template seeds and the connected-axis
endpoint repair gives one additional native match and seven fewer extras, but
loses three correct CSV values and three correct rows. Its original failed
revision remains closed and unapproved.

| Measurement | V27 at 0.10 | V29 at 0.25 |
| --- | ---: | ---: |
| Native matches | 733/838 | 734/838 |
| False native markers | 25 | 18 |
| CSV exports | 12/23 | 12/23 |
| Correct unique values | 409/706 | 406/706 |
| Correct relational rows | 405/724 | 402/724 |
| Wrong-phase rows | 3 | 3 |

V29 retains 713 earlier matches, recovers 21 and loses 20. Recall remains
87.59%, below the unchanged 95% bar in `ml/policy/acceptance-bars.json`.
All 21 native sources, 31 panels and 838 truths remain scored. OCR stays at
348/453 exact readings and axis corners at 85/93. The existing model cutoffs
are fixed, so this compares configurations and does not isolate weights alone.

CSV retains all 23 sources, 39 panels, 706 points and 724 expected rows. Train
produces ten exports, 250 correct values and 246 correct rows; dev produces two
exports and 156 correct values/rows. Every source retains its completed/failed
status. One failed source has different calibration error text. All twelve
exported sources have some coordinate or value changes. There are no scored
wrong-scale, duplicate or failed-source residual rows. Upstream OCR and axis
observations are unchanged. Failed sources remain in the denominator.

Native inference takes 168.84 seconds, CSV inference 249.11 seconds, and the
guarded sequence 435.09 seconds. All recorded models use CPU. The application
runtime is the tested endpoint repair at `b1b6417`, whose 88 Axis tests, 508 App
tests, clean builds and CI passed. This comparison changes no application
source and repeats no unchanged test suite.

The [aggregate evidence](GOAL-22-REPAIRED-RUNTIME-V29-COMPARISON.json) binds the
protocols, model/runtime bytes, inputs and complete scores. No training,
threshold sweep, private/sealed access, model import, production approval,
packaged build or release occurred. Existing Apache-2.0 provenance is unchanged.
Goal 22 remains incomplete; build 433 remains 0.4.33.
