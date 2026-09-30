<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve OCR mask eligibility in marker recovery, 2026-09-30

Template and enclosed-center recovery treated every OCR region as excluded
text, even when OCR deliberately kept a possible plot symbol unmasked for
Review. After the V46 detector change, all 24 lost marker matches had their
previously accepted centers covered by OCR regions; 23 regions had no text
mask. Sixteen replays using the exact previous runtime verified identical
original pixels and unchanged masks within 20 pixels of 22 lost truths.
The suspected legend masks were not the main cause.

Both recovery paths now consume the eligible masks selected by the same
`ProductionTextMarkerExclusion.SelectMasks` used for detection. Rejected and
blank OCR remain ineligible. Eligible text masks and verified legend frames
still exclude recovery. Original-pixel measurement, classifier approval,
duplicate suppression, calibration and export guards are unchanged. Recovery
version strings advance so stage provenance records the behavior change.

All **520 App tests pass**, including ten new eligibility cases. App and
native-runner builds have zero warnings and errors; checks take
107.233 seconds. Full native and CSV comparisons run
serially for the retained V44 and frozen V46 detector arms. Each uses exactly
its own previous model payloads, thresholds, source images and truth. The
four runs preserve 88 source executions and 140 panel observations, including
every failed source, and take 697.359 seconds.

| Measurement | V44 before / after | V46 before / after |
| --- | ---: | ---: |
| Native matched markers, 838 truth | 733 / 739 | 715 / 743 |
| Native extra markers | 25 / 26 | 17 / 19 |
| CSV source exports, 23 truth | 15 / 15 | 13 / 13 |
| Correct unique CSV values, 706 truth | 414 / 414 | 257 / 257 |
| Correct relational CSV rows, 724 truth | 409 / 409 | 252 / 252 |
| Extra CSV points | 12 / 12 | 5 / 5 |
| Extra CSV rows | 14 / 14 | 7 / 7 |

One-to-one native truth comparisons: V44 has 732 retained, 7 gained, 1 lost; V46 has
715 retained, 28 gained, 0 lost. The gain is not a claim that every previous match is retained.
The repair restores 23 of the 24 marker matches lost by the original V46
change. V44's single loss occurs in the dense 148-marker source: a previously
recovered center is absent after recovery changes, while six other matches
are gained there. The complete per-truth record remains in the evidence.
Axes, OCR regions, eligible masks and initial center detections are unchanged
across all 140 panel observations. Exact OCR stays 348/453 for V44 and 387/453
for V46. The repair changes downstream recovery only.

V44 retains every source status, failure message and exported numeric value.
Its development split remains 156/206 correct values and 156/224 correct rows.
V46 development has 23/206
correct values and 23/224 correct
rows. Neither arm emits a scored wrong-scale, duplicate or failed-source
residual row. Every exported row has valid calibration and non-null x/y.
Other calibration, OCR, classification and grouping errors remain visible
in the full scores and failure records.

Retain this runtime repair and keep V44/V27/V5. The V46 training revision
remains **failed_dev_unconsumed**; this diagnostic replay neither reopens nor
promotes it. The [diagnosis](GOAL-22-RECOVERY-MASK-DIAGNOSIS.json) and
[evidence](GOAL-22-RECOVERY-MASK-ELIGIBILITY.json) bind the source, runtime,
tests, protocols and scores. The initial diagnostic launcher expected six
single-panel cases but the affected cases contained eight panels per arm;
that error was repaired before replay and its successful build was reused.

The diagnosis used CRLF bytes during execution; Git stores the same JSON with
LF line endings. The evidence record binds both the preserved execution input
and the repository copy. Frozen protocols, results and their checksums remain
unchanged; no validation run was repeated for this serialization correction.

Licensing is unchanged: project-owned Apache-2.0 code and owned synthetic
inputs, with the same reviewed models and dependencies. No training,
private/sealed read, production activation, packaged build or release occurred.
WPF interaction was not manually exercised. Build 433 remains 0.4.33. Goal 22
remains incomplete; continue from the remaining measured native and CSV errors.
