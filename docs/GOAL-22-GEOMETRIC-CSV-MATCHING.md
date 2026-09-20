<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Stable geometric CSV evaluation

The same saved export received different relational scores after its run UUIDs
changed. Its marker classifier had split one true series into two equally sized
predicted series. The evaluator used UUID ordering to break that matching tie.

Series are now ordered by their complete sorted source-pixel coordinates.
Point matching orders predictions by source coordinates and resolves equal
distance truth neighbors by coordinates. UUIDs remain provenance only; values,
phase answers and relation correctness do not select a geometric match.
Matching cardinality, tolerances, checksums and complete denominators are
unchanged. Exactly coincident geometry remains inherently ambiguous.

All 35 evaluator scenarios and 21 binding scenarios pass. New regressions rename
series/point identifiers and reverse rows while retaining split-series errors
and overlapping point neighborhoods. The tool build has zero warnings/errors.

Both saved 23-source workflow outputs now produce identical values for every
metric: 1/706 correctly exported unique points, 705 missing, one extra and
0/724 correct relational rows. Both retain valid artifact integrity. Offline
scoring takes 0.19 and 0.18 seconds; no model inference is repeated.

This repairs measurement, not graph reading. Next, enforce existing OCR text
geometry at the plotted-marker boundary, preserve rejected candidates for
review, and evaluate the unchanged model outputs. No training, private/sealed
read, model revision, approval, package or release occurs. Goal 22 remains
incomplete and the retained portable is 0.4.33.

The [evidence record](GOAL-22-GEOMETRIC-CSV-MATCHING.json) binds source snapshots,
build, self-tests and both old and repaired scores. Earlier scores remain intact.
