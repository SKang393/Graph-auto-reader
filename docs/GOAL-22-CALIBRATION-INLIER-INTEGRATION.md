<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Rejected OCR labels stay out of automatic calibration

A robust fit could reject an incorrect OCR number while the session lattice
and automatic anchor maxima still consumed it. Calibration now uses accepted
labels from a valid, unambiguous fit. Original OCR and rejection diagnostics
remain available for review. Ambiguous fits, unsupported session origins and
unknown point values still require review. Explicit caller maxima remain
supported; the automatic workflow no longer supplies raw OCR maxima as overrides.

All 57 axis, 371 application and 30 export tests pass without skips. The x64
tool builds with zero warnings/errors and its fictitious private-runner check
passes. An initial test-only compiler error is retained in the evidence; the
already passing axis checks were reused after verifying unchanged source bytes.

The same 23-source synthetic workflow takes 155.85 seconds. Raw axis, OCR and
marker evidence and original fitted transforms are unchanged. Two panels have
corrected lattice assignments and three have corrected automatic anchors.
Valid observed calibrations rise from 18 to 19, with no previously valid panel
regressing. The newly valid panel still has unsupported point evidence and its
export fails closed.

All 115 prior exported points retain identical values and x provenance. Scores
remain nine completed sources, fourteen failures, 85/706 correct unique values
and 77/724 correct relational rows. There are still 621 missing truth points,
30 extra predictions and five matched rows with wrong phases.

Next: give multi-panel exports distinct filenames while retaining participant
metadata, preview/write parity and protection against overwriting existing
files. Detection, grouping and remaining OCR/calibration failures also need work.

[Source bindings and full evidence](GOAL-22-CALIBRATION-INLIER-INTEGRATION.json)
retain the unchanged denominator and every failure. No training, private/sealed
read, model revision, activation or package occurred. Build 433 remains 0.4.33.
All four Goal 22 outcomes remain incomplete.
