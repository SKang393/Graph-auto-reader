<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recognize qualified phase headings, 2026-09-30

An owned synthetic graph had valid calibration and 24 accepted markers, but
export failed with `NO_EXPORTABLE_SERIES`. OCR correctly read “Review
Intervention”, “Review Withdrawal” and “Review Reintroduction”, then assigned
them `Other` because the role gate required an exact full label.

The role classifier now accepts its existing full phase terms at the end of
an above-plot label, with a whitespace boundary. Withdrawal and reintroduction
semantic normalization uses the same boundary. Explicit annotation context,
inside-plot behavior, damaged-word rejection and trailing-narrative rejection
remain covered. No prefix whitelist or guessed text is introduced. The OCR
behavior identity advances to `graph-text-role-classifier-v9-qualified-headings`
and phase stage to `0.1.3`; schema version remains 1.

Checks pass **547 OCR tests, 53 phase tests and 520 App tests**, with 16 existing
optional OCR skips. All four builds have zero warnings and errors. Checks take
140.332 seconds; four serial native/CSV comparisons take
720.013 seconds. Each arm keeps its model
payloads, thresholds, source images and truth unchanged. All 140 observed
panels retain their axis geometry, calibration, initial marker centers and
accepted marker geometry.

| Measurement | V44 before / after | V46 before / after |
| --- | ---: | ---: |
| Native matched markers, 838 truth | 739 / 739 | 743 / 743 |
| Native extra markers | 26 / 26 | 19 / 19 |
| Exact OCR readings, 453 truth | 348 / 348 | 387 / 387 |
| Correct native text roles | 360 / 360 | 392 / 394 |
| Extra native text regions | 108 / 108 | 127 / 129 |
| Character edits, 3100 truth characters | 728 / 728 | 379 / 382 |
| CSV exports, 23 sources | 15 / 15 | 13 / 14 |
| Correct unique CSV values, 706 truth | 414 / 414 | 257 / 281 |
| Correct relational CSV rows, 724 truth | 409 / 409 | 252 / 276 |
| Wrong-phase CSV rows | 3 / 3 | 3 / 3 |

V44 retains every source status, failure message and numeric export value.
V46 gains exactly one export with 24 correct values and rows, with no previous
export removed or changed. Its newly recognized “Shared Baseline” also allows
two spurious text-recovery regions, “n” and “nt”, in a dense native case.
Those extras and their three inserted characters are counted, not discarded.
All 712 exported rows across both arms have valid calibration and non-null
x/y; scored wrong-scale, duplicate and failed-source residual rows remain zero.
All failed cases remain in the scoring denominator. The recovered export is
in the synthetic train split; neither arm's CSV dev score changes.

Retain this generic runtime correction and keep V44/V27/V5. V46's training
revision stays **failed_dev_unconsumed** and is not promoted. The
[diagnosis](GOAL-22-QUALIFIED-PHASE-HEADING-DIAGNOSIS.json) and
[outcome evidence](GOAL-22-QUALIFIED-PHASE-HEADINGS.json) bind source snapshots,
runtime assemblies, tests, protocols and full scores. Reproduction uses
`artifacts/goal22-tools/Run-QualifiedHeadingChecks.ps1` and
`artifacts/goal22-tools/Run-QualifiedHeadingEvaluation.ps1`; their saved logs
and immutable run directories are bound by the evidence.

Project-owned Apache-2.0 source and synthetic inputs only; no dependency or
model payload change, training, private/sealed read, production activation,
packaged build or release. WPF interaction was not manually exercised. Build
433 remains 0.4.33. Goal 22 remains incomplete. Continue with measured legend
text-box overlap and the remaining calibration, marker and OCR failures.
