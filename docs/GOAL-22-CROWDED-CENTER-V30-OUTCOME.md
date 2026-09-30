# Crowded-center V30 outcome

V30 improves native marker recall but loses six previously successful CSV
exports. Reject this development candidate and retain V27/V5. The calibration
and export safeguards remain unchanged.

## Change and validation

The [recorded coverage diagnosis](GOAL-22-CROWDED-CENTER-COVERAGE-DIAGNOSIS.json)
found 54 native misses without a nearby decoded center, including 29 in a
crowded graph. Add a general owned training supplement containing 3,456 scenes
and 9,504 visible markers across all nine shapes, three fills, four neighbor
contexts and two draw orders. Every visible neighbor participates in labels.
Four truths unsupported by the existing proposal grid remain recorded.

Retain all 87,638 prior rows and add 182,614 crowded rows, for 270,252 total.
The 176 development scenes and all 2,210 truths remain byte-identical. Start
from the authenticated V27 checkpoint and run eight fixed epochs, 16,896
optimizer steps, at the registered recipe. Keep the final epoch only. No
private or sealed data is read.

All 117 preparation and recipe tests pass. Training and full development
evaluation take 1,908.590 seconds. ONNX and PyTorch agree across all 270,236
development proposal rows: maximum absolute error 0.000006676, with zero
confidence-cutoff decision changes. Both use 12 threads under the existing
80 percent CPU guard. The original source snapshot verifies unchanged at close.

## Results

At the registered 0.25 center cutoff, component development recall is 95.26%
but precision is 69.72%. Family recall is 99.51% but precision is 44.57%.
The unchanged 95% bars fail. Other reported cutoffs are descriptive; none is
selected after seeing these results.

The full workflow holds the tested `b1b6417` application runtime, V5 classifier,
OCR models and every input fixed. It compares V30 at 0.25 with retained V27 at
0.10, so this is a configuration comparison rather than weights alone.

| Complete workflow measurement | V27/V5 | V30/V5 |
| --- | ---: | ---: |
| Native matched markers, 838 truths | 733 | 762 |
| Native extra markers | 25 | 21 |
| Native missing markers | 105 | 76 |
| Correct axis corners, 93 truths | 85 | 85 |
| Exact OCR text, 453 truths | 348 | 348 |
| CSV exported sources, 23 sources | 12 | 6 |
| Correct CSV values, 706 truths | 409 | 99 |
| Correct CSV rows, 724 truths | 405 | 98 |

Native comparison retains 716 old matches, gains 46 and loses 17. Recall is
90.93%, still below 95%. Six formerly exported sources now fail closed with
`WORKFLOW_RECALIBRATION_REQUIRED`. All 39 CSV panels and all failed-source
truths remain scored. Train retains five exports and 74 correct values; dev
retains one export and 25 correct values. There are four extra points, five
extra rows, and zero duplicate, wrong-scale or failed-source residual rows.
Those zero counts are observations, not proof of a safety property.

Native inference takes 167.054 seconds and CSV inference 237.602 seconds;
the complete evaluation chain takes 420.954 seconds. All model envelopes use
CPU. OCR and axis observations are unchanged.

## Outcome and next diagnosis

The [single revision outcome](../ml/markers/center/crowded_coverage_v30/P1_RESULT.json)
binds the training, parity, tests, runtime, scores and decision. Ledger revision
93 closes as `failed_dev_unconsumed`; all preceding 92 entries are unchanged.
No sealed budget is consumed, model is activated or package is produced.

Saved-stage attribution leaves 39 misses without a nearby decoded center,
27 classifier rejections, three text exclusions, six competing neighboring
matches and one suppression. These counts describe the observed stages and do
not establish a unique causal fix. Trace the six new CSV failures before any
further adaptation. Do not weaken calibration checks to accept extra points.

Code, weights and training data are project-owned Apache-2.0. No dependency or
external weights are added. The candidate remains local and unapproved. Build
433 stays 0.4.33; Goal 22 and overall acceptance remain incomplete.
