<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Remaining coverage diagnosis, 2026-09-21

The original-pixel marker repair at `41b8a68` passed CI run 35629755178.
It remains the development baseline: 666/838 correct marker centers, 17
extras and 172 misses. Accuracy acceptance remains incomplete.

Two further marker approaches are closed without integration. Both compare
with their frozen earlier baseline of 569/838, rather than the integrated
666/838 result:

| Diagnostic | Result | Decision |
| --- | --- | --- |
| Restrict each isolated component's classifier crop to its measured bounds | 679 correct, 79 extras, versus 679 correct and 43 extras without isolation | Reject; 36 additional false points |
| Restore suppressed proposals on distinct complete components | Nine raw candidates; union 571 correct and 21 extras | Reject; only two additional correct matches and four extras, before classification |

These runs took 10.77 and 9.47 seconds. They do not justify relaxing duplicate
removal or classifier cutoffs. Keep the earlier failed results and do not
repeat these probes without a new cause or implementation change.

The largest missed-marker source contributes 104 of the remaining 172 misses.
Among its 148 authored markers, 100 match proposals before duplicate removal,
79 afterward, 46 after classification and 44 after text exclusion. These
counts locate losses but do not prove which operation is defective. Several
duplicate proposals can match different nearby truths within the five-pixel
tolerance. Global classification observations also include template additions.

Saved OCR observations contain 72 missing regions: 47 have no raw detector
overlap, 20 partial overlap, and five raw matches are lost later. Seventeen
are y-axis labels. A 9.48-second component replay, with no model inference,
shows that several of those labels have complete visible components but fail
the fallback's right-edge alignment assumption. Left-aligned labels keep
their first digit aligned while their widths change. Other misses involve
merged ink or missing anchors, so this finding does not explain all OCR errors.

The next repair supports either edge measured from existing y-axis labels.
It must still recognize the original pixels, retain existing readings and
mark recovered labels for review. No number may be supplied from an expected
sequence. Full application and CSV verification remain required for that change.

All evidence uses project-owned open synthetic data. No private/sealed reads,
training, new model imports, approval, dependency changes or packaged builds
occurred. Build 433 remains 0.4.33. Source, script and result bindings are in
the [evidence record](GOAL-22-REMAINING-COVERAGE-DIAGNOSIS.json).
