<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Tick-lane predicate diagnostics

The optional synthetic tick-lane observation now records each failed seed
bounds check separately. A component can fail several checks, so these counts
overlap. A null bounds breakdown means classification did not run, not zero
rejections. The existing synthetic observation schema permits additive fields.
Normal candidate selection, OCR, calibration, and export gates are unchanged.

The full OCR suite passed 806 tests with 13 skips; subsequent focused OCR and
production-adapter tests passed after adding pipeline and JSON-shape coverage.
No thresholds, model weights, or release approvals changed. A fresh pinned
synthetic replay is still needed to determine which checks reject the V13
components and whether any actual labels can be recovered.
