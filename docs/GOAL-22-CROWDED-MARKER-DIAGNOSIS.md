<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Crowded marker diagnosis, 2026-09-21

Keep the current native result of 721/838 correct centers and 27 extras.
Its source checkpoint `5fe3cad` passes CI run 35642604511. This diagnosis
changes neither the application nor its models.

## Filled-symbol experiment is closed

A single 3x3 erosion of original ink, followed by the existing classifier,
adds 26 correct matches but 154 false points. The resulting 747 correct and
181 extra points give 80.50% precision. Reject this approach without integration
or a cutoff sweep. It takes 11.35 seconds and retains all 838 truths and every
previous match. A full CSV replay is unnecessary for this already failed
precision result.

## Training coverage differs from crowded development graphs

All twenty complete graphs used in V5 training have widely separated circles
and squares. Their 500 points have no overlapping bounding discs or neighboring
center inside a nominal classifier crop. All 500 rendered annotation centers
exactly match the training crop source coordinates.

The twenty-one current development graphs contain all nine shapes and 838
points. Of these, 84 have an overlapping bounding disc and 135 have another
center inside the nominal crop. Twenty-eight have at least two overlapping
discs. These are geometric proximity measures, not proof of visible ink
occlusion. The source-bound audit takes 0.19 seconds.

The smaller patch recipes already cover all nine shapes. Their neighbor
examples draw one neighbor, then the target on top. The native recipe separates
centers by 2.4 radii; the print-context recipe uses 2.1 to 3.2 radii horizontally.
This is a coverage limitation, not evidence that every remaining failure needs
new training. Previous V6 and V7 repairs remain rejected.

## Correct-geometry diagnostic separates two problems

One fixed diagnostic gives the existing V5 classifier the authored center and
radius for every owned data point, using the same original-pixel crop and
0.5 rejection cutoff. Truth supplies geometry here, so these results are not
native detection or acceptance scores.

| Diagnostic population | Points | Retained | Rejected |
| --- | ---: | ---: | ---: |
| Complete-graph training points | 500 | 500 | 0 |
| All current development points | 838 | 795 | 43 |
| Development points with overlapping discs | 84 | 55 | 29 |
| Currently missing native points | 117 | 84 | 33 |

Development shape/fill correctness is 752/838 and 792/838. Among the 148
hexagonal `other` markers, 39 are rejected and 84 receive the correct shape.
All points remain counted, including partial overlaps.

The completed diagnostic takes 10.34 seconds overall, with 4.74 seconds inside
the Python diagnostic. Every call uses CPU. The first attempt is retained as
void: classification finished, but the reporter called a nonexistent budget
method before saving results. The retry fixes reporting only and changes no
model, input, metric or cutoff. Its final measured work/rest cycle stays below
80%; this is not an instantaneous per-core utilization claim.

Next, inspect proposal and crop geometry for the 84 currently missing points
that this classifier retains at correct geometry. Keep the 33 remaining
classifier rejections visible. Do not start another training revision solely
from the proximity statistics.

The [evidence record](GOAL-22-CROWDED-MARKER-DIAGNOSIS.json) binds inputs,
source snapshots, scores and both attempts. No private or sealed data, training,
new weights, dependency, model activation or package is involved. Source and
owned synthetic examples retain Apache-2.0 provenance. Build 433 remains
0.4.33, and all four Goal 22 outcomes remain incomplete.
