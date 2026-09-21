<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve short cross-shaped markers during artifact masking

The residual mask now keeps a short crossing reviewable when its arms fit the
existing compact-symbol limit. Native marker matches increase from 568 to 579
of 838. This repairs an input defect; the model still fails its accuracy bars.

## Defect and implementation

The saved native input replay shows small X-shaped data symbols receiving a
connecting-line intersection mask. The same four opposing arms describe both
a short symbol and a line crossing. Whole-component protection does not help
when a connector joins the symbol.

Require two opposing line pairs to extend beyond the existing 13-pixel
compact-symbol span before assigning the intersection mask. Short ambiguous
crossings remain visible and produce a review warning. Long intersections,
arrows, brackets and legend exclusions retain their tests. The configuration
fingerprint advances to `raster-residual-v2`; model weights, model cutoffs,
axis geometry, OCR and calibration safeguards remain unchanged.

## Results and limitations

| Measure | Before | After |
| --- | ---: | ---: |
| Native matched markers, all 838 truths | 568 | 579 |
| Extra native markers | 84 | 86 |
| Missing native markers | 270 | 259 |
| Exact text readings, all 453 labels | 330 | 330 |
| Correct text roles | 350 | 350 |
| Printed axis endpoints within two pixels, all 93 | 85 | 85 |
| Complete CSV exports, all 23 sources | 12 | 12 |
| Correct unique exported values, all 706 truths | 403 | 403 |
| Correct relational rows, all 724 expected rows | 399 | 399 |

The native comparison retains 563 earlier matches, recovers 16 and loses five.
Three losses have no nearby above-threshold decoded center; two are rejected
by the unchanged classifier. Precision is 87.07% and recall is 69.09%, both
below 95%. Clearer input alone does not establish a reliable marker model.

The full CSV fixture loses two prior points and gains two others in one source.
Another 55 output records across four sources move by at most 0.9283 original
pixels and 0.3835 y units; their x values and export metadata are unchanged.
Equal aggregate scores therefore do not mean identical predictions. All 11
review failures remain, with zero wrong-scale rows and zero residual exports
from failed sources. All five model stages use CPU on every observed panel.

All 469 application tests pass, including six short/long crossing cases with
and without connecting lines. The native runner builds with zero warnings or
errors. Checks take 86.42 seconds, the 21-source comparison 133.21 seconds,
and the 23-source CSV check 147.51 seconds. Native exit code 1 reflects the
retained calibration failures, not a passing end-to-end workflow.

## Evidence and continuation

The [evidence index](GOAL-22-COMPACT-CROSS-MASK-REPAIR.json) binds the two source
files, tests, executable provenance, model bindings, scores, retained losses
and full CSV differences. An initial unbounded pairing of changed export rows
was superseded by the five-pixel pairing; unmatched removals and additions are
reported separately instead of being described as large point movements.

GitHub run [35607024179](https://github.com/SKang393/Graph-auto-reader/actions/runs/35607024179)
passed for the preceding corner/scheduler checkpoint `bd6456f`. The earlier
scheduler-only run was cancelled by that subsequent push, not a test failure.

Next compare the already available V28 center payload on the repaired native
inputs, preserving its closed failed outcome. That diagnostic uses its frozen
0.25 cutoff, so any difference from V27 at 0.10 is not a weight-only effect.
No new training, private/sealed read, imported dependency, model approval,
packaged build or release occurs. Code and synthetic inputs remain Apache-2.0;
existing third-party models and runtime retain their reviewed notices.

Build 433 remains 0.4.33. All four Goal 22 outcomes are not yet jointly verified.
