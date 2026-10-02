<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recognize existing role cues across the left axis

A horizontal participant label or measurement title can extend across the left
axis while its center lies inside the plot. The previous center-only geometry
missed five correctly located labels in the server comparison. The
[diagnosis](GOAL-22-LEFT-AXIS-ROLE-DIAGNOSIS.json) records those measured boxes,
the full mismatch inventory, fixed inputs and unchanged acceptance policy.

Existing participant and measurement cues now use crossing box geometry. The
participant cue also applies above the plot when the box crosses the left axis.
Literal text, explicit context, numeric expectations and orientation retain
precedence. Unknown crossing text keeps its previous fallback. Measurement
titles still carry a review warning. The classifier cache version advances.

## Complete comparison

| Configuration | Exact text / 453 | Correct roles / 453 | Matched markers / 838 | Extra markers |
| --- | --- | --- | --- | --- |
| V44/English | 358 -> 358 | 379 -> 381 | 745 -> 745 | 29 -> 29 |
| V46/server | 431 -> 431 | 427 -> 432 | 749 -> 749 | 23 -> 23 |

All five diagnosed labels receive the intended role. V46 roles reach 95.36
percent and exact text stays at 95.14 percent. Every literal reading, polygon,
alternative and review status is unchanged, including Participant O1. Only
roles, confidence and associated review warnings change on two V44 native and
three V46 native panels. No CSV panel changes. Every text mask, marker stage,
accepted marker property, axis and calibration is retained.

All 866 exported audit rows remain identical apart from run identities,
including confidence and valid original-pixel traces. V44 remains at 15/23
exported sources, 414/706 correct values and 409/724 correct rows. V46 remains
at 16/23, 429/706 and 416/724. Three/eleven wrong-phase rows remain. Wrong-scale,
duplicate and failed-source residual rows stay zero. These observations do not
replace the application's structural safety tests.

## Verification and limits

795 OCR and 607 App tests pass, with 16 optional OCR skips. Twenty-six new cases
cover crossing cues, boundary positions, unknown-text fallback, explicit
contexts, numeric expectations, orientation and pipeline review warnings.
Three builds have zero warnings and errors. Checks take 112.245 seconds;
four fixed comparisons take 993.567 seconds; the complete audit takes 5.299
seconds. Runs use the hidden Idle, 12-CPU, 80-percent guard. Harness exit 1
represents retained review-blocked sources; every source is scored.

The [bound result](GOAL-22-LEFT-AXIS-ROLE.json) includes source, runtime, model,
license, test, mechanism and complete-delta evidence. Apache-2.0 project source
only; no dependencies or weights change. Keep V44/English/V27/V5.

Goal22 remains **FAIL, incomplete**. Detection precision, marker recall and
complete exports still miss requirements. Sealed prerequisites, real acceptance,
production activation and both final Windows distributions remain. No private
or sealed reads, training, package, tag or release. Build 433 remains 0.4.33.
