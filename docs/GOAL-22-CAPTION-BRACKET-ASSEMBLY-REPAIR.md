<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve bracketed captions during text assembly, 2026-09-21

Header assembly now checks the existing original-pixel bracket evidence before
joining words. If a bracket contains one caption but not the proposed combined
crop, the adjacent label stays separate. Words within a complete bracketed
caption still join. Inside-plot grouping and the older geometry-only API remain
available. Production header context supplies the immutable original image;
the composition and cache identity advance to v3.

A saved-observation replay finds exactly two affected header merges on the
21-source development fixture. Both combine a bracketed annotation with a
detached condition label. Native verification recovers all four labels
correctly and retains every earlier exact reading.

| Check | Before | After |
| --- | ---: | ---: |
| Matched regions | 385/453 | 389/453 |
| Exact text | 340/453 | 345/453 |
| Correct roles | 353/453 | 357/453 |
| Extra / missing regions | 118 / 68 | 116 / 64 |
| Character errors | 858/3100 | 795/3100 |
| Matched markers / extra markers | 666/838 / 17 | 666/838 / 17 |
| Correct axis corners within two pixels | 84/93 | 84/93 |

The fifth additional exact reading is an incidental numeric change from
`1.00` to `100`. An already incorrect participant reading also changes but
remains incorrect. Their complete recognition-input cause is not independently
traced. Two native calibration records change and both remain `NeedsReview`.
Do not describe every original reading or calibration record as unchanged.

The separate 23-source, 39-panel CSV check retains identical exported numeric
values, completion states, errors, calibration records and metrics: 12 exports,
11 failures, 403/706 correct unique values and 399/724 correct relational rows.
Eight panels have changed OCR region IDs because assembly is versioned; their
other region fields are identical. Three existing wrong-phase rows remain.
Wrong-scale, duplicate and failed-source residual row counts remain zero.
All observed model executions use CPU.

Validation passes **458 OCR tests**, with 16 existing optional skips, and
**486 App tests**. Six new cases cover brackets at two scales, complete
multiword captions, uncorroborated underlines, in-plot text, immutable evidence,
cancellation and separate recognition crops. The native tool builds with zero
warnings/errors. Checks take 97.09 seconds, native verification 146.44 seconds
and CSV verification 158.95 seconds. A helper-name collision stopped preparation
before inference; the historical file was preserved and the retry used unique
helper names. That record remains bound in the evidence.

Commands use the Release OCR/App test projects and the native tool's
`--run-frozen-candidate-synthetic` and `--score-frozen-workflow-csv` operations.
The [evidence record](GOAL-22-CAPTION-BRACKET-ASSEMBLY-REPAIR.json) binds exact
options, source, model/runtime checksums and results. Predecessor `d11b063`
passed CI 35633132854.

Accuracy still fails the unchanged 95% bar. Remaining OCR and marker coverage,
sealed prerequisites, real workflow/Chandler verification, model promotion and
both final 2.0.0 distributions are unfinished. No new dependency, model import,
training, private/sealed read, approval or package occurred. Apache-2.0 project
code and reviewed dependencies are retained. Build 433 stays 0.4.33; all four
Goal 22 outcomes remain incomplete.
