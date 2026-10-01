<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recover markers suppressed by rejected artifacts

The [frozen diagnosis](GOAL-22-MARKER-SUPPRESSION-RECOVERY-DIAGNOSIS.json)
reproduces the original suppression and 3,794 classifier outputs. A candidate
later rejected as an artifact can suppress a useful neighboring proposal.
Legend symbols require separate treatment: being far from plotted truth does
not make a legend glyph a classifier error.

The workflow now reconsiders those proposals after its existing template and
enclosure recovery. Every blocking initial center must have been rejected by
the unchanged classifier. The proposals must clear the existing plot, text
and legend exclusions. New classifier-accepted markers keep the original
suppression distances, and existing accepted markers have priority. The
ordinary stroke-gap, calibration, review and export checks still follow.
Internal evidence supports both approved and candidate adapters; production
does not rely on candidate-only diagnostics. The cache identity includes the
new recovery version. No model, crop, threshold or schema changes.

## Complete comparison

| Configuration | Native matches | Extra markers | Retained / recovered / lost truths | Exact OCR | Exports | Correct values | Correct rows |
| --- | --- | --- | --- | --- | --- | --- | --- |
| V44/English | 740 -> 745/838 | 28 -> 29 | 740 / 5 / 0 | 358/453 | 15/23 | 414/706 | 409/724 |
| V46/server | 744 -> 749/838 | 22 -> 23 | 744 / 5 / 0 | 427/453 | 16/23 | 429/706 | 416/724 |

Retain this additive repair. Every previous accepted marker and its scientific
classification properties remains present. Each native arm adds six centers:
five true recoveries and one false detection. Precision remains above 95 percent;
recall remains below 95 percent. The shared acceptance bar is unchanged.

Five additional CSV candidates per arm occur on already-failed sources and
produce no exported rows. All 866 previous exported audit rows are identical
apart from run identities, including numeric values and confidence. Every row
has valid calibration and finite original-pixel traces. Wrong-scale, duplicate
and failed-source residual row counts remain zero in these runs. Three V44
and eleven V46 wrong-phase rows remain.

Axis geometry and every saved OCR field are unchanged. Calibration and marker
properties change on three native panels and four CSV panels per arm. The
separate mechanism audit proves that initial detector outputs are unchanged,
each addition existed before suppression, each original blocker was rejected,
and the original spacing is preserved against all old and new accepted markers.

## Verification and timing

607 App tests pass without skips, including 21 new cases and an approved-path
test that recovers an authored cross through Review and Export. Both builds
have zero warnings and errors. Checks take 88.2748139 seconds; the four workflow
comparisons take 943.4846057 seconds; the complete-delta audit takes 5.8629684 seconds.
Recorded recovery selection/classification envelopes sum to about 1.744 seconds.
These measurements do not establish a controlled performance guarantee.

The first build failed CA1861 on a test assertion. The second attempt passed 606
tests and failed the new fixture before marker detection because its OCR fake
expected a black raster. The repaired fixture preserves its white PNG and uses
three session columns. Both failures and exact commands are retained; production
source is byte-identical across all three attempts.

The [bound result](GOAL-22-MARKER-SUPPRESSION-RECOVERY.json) records source,
runtime, model, license, failure, test and complete-comparison evidence.
Source and tests retain Apache-2.0 notices; no dependency or weights were added.
Keep V44/English/V27/V5. V46 remains a closed comparison.

Goal 22 acceptance remains **FAIL**. Remaining accuracy and workflow failures,
sealed prerequisites, real acceptance, production activation and final Windows
distributions are unfinished. No private/sealed data, optimizer, package, tag
or release was used. Build 433 remains 0.4.33.
