<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Complete compact heading crops with measured orientation

A raw detector assigned a quarter-turn angle to a truncated 7-by-8-pixel
letter A. The existing completion rule rejected it despite a containing
11-by-10 horizontal ink component and a corroborated heading row. Its text
was already correct, but its polygon failed the existing geometric match.
The [diagnosis](GOAL-22-COMPACT-HEADER-ORIENTATION-DIAGNOSIS.json) binds the
original-pixel replay and its preserved preliminary failures.

The completion helper now permits this case for an unreviewed, single-character
PhaseHeading read from original pixels. A unique horizontal component must
contain the fragment. The existing three-heading, size, plot, collision and
protected-context checks remain. Existing horizontal angles are preserved.
The actual complete crop is recognized in the existing separate batch, with
no supplied text. Failed or empty recognition preserves the original reading;
a numeric reread cannot become a calibration tick. The cache version advances.

## Complete comparison

| Configuration | Exact text / 453 | Correct roles / 453 | Matched text boxes | Extra text boxes | Matched markers / 838 | Extra markers |
| --- | --- | --- | --- | --- | --- | --- |
| V44/English | 358 -> 358 | 379 -> 379 | 403 -> 403 | 54 -> 54 | 745 -> 745 | 29 -> 29 |
| V46/server | 427 -> 428 | 423 -> 424 | 443 -> 444 | 47 -> 46 | 749 -> 749 | 23 -> 23 |

All previous exact readings and marker matches remain. Fifteen successful
new crop completions preserve their respective literal text; one A now has
correct matching geometry. V46 character errors fall from 120 to 118 out of
3,100 characters. This is a geometry improvement, not a new character prediction.

All 60 previous completions remain. One has a secondary-alternative probability
difference below 0.000000002, while its primary probability, overall confidence,
text, role and polygon remain identical. Raw detector regions are unchanged.
Expanded OCR masks alter intermediate decoded marker proposals on two native
panels per arm and three V44 CSV panels. All post-filter candidate centers,
accepted marker properties, axis geometry and calibration remain unchanged.
The complete audit records these differences and cache-derived identity changes.

V44 still exports 15/23 sources with 414/706 correct values and 409/724 correct
rows. V46 remains at 16/23, 429/706 and 416/724. All 866 exported audit rows are
identical apart from run identities, including confidence, and retain finite
original-pixel traces and valid calibration. Three V44 and eleven V46 wrong-phase
rows remain; wrong-scale, duplicate and failed-source residual rows remain zero.
These observations do not replace the application's structural safety tests.

## Verification and limits

736 OCR and 607 App tests pass, with 16 optional OCR skips. Nineteen new cases
cover rotation, preserved horizontal angles, human/context protections,
component ambiguity, original pixels, failed rereads and cached crop replacement.
All three builds have zero warnings and errors. Checks take 112.996 seconds;
the four fixed comparisons take 873.096 seconds; the complete audit takes
5.553 seconds. Execution uses the existing hidden Idle, 12-CPU, 80-percent guard.
Harness exit 1 reflects retained review-blocked cases; every source remains scored.

The [bound result](GOAL-22-COMPACT-HEADER-ORIENTATION.json) records the exact
source, runtime, model, license, test and full-delta evidence. Apache-2.0 project
source only; no dependency or weights changed. Keep V44/English/V27/V5. V46 is
an existing closed comparison, not a newly selected production model.

Goal 22 remains **FAIL, incomplete**. Recognition, detection precision, marker
recall and complete exports still miss requirements. Two-heading layouts and
multi-character crops are outside this repair. Sealed prerequisites, real
acceptance, production activation and both final Windows distributions remain.
No private/sealed data, training, package, tag or release. Build 433 stays 0.4.33.
