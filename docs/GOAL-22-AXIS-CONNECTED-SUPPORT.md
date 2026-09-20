<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Connected axis support

Detached collinear strokes could enlarge an axis across empty space. Three
owned synthetic graphs extended about 28 pixels into a caption. Two other
graphs merged vertically separated axis evidence. The exact existing OpenCV
runtime reproduced all 30 saved geometries with zero corner difference before
the repair. A failed native-loader diagnostic attempt remains recorded.

Axis fitting now separates connected support using the existing corner-contact
tolerance. It refits each component before selecting the axis pair. Dotted
phase dividers retain the original line families. Short gaps and overlapping
axis fragments remain supported. The stage/cache version becomes
`axis-opencv-v3-connected-support`. No model, threshold, contract or scientific
export guard changes.

All 60 axis tests and 404 application tests pass. The Windows x64 checker builds
with zero warnings/errors and both fictitious runner checks pass. An initial
test compile failure from CA1861 is retained; constant test arrays were moved
to static readonly fields without weakening the analyzer.

Replaying the same 30 cached line sets takes 1.33 seconds without image or model
inference. Twenty-four plot polygons stay unchanged, including all eight
development panels. The three caption errors fall to about 1 pixel. A heavily
broken axis remains incomplete, and one formerly merged source selects a
different physical panel. That selection is assessed by the whole-source
comparison, not against the previous panel's top coordinate. An initial
comparison omitted crop offsets; its corrected aggregate record retains that
error and does not rerun inference.

| Same 23 sources and fixed model weights | Before | After |
| --- | ---: | ---: |
| Sources that export | 9 / 23 | 10 / 23 |
| Correct unique values | 140 / 706 | 178 / 706 |
| Correct relational rows | 132 / 724 | 170 / 724 |
| Extra unique points | 4 | 4 |
| Wrong phase rows | 5 | 5 |

The complete run takes 134.11 seconds. All 32 prior observed panels remain
available, with 33 reached in total. All 23 sources, failures, 706 points and
724 expected rows remain counted. Artifact integrity passes. There are still
528 missing points and thirteen failed sources. No previously completed source
regresses. This is a verified boundary repair, not Goal 22 acceptance.

The [aggregate evidence](GOAL-22-AXIS-CONNECTED-SUPPORT.json) binds source
snapshots, tests, diagnostics, complete workflow and CSV scoring. Next, inspect
remaining OCR/calibration failures, unknown-session marker artifacts, empty
grouping and phase errors with the V4 classifier weights fixed.

Source remains Apache-2.0 and the reviewed OpenCV runtime is unchanged. No new
dependency, private/sealed read, optimizer step, model revision, production
approval or package occurs. Retained build 433 remains 0.4.33. All four Goal 22
outcomes remain incomplete.
