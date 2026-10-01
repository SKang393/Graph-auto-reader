<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Complete existing glyph crops in two-heading rows

An already recognized A had a truncated 10-by-7-pixel crop inside one
14-by-15 horizontal ink component. The three-heading rule excluded its
two-heading row. The [diagnosis](GOAL-22-TWO-HEADING-COMPLETION-DIAGNOSIS.json)
records complete original-pixel preprocessing replay and preserves a rejected
preliminary inventory that had omitted non-single-character detections.

Completion now accepts two bound horizontal headings for an existing,
unreviewed, original-pixel single-character PhaseHeading. One unique horizontal
component must contain its fragment. Missing-glyph discovery and fragment-word
recovery retain their three-heading defaults. Row span, size, plot, collision,
human and protected-context checks remain. Actual crops are recognized in a
separate batch without supplied text. Failed or empty rereads retain the old
reading; numeric rereads cannot become calibration ticks. The cache version advances.

## Complete comparison

| Configuration | Exact text / 453 | Correct roles / 453 | Matched text boxes | Extra text boxes | Matched markers / 838 | Extra markers |
| --- | --- | --- | --- | --- | --- | --- |
| V44/English | 358 -> 358 | 379 -> 379 | 403 -> 403 | 54 -> 54 | 745 -> 745 | 29 -> 29 |
| V46/server | 428 -> 429 | 424 -> 425 | 444 -> 445 | 46 -> 45 | 749 -> 749 | 23 -> 23 |

Ten new completed crops retain their old literal text. One A now meets the
existing geometry match, reducing V46 character errors from 118 to 116/3,100.
No previous correct reading or marker match is lost. All 75 prior completions
retain every physical property and probability. One aggregate OCR confidence
changes by about 1.1e-16 through summation order after cache identities change.

Raw detector output is unchanged. Expanded masks alter intermediate marker
proposals on one V46 native panel. Three classified candidates change, but all
remain text-excluded and above the artifact cutoff. All accepted marker
properties, axis geometry and calibration remain identical. The complete audit
retains all intermediate differences, not only final metrics.

All 866 exported audit rows are unchanged apart from run identities, including
confidence, finite original-pixel traces and valid calibration. V44 retains
15/23 exported sources, 414/706 correct values and 409/724 correct rows. V46
retains 16/23, 429/706 and 416/724. Three V44 and eleven V46 wrong-phase rows
remain; wrong-scale, duplicate and failed-source residual rows remain zero.
These observations do not replace structural safety tests.

## Verification and limits

748 OCR and 607 App tests pass, with 16 optional OCR skips. Twelve new cases
cover paired headings, preserved three-heading discovery, original/bound
readings, horizontal components, independent peers and successful/failed
separate rereads. Three builds have zero warnings and errors. Checks take
105.706 seconds; four fixed comparisons take 852.006 seconds; the complete
audit takes 6.232 seconds. Runs use the existing hidden Idle, 12-CPU,
80-percent guard. Harness exit 1 represents retained review-blocked sources;
all sources remain scored.

The [bound result](GOAL-22-TWO-HEADING-COMPLETION.json) records exact source,
runtime, model, license, test and full-delta evidence. Apache-2.0 project
source only; no dependencies or weights change. Keep V44/English/V27/V5.
V46/server remains an existing closed comparison.

Goal 22 remains **FAIL, incomplete**. Recognition, roles, detection precision,
marker recall and complete exports still miss requirements. Multi-character
joined headings remain outside this repair. Sealed prerequisites, real
acceptance, production activation and both final Windows distributions remain.
No private/sealed data, training, package, tag or release. Build433 stays0.4.33.
