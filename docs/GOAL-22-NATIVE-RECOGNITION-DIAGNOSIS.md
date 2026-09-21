<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native OCR diagnosis, 2026-09-21

The open development diagnostic now preserves the recognizer's alternatives
and sequence warnings alongside final text. All prior predictions and aggregate
scores remain identical. Shared records suppress these fields from serialization;
only the explicitly authorized open owned-development command projects them.
The warning-free build and 50 boundary, 15 worker and 13 archive checks pass.
The complete build, checks and 21-source run took 117.91 seconds.

The trace identifies 21 numeric misreads among 50 matched text errors. Six
numeric readings are changed by sequence reconciliation: three are corrected
and three are damaged. In the damaged cases, correct high-confidence readings
are rescaled to fit another misread. The next repair must require independent
unchanged tick support and carry unresolved numeric-review warnings through
the application's export safety check. No digit can be inferred or fabricated.

Two other comparisons are closed. The Python crop probe reproduces only 65 of
74 native final readings; wider crops help some labels and hurt others, so it
does not establish native parity or justify a crop change. The already licensed
original mobile detector reads 295/453 labels exactly with 309 false detections,
versus the current V44 detector's 328/453 and 147 on identical owned inputs.
The current detector is retained. The control took 127.88 seconds.

See the [hash-bound aggregate evidence](GOAL-22-NATIVE-RECOGNITION-DIAGNOSIS.json).
No application behavior, weights, thresholds, licenses, dependencies or product
version changed. No private/sealed reads, training, promotion or packaged build.
All four Goal 22 outcomes remain incomplete. Build 433 stays 0.4.33.
