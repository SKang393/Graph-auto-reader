<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Wide horizontal model orientation candidate

An opt-in, unapproved candidate retains a horizontal model angle when the
model polygon is at least 1.2 times as wide as tall. The default composition
is unchanged. The candidate has a distinct detector fingerprint and adapter
identity. The threshold was chosen after inspecting exposed training cases.

On the same 20 previously inspected training images, all 21 panels still
required calibration review. For 105 previously reviewed complete numeric
Y-tick regions, the candidate corrected one previously wrong reading, retained
104 numerically correct readings in total, introduced no numeric regression,
and lost no reviewed region. The other known wrong reading came from a nearly
square polygon and is outside this rule. The visual review is assistant-made,
not independent, and does not measure missed text or generalization.

Two previously region-specific competing-reading warnings disappeared: one on
the corrected region and one on a region whose correct reading stayed the same.
Another panel changed its X-tick review reasons. All panels retained their
status, center observations, and accepted markers. These warning changes make
the candidate unsuitable for automatic acceptance; it remains experimental and
requires additional review safeguards. No model or release gate was approved.

The initial scoring registration was amended before the capture report existed:
the latest baseline lacked detailed OCR regions, so region comparisons use the
earlier pinned detailed capture, while panel status and review-warning changes
use the latest baseline's structured failure reasons.

| Evidence | SHA-256 |
| --- | --- |
| Protocol | `04f05236af41b796e832d1108424570cea66005b9e3a72d6b73a0f414157541a` |
| Initial registration / amendment | `b7e964ffb35a4b8fc9c14d69247a7f42c7702adcd5ad87156fb7d6e6486eceb5` / `9ce615ea2fd15a10c1085e9795cd941cfe614f2b1aefd411232317080839187a` |
| Runtime description before and after | `c07ea104b122d31f91cbd87b6293adc4f711c18bdcf2508e723f975caae41ed7` |
| Candidate workflow capture | `14ea74095ea0f368ab8996f15186cb0559438b17e9af9ef6e787f0bf28df1247` |
| Aggregate comparison | `ae40c879a5050851d26c0d52378f4e95530631854c90fd097981994c143d829d` |

The OCR suite passed 848 tests with 13 skipped; the application suite passed
775 tests, and integration passed 170 with seven skipped. Two comparison tests
and pyflakes passed. The Release diagnostic build had zero warnings. The
capture returned exit code 1 because all 20 workflows required review. CPU
inference was used; no optimizer steps or sealed-payload reads occurred. Private
images, transcriptions, and per-region outputs remain project-local.
