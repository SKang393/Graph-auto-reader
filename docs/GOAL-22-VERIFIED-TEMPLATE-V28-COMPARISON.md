<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Existing V28 after template verification, 2026-09-30

Keep V27/V5. Reusing the existing V28 center configuration after the verified
template repair gains seven net native matches but loses three CSV exports and
71 correct values. The original failed V28 revision remains closed.

| Measurement | V27 at 0.10 | V28 at 0.25 |
| --- | ---: | ---: |
| Native matches | 726/838 | 733/838 |
| False native markers | 25 | 27 |
| CSV exports | 12/23 | 9/23 |
| Correct unique values | 409/706 | 338/706 |
| Correct relational rows | 405/724 | 334/724 |
| Wrong-phase rows | 3 | 3 |

V28 retains 709 old matches, recovers 24 and loses
17. Its 87.47% recall fails the unchanged 95% bar. All 21 native sources,
31 panels and 838 truth points remain. OCR stays at 348/453 exact readings and
axis corners at 84/93. Every model except the center model is identical.
These complete configurations use their existing fixed cutoffs; the result
cannot isolate a weights-only effect.

The CSV comparison retains all 23 sources, 39 panels, 706 points and 724 rows.
Train produces seven exports, 181 correct values and 177 correct rows; dev
produces two exports and 157 correct values/rows. Three previously exporting
train graphs fail closed with `WORKFLOW_RECALIBRATION_REQUIRED`. Failed graphs
stay in the denominator. There are no scored wrong-scale, duplicate or
failed-source residual rows, and upstream OCR/axis observations are unchanged.

Native inference takes 181.30 seconds and CSV inference
229.73 seconds; the complete guarded sequence takes
423.54 seconds. All recorded models use CPU.
The application runtime is the already-tested template repair at `fa9da8e`,
whose 508 App tests, clean build and CI passed. This comparison changes no
application source and repeats no unchanged test suite.

The [aggregate evidence](GOAL-22-VERIFIED-TEMPLATE-V28-COMPARISON.json) binds
protocols, exact model/runtime bytes, source inputs and complete scores.
No training, threshold sweep, private/sealed access, production approval,
dependency, model import, packaged build or release occurred. Existing
Apache-2.0 provenance is unchanged. Build 433 remains 0.4.33. Goal 22 is incomplete.
