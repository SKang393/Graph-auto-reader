<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Participant-row suffix experiment, 2026-10-29

This unapproved candidate reads a longer original-pixel crop beside the plot,
while carrying the original text anchor through mask and marker exclusion.
The implementation remains isolated in the `open-archive-ocr-20261029`
worktree. It is not selected for production.

On a 21-source open archive, the baseline and candidate used the same models,
archive and composition. Exact readings rose from 284 to 287 of 453, and
correct participant roles from 5 to 8 of 31; false detections rose from 80 to
81. Source and executable hashes were checked before and after the run.

A separate frozen synthetic workflow comparison used matching current baseline
and candidate builds. The 21-source native and 23-source CSV fixtures are
different cohorts and are not combined. The saved fixtures' older phase adapter
was explicitly rebound to the same current adapter in both arms. The native
cohort retained all 21 failures in both arms, with 358 of 453 exact OCR
readings and 745 of 838 marker positions matched in each. No native export
score was available for this cohort.

The CSV cohort completed 15 sources and failed 8 in each arm. Against the
same 706-point truth, both produced 414 correct point values and 409 correct
rows out of 724; both had 14 extra rows and three wrong phase rows. Source
status and exported-row comparisons detected no changes. These remain failing
workflow results, not real-data or release acceptance. The candidate did not
improve the scored workflow and adds an open-fixture false detection, so the
runtime patch is not promoted.

The open-archive result and paired workflow/scoring receipts are under the
resume worktree's `artifacts/goal22-runs/participant-row-anchor-open-20261029-v1`,
`participant-row-paired-20261029-v3` and
`participant-row-paired-score-20261029-v1`. The paired score SHA-256 is
`d4d91c4cc8abdcf35e79561ffe8c05a7b1177d972f9cba993e0136ae1f1fb244`.
The open result SHA-256 is
`9fdb375bdcf423e35d1238047d2c40c2128316de7ab3365d0d923789eb991e6a`.
The earlier failed preparation and runtime attempts remain in their original
directories. No private or sealed corpus was read; no model was approved or
packaged. The previously consumed sealed child failure remains unresolved.
