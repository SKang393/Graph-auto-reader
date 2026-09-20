<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve uncertain marker fill during legend reasoning

Grouping already permits an unresolved fill inside a same-shape series whose
other markers establish the fill. Legend validation instead required every
individual fill to equal the series fill, stopping a measured synthetic panel
containing 18 open squares and one square with unknown fill.

Legend reasoning now accepts that existing grouping behavior. The individual
marker remains unknown and produces an explicit unresolved-fill warning.
Conflicting known fills, mismatched shapes and invalid series membership still
fail validation. The legend algorithm version advances to 0.1.1; the public
contract version remains 1. No model or operating threshold changes.

All 41 legend tests and 341 application tests pass without skips. The Windows
x64 diagnostic tool builds with zero warnings/errors. The private-runner
self-test passes using fictitious inputs. Tests explicitly retain unknown fill
and reject known conflicts, wrong shapes and wrong ownership.

The first workflow attempt was void: the frozen binding retained legend 0.1.0
and correctly rejected the 0.1.1 executable before inference. The failed setup
is preserved. A fresh attempt binds the algorithm identity read from that
same verified executable; no validation was weakened or sealed budget consumed.

Actual inference on the unchanged 23 synthetic sources takes 152.02 seconds.
All axis, OCR, marker and calibration observations in the previous 25 panels
are unchanged. The legend failure clears, allowing two more panels to run,
before a later calibration failure stops that source. Three unresolved-fill
warnings survive in the workflow report. Previously completed minimal CSVs
remain byte-identical.

Completed exports remain 6/23. Full scoring remains 49/706 correct unique
points and 28/724 correct relational rows, including 18 wrong phase rows.
All output artifacts pass integrity checks. These are failed development
acceptance results; progress through a later panel is not product acceptance.

Next: repair phase-boundary evidence integration. Existing observations show
real evenly spaced dividers retained as ambiguous grid/phase lines, while
unrelated short line fragments can become a full-height divider. Inspect actual
heading and line evidence before resolving either case. Do not invent phase
boundaries from expected answers.

The [evidence record](GOAL-22-UNKNOWN-FILL-INTEGRATION.json) binds sources,
compiled snapshots, tests, the retained void attempt, runtime and complete
scoring. No training, private/sealed read, model revision, activation, package
or release occurred. All four Goal 22 outcomes remain incomplete. The retained
portable is build 433, version 0.4.33.
