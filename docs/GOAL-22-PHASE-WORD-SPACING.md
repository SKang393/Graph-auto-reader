<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Separate phase words using measured original-ink gaps

Some composite header crops contain a phase word and an adjacent letter whose
gap exceeds the word's internal character gaps but is smaller than glyph height.
The previous wide-gap rule could not separate them. In one small graph, the
letter also straddles the plot edge while its center remains inside. The
[diagnosis](GOAL-22-PHASE-WORD-SPACING-DIAGNOSIS.json) binds the original-pixel
replay, visual inspection, fixed inputs and unchanged acceptance policy.

The existing wide-gap behavior remains. A narrower boundary now requires the
unchanged classifier to recognize the remaining literal word as a phase heading,
a positive gap greater than every internal component gap, and the glyph center
inside the plot. Unknown words and participant names cannot use this path.
Original/source binding, human review, context, size, alignment and collision
checks remain. Actual glyph and word crops are recognized in separate batches,
without supplied text. Only a complete successful pair replaces the original.
Failed or empty reads retain it; numeric rereads cannot become calibration ticks.
The existing composition cache version advances.

## Complete comparison

| Configuration | Exact text / 453 | Correct roles / 453 | Matched text boxes | Extra text boxes | Matched markers / 838 | Extra markers |
| --- | --- | --- | --- | --- | --- | --- |
| V44/English | 358 -> 358 | 379 -> 379 | 403 -> 403 | 54 -> 54 | 745 -> 745 | 29 -> 29 |
| V46/server | 429 -> 431 | 425 -> 427 | 445 -> 446 | 45 -> 45 | 749 -> 749 | 23 -> 23 |

Seven composite crops reread as separate B and Intervention regions: three
CSV cases per arm and one V46 native case. All three previous successful
separations retain their physical properties and probabilities. No previous
exact reading or marker match is lost. V46 exact text reaches 95.14 percent;
character errors decrease 116 to 113/3,100. Roles remain 94.26 percent. Passing one
development bar does not approve or reopen the closed V46 comparison.

Raw detector regions are unchanged. Text masks alter intermediate proposals on
three CSV panels per arm and one V46 native panel. One classified native
candidate changes but remains text-excluded with artifact probability 1 before
and after. Every accepted marker property, axis and calibration remains unchanged.
The complete audit retains all intermediate differences and changed identities.

All 866 exported audit rows remain identical apart from run identities, including
confidence, finite original-pixel traces and valid calibration. V44 remains at
15/23 exported sources, 414/706 correct values and 409/724 correct rows. V46 remains
at 16/23, 429/706 and 416/724. Three/eleven wrong-phase rows remain. Wrong-scale,
duplicate and failed-source residual rows remain zero. These observations do
not replace the application's structural safety tests.

## Verification and limits

769 OCR and 607 App tests pass, with 16 optional OCR skips. Twenty-one new cases
cover both sides, internal-gap comparisons, plot-edge centers, unknown words,
participant names, protected contexts, human decisions, separate actual crops,
failed rereads and cached replacement. Three builds have zero warnings and errors.
Checks take 115.866 seconds; four fixed comparisons take 910.596 seconds; the
complete audit takes 5.708 seconds. Runs use the hidden Idle, 12-CPU, 80-percent
guard. Harness exit 1 represents retained review-blocked sources; all are scored.

The [bound result](GOAL-22-PHASE-WORD-SPACING.json) records source, runtime,
model, license, test and complete-delta evidence. Apache-2.0 project source only;
no dependencies or weights change. Keep V44/English/V27/V5.

Goal22 remains **FAIL, incomplete**. Roles, detection precision, marker recall
and complete exports still miss requirements. Sealed prerequisites, real
acceptance, production activation and both final Windows distributions remain.
No private/sealed data, training, package, tag or release. Build 433 stays 0.4.33.
