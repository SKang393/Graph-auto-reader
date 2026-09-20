<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve calibrated session estimates in export

Printed-session export previously required a literal printed value on every
point. A graph labeled at sessions 1 and 7 could therefore calibrate an unlabeled
point at session 4, then fail export. The existing contract already distinguishes
printed values, estimated values and observation order.

Export now uses a finite printed value when present, or an explicit finite
estimated value whose source is `Estimated`. The audit preserves `estimated`
provenance. It never substitutes the graph-coordinate field or observation
index for a missing session value. Calibration, session-origin, finite-value,
membership and file-integrity requirements remain unchanged. Observation-order
mode remains an explicit reindexing choice. No contract/schema change is needed.

All 26 export tests and 341 application tests pass with no skips. Tests cover
mixed printed/estimated gaps, unchanged source evidence, JSON audit provenance,
unknown or inconsistent provenance, nonfinite estimates, absent calibration,
invalid origin, and the complete candidate workflow writing an inferred session.
The x64 tool builds with zero warnings/errors; the fictitious private-runner
self-test passes. An initial test used exact floating-point equality for a
weighted pixel center; its failure is retained and its comparison corrected.
The already-passing export suite was reused after verifying unchanged sources.

Actual model inference repeats all 23 synthetic source images. Every observed
axis, OCR region, accepted marker and calibration matches the preceding run,
excluding timing and run IDs. The three previously exported minimal CSVs are
byte-identical. Six sources now export instead of three. The final 80 rows
retain 22 printed and 58 estimated x sources.

| Complete workflow metric | Before | After |
| --- | ---: | ---: |
| Sources that export | 3 / 23 | 6 / 23 |
| Correct unique exported points | 3 / 706 | 49 / 706 |
| Missing unique points | 703 | 657 |
| Extra unique points under series matching | 3 | 17 |
| Correct relational rows | 0 / 724 | 28 / 724 |

The full denominator remains unchanged. Eighteen matched relational rows have
wrong phases. Seventeen sources still fail, and grouping continues to split
true series. This is failed development acceptance. All written artifacts pass
integrity checks; no point with missing evidence is granted a guessed value.

Next: align legend validation with grouping's existing unknown-fill support.
One measured failing panel has 18 open squares and one square with unknown fill;
grouping can legitimately retain them together, while legend validation currently
demands every individual fill equal the aggregate series fill. Preserve unknown
marker evidence and continue rejecting conflicting known fills and wrong shapes.

The [evidence record](GOAL-22-ESTIMATED-SESSION-EXPORT.json) binds exact sources,
snapshots, runtime, checks, model observations and full scoring. No training,
private/sealed read, model revision, approval, package or release occurs.
Goal 22 remains incomplete; the retained portable is build 433, version 0.4.33.
