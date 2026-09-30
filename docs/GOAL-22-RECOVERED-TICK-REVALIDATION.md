<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Revalidate insufficient evidence after tick recovery, 2026-09-30

The OCR pipeline checked numeric alternatives before recovering missed tick
crops. Its insufficient-evidence warning then survived even when original
primary readings completed the existing checks. The repair rechecks that
specific warning after recovery, preserving every reading, confidence,
alternative, polygon, mask and other review cause. It requires original,
unreviewed literal numeric primaries with positive finite confidence, unchanged
output from the existing alternative search, and a complete monotonic fit with
no discarded tick. Numeric replacements and conflicting or ambiguous evidence
still require review. The result-cache identity advances.

A saved-output replay reproduced all 70 baseline controls in 64.267 seconds
without model inference. The implemented native workflow confirms the result:

| Measurement | Before | After |
| --- | ---: | ---: |
| CSV sources exported | 12/23 | 15/23 |
| Complete calibration panels | 24/39 | 27/39 |
| Correct unique values | 409/706 | 414/706 |
| Correct relational rows | 405/724 | 409/724 |
| Extra points / extra rows | 11 / 12 | 12 / 14 |
| Wrong-phase rows | 3 | 3 |
| Wrong-scale / duplicate / failed-source residual rows | 0 / 0 / 0 | 0 / 0 / 0 |

All earlier successful exports preserve numeric values, phases and row counts.
The three newly exporting train sources add six rows, including five correct
unique values and four correct relational rows. One extra point and two extra
rows remain counted. Train values increase from 253 to 258/500 and rows from
249 to 253/500. Dev stays at two exports, 156/206 values and 156/224 rows.
No matched value is scored numerically incorrect. Eight sources still fail.

All 21 native sources, 31 native panels, 23 CSV sources and 39 CSV panels remain
observed. OCR regions, masks, confidences, axes, raw proposals and classified
markers are unchanged. Native retains every earlier match: 733/838 markers,
25 extras, 348/453 exact OCR strings and 85/93 axis corners. Six obsolete
warnings retire across both runs; every other OCR warning is preserved. Five
calibration records change, including three NeedsReview-to-Valid transitions.
No incomplete calibration is silently exported.

All **519 OCR and 510 App tests** pass; 16 existing optional OCR payload tests
remain skipped. Nine added cases exercise both axes, cached results,
non-numeric/contradictory/equal-confidence alternatives, missing tick strokes,
zero-confidence readings and earlier numeric replacements. Builds have zero
warnings/errors. The first build stopped on CA1859 for a private collection
parameter; its concrete HashSet type repairs that void attempt. The queued
first evaluation stopped before inference.

Final checks take 135.25 seconds. Native and CSV
inference take 166.18 and
274.12 seconds; the complete workflow sequence
takes 458.37 seconds. All observed model
providers are CPU under the shared resource guard. Commands are the OCR/App
Release test projects and the native runner's frozen synthetic workflow and
CSV-scoring modes, bound in the [evidence record](GOAL-22-RECOVERED-TICK-REVALIDATION.json).

Existing Apache-2.0 source, synthetic inputs and reviewed models retain their
licenses. No new dependency, training, private/sealed read, production switch,
packaged build or release occurred. WPF interaction was not manually exercised.
Retain V27/V5. Accuracy and Goal 22 remain incomplete; build 433 stays 0.4.33.
