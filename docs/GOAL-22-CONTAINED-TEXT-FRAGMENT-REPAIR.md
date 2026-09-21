<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Remove redundant text fragments, 2026-09-21

OCR now removes an unreviewed fragment when a longer reading already contains
its entire rectangular crop and its exact text. For example, a second reading
of `ollo` inside the full `Follow-up` label is redundant. Matching is case
sensitive and ignores whitespace only. Numbers, tick roles, reviewed text,
rejected parent readings, different image evidence, irregular polygons and
conflicting text remain visible. This runs within one panel after recognition;
recognition crops and model settings stay unchanged. Result-cache identity
includes the new composition version, and removed region IDs remain in warnings.

The initial diagnosis replayed saved open synthetic predictions. Inspection of
the original pixels confirmed six fragments inside complete labels. Native
execution then verified the same six removals without altering any surviving
reading, calibration or marker match.

| Check | Before | After |
| --- | ---: | ---: |
| Matched text regions | 389/453 | 389/453 |
| Exact text / correct roles | 345 / 357 | 345 / 357 |
| Extra / missing regions | 116 / 64 | 110 / 64 |
| Character errors | 795/3100 | 780/3100 |
| Matched / extra markers | 666/838 / 17 | 666/838 / 17 |
| Axis corners within two pixels | 84/93 | 84/93 |

The separate 23-source, 39-panel CSV check removes one duplicate fragment and
preserves all other readings and calibration records. Exported values, source
states and errors are identical: 12 exports, 11 failures, 403/706 correct unique
values and 399/724 correct relational rows. Three existing wrong-phase rows
remain; wrong-scale, duplicate and failed-source residual rows remain zero.
All observed model executions use CPU.

Validation passes **476 OCR tests** with 16 existing optional skips and
**486 App tests**. Eighteen new cases cover nested fragments, literal numbers,
tick roles, reviewed regions, conflicts, partial overlap, image evidence,
nonrectangular geometry, cancellation and pipeline/cache/mask consistency.
The native tool builds with zero warnings/errors. Checks take 98.98 seconds,
native verification 145.70 seconds, and CSV verification 158.65 seconds.

Commands use the Release OCR/App test projects and the native tool's
`--run-frozen-candidate-synthetic` and `--score-frozen-workflow-csv` operations.
The [evidence record](GOAL-22-CONTAINED-TEXT-FRAGMENT-REPAIR.json) binds exact
options, source, models, native runtime and results. Predecessor `0328e49`
passed CI 35635412244. Source review was performed by the lead agent alone.

Accuracy remains below the unchanged 95% requirement. OCR/marker coverage,
sealed prerequisites, real workflow/Chandler verification, production promotion
and both final 2.0.0 distributions remain unfinished. No new dependency, model
import, training, private/sealed read, approval or package occurred. Apache-2.0
project code and reviewed dependencies remain. Build 433 stays 0.4.33; all four
Goal 22 outcomes remain incomplete.
