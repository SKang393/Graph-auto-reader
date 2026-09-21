<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native marker input diagnosis and observed axis corners

The corner repair recovers seven development points without losing any
previous matches or exact text readings. It does not pass the model gates
or complete Goal 22.

## Defect and repair

The native preprocessing replay reproduces all 31 panels and 148,122 emitted
proposals with matching original pixels, executable assemblies, residual-mask
category counts and proposal counts. No inference or training is needed for
that replay. Of 199 truths without a nearby above-threshold decoded center:

- 14 have no emitted proposal within five pixels;
- 49 have a completely clear nearby input patch;
- 87 have a clear center with masks elsewhere in the patch;
- 49 have a mask at every nearby proposal center.

The first group exposes two wrongly bounded plots. Their visible vertical
axis terminates near a symbol, leaving a short original-ink tail that neither
native line detector reports. Geometry therefore chooses an interior phase
divider as the vertical axis and excludes the earlier points.

The provider now recovers that terminal connection from a long observed
perpendicular line. It requires a connected path through the original pixels
within the existing local bridge range. Empty gaps remain disconnected.
Corners already supported within the existing line-merge tolerance receive
no additional weighting. Images, model weights and model thresholds remain
unchanged. The axis stage identity advances to
`axis-opencv-v7-observed-corner-connections`.

## Verified results

| Measure | Before | After |
| --- | ---: | ---: |
| Matched native markers, all 838 truths | 561 | 568 |
| Extra native markers | 82 | 84 |
| Missing native markers | 277 | 270 |
| Previously matched truths lost | - | 0 |
| Printed axis endpoints within two original pixels, all 93 | 82 | 85 |
| Panels with all three endpoints within two pixels, all 31 | 22 | 23 |
| Exact OCR readings, all 453 labels | 330 | 330 |
| Correct OCR roles | 344 | 350 |

The independent 23-source CSV check preserves 12 completed exports and 11
calibration-review failures. Aggregate results remain 403/706 correct unique
values and 399/724 correct relational rows. Wrong-scale rows and residual
exports from failed sources remain zero. Three first-point records change
across two sources, with the largest y-value change approximately 0.3723.
The exported values are therefore not byte-for-byte unchanged; their scored
accuracy and source statuses are unchanged.

Validation passes 84 axis tests, 463 application tests and 92 inference tests.
One optional model-store inference test is skipped, not counted as passing.
The native runner builds with zero warnings and errors. Tests and build take
103.97 seconds; the native comparison takes 134.34 seconds; the full CSV
check takes 145.56 seconds. These compiler outputs are not packaged builds.

## Retained failures and next work

The first corner implementation also reweighted already supported corners,
losing one prior point and one exact OCR reading. The final restriction removes
both regressions. An intermediate test run rejected an overly broad contact
tolerance before model execution. Both attempts remain in the evidence index.
Two preprocessing-replay plumbing failures are also retained; neither reads
private or sealed data.

The saved masks additionally label small X-shaped symbols as connecting-line
intersections. Sixty missing cross truths are near such regions, whose arms
fit the existing compact-symbol span. This is descriptive proximity evidence,
not a claim that a mask repair will recover all sixty. The next isolated repair
preserves those ambiguous short crossings and verifies actual model output.

The [evidence index](GOAL-22-NATIVE-MARKER-INPUT-AND-CORNER-REPAIR.json) binds
source, assemblies, fixtures, models, every comparison, timing and prior
attempts. No acceptance bar, private/sealed access, training, production
approval or package changes. All material remains project-owned Apache-2.0
code or synthetic evidence using existing reviewed dependencies and models.
Build 433 remains 0.4.33. All four Goal 22 outcomes are not yet jointly verified.
