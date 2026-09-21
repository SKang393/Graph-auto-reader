<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Numeric tick review safeguard, 2026-09-21

Sequence reconciliation could replace correct readings such as `100` and `200`
with decimal alternatives to fit another misread. The application could then
accept that regular sequence despite an unresolved OCR warning. This was a
scientific-value safety defect.

Numeric replacements now need two unchanged readings at distinct pixel
positions and cannot discard contradictory ticks. Every actual numeric
replacement still requires review: the regularity used to select an alternative
cannot independently validate it. Original alternatives remain available.
The application carries these warnings into calibration validity and blocks
automatic export. Cache identities advance so old results cannot bypass this
behavior. No frozen schema or numerical acceptance threshold changes.

The final check passes 418 OCR tests, with 16 existing optional payload tests
skipped, and 447 application tests. The native diagnostic build has zero
warnings and errors. On the same 21 open owned sources, exact text improves
from 328 to 330 of 453. Detector geometry, recognizer alternatives, final region
geometry and roles remain identical. This still fails OCR acceptance. The
unchanged three-source baseline remains 177/183 exact and 175/183 correct roles.

The complete 23-source, 39-panel native workflow now exports 12 graphs and
requires calibration review for 11. Previously it exported 17 and failed six.
Five additional graphs are blocked by unresolved numerical evidence. Correct
unique values fall from 479/706 to 403/706, and correct export rows from
474/724 to 399/724. This is a visible loss of automatic coverage, not an
accuracy pass. No value changes in the retained exports, no wrong-scale rows,
and no residual export artifacts from failed cases were found. The workflow
command returns exit code 1 because those failures are preserved.

The first safeguard attempt still allowed one incorrect replacement anchored
by two other misreads. It is superseded by the final mandatory review rule.
Final tests, build and open OCR checks took 231.07 seconds. The queued native
workflow and audit took 349.35 seconds including its wait; inference itself
took 147.04 seconds. The native workflow self-test also passes.

See [aggregate evidence and exact input/source hashes](GOAL-22-NUMERIC-TICK-REVIEW.json).
Existing Apache-2.0 project code and previously reviewed model payloads are
unchanged in licensing. No new dependencies, models, training, private or
sealed reads, promotion, package, or release occurred. Build 433 stays 0.4.33.
The models still miss and misread labels; WPF manual review was not exercised
by these checks. All four Goal 22 outcomes remain incomplete.
