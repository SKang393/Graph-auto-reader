<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Keep unreviewed symbol strings from hiding plotted markers, 2026-09-30

The server recognizer described a row of plotted markers using only symbols
and punctuation. Its eight text elements bypassed the single-glyph ambiguity
rule, and an OCR mask hid eleven previously matched markers. The
[diagnosis](GOAL-22-SYMBOL-RUN-MASK-DIAGNOSIS.json) authenticates their original
centers inside the mask.

The existing inside-plot rule now withholds masks for unreviewed Annotation or
Other regions composed only of Unicode symbols, punctuation and whitespace.
Literal text, role and geometry remain available for Review. Words, numbers,
other roles, outside-plot regions and reviewed decisions retain their existing
behavior. The single-glyph warning is preserved; symbol runs receive
`ocr_symbol_run_annotation_needs_review`. The cache identity advances to
`inside-plot-symbol-mask-review-v2`. No numerical threshold changes.

All **585 OCR and 520 App tests pass**, including 22 new cases. Sixteen existing
optional OCR tests remain skipped. OCR, App and native-runner builds have zero
warnings and errors. Checks take 124.411 seconds; the four
serial workflow comparisons take 779.777 seconds.
The first test build failed with `CS0122` because two tests accessed an internal
helper directly. The tests were corrected without widening the production API;
that failed build and the zero-inference evaluation remain recorded as void.

Each arm holds its four models, operating thresholds, original inputs, truth
and export guards fixed. V44 uses the retained English reader; V46 uses the
previously compared server reader. These are separate before/after controls.

| Measurement | V44 English before / after | V46 server before / after |
| --- | ---: | ---: |
| Native matched markers, 838 truth | 739 / 739 | 733 / 744 |
| Native extra markers | 26 / 26 | 20 / 20 |
| Exact OCR readings, 453 truth | 348 / 348 | 412 / 412 |
| CSV exports, 23 sources | 15 / 15 | 16 / 16 |
| Correct unique CSV values, 706 truth | 414 / 414 | 429 / 429 |
| Correct relational CSV rows, 724 truth | 409 / 409 | 416 / 416 |
| Wrong-phase CSV rows | 3 / 3 | 11 / 11 |

V46/server retains all 733 previous native matches and recovers eleven, with
no additional false points. Two earlier center/radius tuples also change:
one radius changes at the same center, and another center moves one pixel.
V44's accepted geometry, calibration and CSV values remain unchanged.

All OCR region values and axis geometry remain unchanged across 140 observed
panels. The new warning appears in two native and two CSV panels; one mask is
removed in each scope. V46/server updates calibration evidence in one native
and one CSV panel, with x/y transforms unchanged. One exported center shifts
0.536405 pixels and its y value changes by -0.017701506; that row still passes
the existing value tolerance. Other exported numeric tuples and all source
statuses and failure messages remain unchanged. All 866 exported rows retain
valid calibration and non-null x/y. Scored wrong-scale, duplicate and
failed-source residual rows remain zero. All failed sources remain scored.

The eleven phase errors still come from the previously documented
[damaged headings](GOAL-22-SERVER-READER-ROLE-DIAGNOSIS.md): missing evidence of
repeated baselines yields explicit unknown later phases. The current saved
exports confirm the same six `a1` to `a`, two `a2` to `phase3`, and three
remaining `phase5` rows. No new phase-reasoning change is justified.

Retain this runtime correction. V44/English/V27/V5 remains the model baseline;
V46 training remains closed and the server-reader configuration is unapproved.
The [outcome record](GOAL-22-SYMBOL-RUN-MASK-REVIEW.json) binds source snapshots,
tests, model licenses, protocols and actual outputs. Reproduction commands are
in `artifacts/goal22-tools/Run-SymbolRunMaskChecks-v2.ps1` and
`artifacts/goal22-tools/Run-SymbolRunMaskEvaluation-v2.ps1`.

Only owned Apache-2.0 source and synthetic inputs were used. No new dependency,
training, private/sealed read, activation, packaged build or release. WPF
interaction was not manually exercised. Build 433 stays 0.4.33; Goal 22 is
incomplete. Continue diagnosis of the remaining native marker and OCR errors.
