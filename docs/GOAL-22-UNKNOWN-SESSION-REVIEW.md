<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Show unknown sessions consistently

Two saved synthetic sources fail export because a detected marker lies outside
the supported session lattice. The general axis fit is valid, but it does not
establish that marker's session. Export correctly rejects these points. Review
nevertheless received a numeric x value from a fallback axis calculation.

The detection adapter now preserves unknown x values in Review and the retained
domain record. It keeps the measured marker location, calibrated y value,
observation order and review evidence. Printed and explicitly estimated sessions
are unchanged. No threshold or export guard is relaxed. The detection adapter
identity advances to v3 so its changed projection is distinguishable.

All 419 application and 36 export tests pass. The new complete-workflow fixture
proves that a valid general calibration can coexist with an unresolved marker,
that both Review and retained data keep x unknown, and that export emits no
artifacts. The x64 checker builds without warnings or errors, and its fictitious
workflow check passes. Validation takes 91.85 seconds. The first fixture exceeded
the global alignment limit and was corrected without changing production
tolerances; its failed check remains recorded.

[Evidence and source hashes](GOAL-22-UNKNOWN-SESSION-REVIEW.json) are retained.
No model inference, training, native UI inspection, private/sealed read,
activation or packaged build was performed for this repair. The latest actual
model measurement remains the preceding phase-spacing run. Model accuracy and
acceptance-adapter gaps still require work. Build 433 remains 0.4.33; all four
Goal 22 outcomes remain incomplete.
