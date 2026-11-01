<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Original-image candidate workflow diagnostic

The `--describe-workflow` and `--capture-workflow` options run the fixed 20
unmodified training images through candidate axis, OCR, artifact-mask, and
marker-center adapters, the checksum-resolved approved marker classifier, and
the existing downstream workflow. The composition remains unapproved. It
records the marker-stage observation even if a later stage fails, but does not
read truth during capture. The 15-image build and five-image internal holdout
are both within the authorized training split. `--capture-workflow-smoke`
selects only the first image. `--capture-workflow-one` selects one ID already
present in the pinned manifest and records private exception details. Neither
single-image run can be scored as the full run.

All 20 images failed the full workflow. Nineteen of 21 imported panels reached
the marker stage and then returned `WORKFLOW_RECALIBRATION_REQUIRED`. Their
calibration statuses were nine invalid session origins and ten requiring
review. The other two panels failed with an unstructured `OverflowException`
before the marker-stage observation. The underlying overflow requires a
separate diagnosis; it is not a completed workflow or a release pass.

Across all 332 unique recorded points, including the two failed panels, the
observed center stage matched 200 and the later accepted marker stage matched
161. On the 15 build images those counts were 155 and 121 of 253; on the five
internal holdout images they were 45 and 40 of 79. The 19 reached panels
reproduced the center geometry and confidence of the independently captured
center-only run. The missing stage observations remain misses in the full
denominator. The workflow produced zero final points because all panels failed
before completion. Recorded points are imperfect and incomplete, so these
figures do not establish precision, series correctness, CSV correctness, or
generalization.

The first integration attempt selected the parent model-store directory, which
the production resolver correctly rejected because it contained an unlisted
nested package. The second used the package but compared an uppercase resolved
model hash case-sensitively to a lowercase binding. The fixed tool uses the
verified package root and case-insensitive checksum comparison. It locks and
revalidates the exact classifier index, manifest, weights, notice, and approval
evidence while running.

| Evidence | SHA-256 |
| --- | --- |
| Workflow runtime description before and after capture | `7c851951abf42047f112136fdacd4932e39ea70644019cb237c437c46b9c78f9` |
| Full workflow report | `a873df755b5b80e16974cff8638ae7b4a97b23acf6b67b874cfe81aecd447aee` |
| Workflow point score | `87ba6473974bcff34733108bfa7c8f2ae18b90e421c537c7f84ac7035114e515` |
| Approved classifier payload | `26f9304f1689053a0b94aa896a1e239f6ade1e5c1920736a3535c1b32f803b8a` |

The Release diagnostic build passed with no warnings, and 21 Python tests plus
pyflakes passed. The score tests cover a failure after marker observation and
an import failure with no panels, preserving the truth denominator in both
cases. The diagnostic used CPU inference, performed no optimizer steps, read no
sealed payloads, and did not change production approval. Private inputs,
predictions and per-image errors remain under ignored project-local artifacts
while the overflow and calibration investigation consumes them.

## Session-lattice range repair

The two overflows were traced to converting an out-of-range first observed
session into a 32-bit integer. The check now returns insufficient evidence
without producing an absolute origin or fabricated assignments. A separate
regression also exposed the upper boundary of pitch quantization: the double
representation of `long.MaxValue` rounds to an unrepresentable value. That
boundary is now rejected before the integer conversion. Both positive and
negative extreme-session regressions and the pitch-boundary regression failed
before the repair; all 121 axis tests passed afterward.

On the two original failing images, the repaired candidate workflow reached
classification and returned `WORKFLOW_RECALIBRATION_REQUIRED` with insufficient
session evidence instead of throwing. A complete rerun reached the marker stage
on all 21 panels. All 20 images still failed the workflow: all 21 panels now
returned the structured recalibration-required result. Nine had invalid
session origins, ten needed review, and two had insufficient evidence. Twenty
panels retained axis-review flags. No final points were produced.

With all 21 marker-stage observations available, center matches were 250/332
and accepted-marker matches were 207/332. The build split matched 181 and 145
of 253; the internal holdout matched 69 and 62 of 79. All 21 center-stage
observations reproduced the earlier frozen center-only geometry and confidence.
The earlier post-classification total of 161/332 included two panels that had
not reached classification, so the increase is stage coverage rather than a
classifier or accuracy improvement. Incomplete point annotations still prevent
a full precision measurement.

| Repair evidence | SHA-256 |
| --- | --- |
| Runtime description before and after rerun | `fe5a7fbd942cef2d6a93ff6e66abd9b4a336b51d985275215a352e31526533c5` |
| Full workflow report | `e25bb86d3de93e0959bb9f7c5af18ba7c08eea9edd2d1c253f7fb4e7489797ef` |
| Workflow point score | `b250ec10493d45044891121d0990fe36c86a94021c52d989cf8b075329399a54` |

The full solution check, `dotnet test GraphAutoReader.slnx -c Release
--no-restore -m:1 -p:UseSharedCompilation=false`, passed 2,381 tests, skipped
25, and failed none. The 21 Python diagnostic tests also passed. A single-source
diagnostic for a source outside the pinned manifest was rejected before it
created an output directory. CPU inference was used; no training or sealed
payload read occurred.
