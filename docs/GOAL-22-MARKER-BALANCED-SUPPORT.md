<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Balanced support around marker centers

The existing geometry check could accept three ink samples from one nearby
legend wall as evidence for a marker center. Four such false centers had no
supported session value, so the scientific export guard correctly blocked
their three source graphs.

An explicitly bound development candidate now requires those ring samples to
bracket the decoded center on both axes. Weights, numeric thresholds, center
coordinates, central-ink and closed-outline alternatives, NMS and export
guards remain unchanged. Historical candidates retain their original behavior.
The candidate manifest and cache identify the new geometry rule explicitly;
real-data admission and Production activation remain closed.

All 414 application tests and 30 Python geometry tests pass. The Windows x64
checker builds with zero warnings/errors, and both fictitious runner checks
pass. Python and C# agree on all 580 saved candidates, including unchanged
legacy decisions: 575 remain and five are rejected. None of the five were
within the existing 5-pixel matching tolerance of authored truth. This limited
replay does not measure detector recall. An initial preparation script asked
for an unrelated axis test file and failed before inference; that failure and
its correction remain recorded.

| Same 23 graphs, weights and truth | Before | After |
| --- | ---: | ---: |
| Sources that export | 10 / 23 | 13 / 23 |
| Correct unique values | 178 / 706 | 360 / 706 |
| Correct relational rows | 170 / 724 | 352 / 724 |
| Extra unique points | 4 | 8 |
| Wrong phase rows | 5 | 5 |

The full comparison takes 150.31 seconds. All 33 prior observed panels remain,
with identical axis and OCR outputs. No previously completed source regresses
and no prior exported numeric value disappears. The newly exporting sources
also reveal four additional extra points, which remain counted. Matched values
have no scale errors, but 346 truth points remain missing and ten sources
still fail. This is a verified local repair; Goal 22 is incomplete.

The [aggregate evidence](GOAL-22-MARKER-BALANCED-SUPPORT.json) binds the
diagnosis, source snapshots, tests, parity and full workflow results. Continue
with fixed-weight calibration/OCR and marker coverage diagnosis. The five
remaining phase errors are visible unknown labels; do not guess scientific
labels or weaken confidence rules merely to improve the score.

Source remains Apache-2.0. No dependency, private/sealed read, optimizer step,
model revision, production approval or package occurs. Retained build 433
remains 0.4.33. All four Goal 22 outcomes still require final verification.
