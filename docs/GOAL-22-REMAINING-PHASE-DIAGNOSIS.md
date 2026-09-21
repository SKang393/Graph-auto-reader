<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Remaining phase labels, 2026-09-21

All three remaining wrong-phase rows come from one owned synthetic graph.
Damaged heading letters prevent the reasoner from identifying the later
withdrawal phase. The application exports the explicit unknown label `phase5`
and records `later_phase_semantic_unknown`; the audit rows remain unreviewed.
It does not silently guess the expected `a3` label.

The diagnostic adds an observation callback to the canonical CSV evaluator.
It retains all 23 sources and 706 truth points and exactly reproduces every
existing aggregate metric. Each emitted mismatch is authenticated against the
actual export audit. The check takes 6.32 seconds with no model inference.
Correct phase assignments remain 399/402 matched relational rows, above the
95% reviewable-error bar. Overall point coverage and source completion still
fail: 403/706 correct unique values and 12/23 completed sources.

No reasoner change follows. The earlier exact-letter spacing repair remains
in place. Fuzzy spelling, phase-order guesses or hidden corrections would not
repair the underlying OCR failure. Continue text and marker coverage work.
The [evidence record](GOAL-22-REMAINING-PHASE-DIAGNOSIS.json) binds the original
scorer, instrumentation, inputs, outputs and export audit.

Source checkpoint e6c56b0 passed CI run 35638527641; a98a822 passed
35637264506. Existing Apache-2.0 code and licensed dependencies are unchanged.
No private/sealed read, training, model import, approval or package occurs.
Build 433 remains 0.4.33. All four Goal 22 outcomes remain incomplete.
