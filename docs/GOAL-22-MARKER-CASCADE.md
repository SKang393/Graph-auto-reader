<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Marker proposal cutoff diagnostic

The complete native-context pipeline misses fourteen development markers,
thirteen of them triangles. Saved tensor evidence shows that several localized
triangles fail the center cutoff before reaching the classifier. Earlier shape
and background training candidates remain failed; their runs are not repeated.

One [registered diagnostic](GOAL-22-MARKER-CASCADE-TRIAL.json) lowers the proposal
cutoff from 0.25 to 0.10 before the unchanged V4 classifier. All four model weights,
source images, geometry, classifier cutoff, calibration checks and truth stay
fixed. The alternate cutoff requires an explicit synthetic-only factory, exact
manifest and binding. Adapter, cache and operating identities distinguish it;
counters retain the literal 0.25 count separately from the chosen cutoff count.
The default and approved factories remain unchanged. Real admission rejects it.

All 425 application tests and six fictitious checker commands pass in 94.80
seconds, with no compiler warnings/errors. Tests cover cutoff boundaries,
manifest drift, cache identity, unsupported values and production/real guards.
The full workflow and saved-output comparisons take 151.46 seconds plus scoring.

| Final marker output | Default 0.25 | Trial 0.10 |
| --- | ---: | ---: |
| Development matches / 206 | 192 | 200 |
| Development extra points | 0 | 1 |
| Development precision | 100% | 99.50% |
| Development recall | 93.20% | 97.09% |
| Train matches / 500 | 485 | 486 |
| Train extra points | 11 | 19 |

All 39 panels remain observed, and every axis and OCR result is unchanged.
However, exporting sources fall from twelve to ten. Correct unique values fall
from 322/706 to 191/706, and correct relational rows from 316/724 to 185/724.
Two failed sources become exportable; four previously exporting sources fail
because an unresolved point blocks the source export. Every failure remains
counted. This is a measured regression, despite better marker recall.

Keep 0.25 as the default. The 0.10 result is an unapproved diagnostic, not a
selected production operating point. Its final-pipeline center measurements
do not replace failed component-model gates or authorize sealed/real reads.
Continue diagnosis of Review/export handling and the additional false points.
Preserve validation of calibration and uncertainty in x values.

[Outcome evidence](GOAL-22-MARKER-CASCADE-OUTCOME.json) binds source, checks,
models, metrics, scoring helpers and unchanged denominators. The preceding
diagnostic checkpoint passed GitHub run 35544347530. No training, dependency,
private/sealed read, approval or package occurs. Apache-2.0 code and existing
reviewed payloads remain in use. Build 433 remains 0.4.33; all four Goal 22
outcomes remain incomplete.
