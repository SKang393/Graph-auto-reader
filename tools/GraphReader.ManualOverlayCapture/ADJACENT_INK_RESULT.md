<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Adjacent-ink tick review safeguard

The application now requests review when a nearby original-pixel ink
component lies just left of an unreviewed numeric Y-tick box and is large and
vertically aligned enough to be relevant. The OCR region and reading are
preserved, and a user-confirmed reading bypasses the warning. Unrelated ink or
a fully readable label can trigger it. Empty, uniform, invalid, or oversized
strips provide no positive signal; they do not prove a crop is complete.

On the frozen review of 115 detector-emitted regions, the native workflow
flagged three of five partial-number crops, the one graph-structure crop, all
three other-text fragments, and three of 105 complete readable numeric ticks.
The graph crop and two partial crops gained region-specific review warnings;
one complete numeric tick also gained a warning. The other flagged text and
numeric regions already had region-specific warnings. The two remaining partial
crops did not trigger this signal. The affected panels already required axis
review, so this does not claim that the earlier run exported an incorrect value.

Across the same 20 previously inspected images, ten warnings appeared on eight
panels. All 21 panels kept the same status, other failure reasons, center
observations, and accepted markers; all 20 images still failed calibration.
Recorded-point matches remained 250 centers and 207 accepted markers of 332
imperfect recorded points. This sample and its assistant-reviewed labels are
not independently adjudicated and do not establish precision or generalization.

Three new regression cases failed before the change and passed afterward. The
final application suite passed 775 tests; the integration suite passed 170 with
seven skipped. Nine Python scorer tests and pyflakes passed, and the diagnostic
Release build succeeded with zero warnings. CPU inference and image processing
were used; no optimizer steps or sealed-payload reads occurred. No model or
release gate was approved.

| Evidence | SHA-256 |
| --- | --- |
| Protocol | `1ec4b31bb4314d5cc4697bfe32369c22006c1155ce58adbfcf0a972026131339` |
| Pre-run registration | `47472ae621da125d9d8abedfdea23e93542afc8dfc46edd37e9b2eca866fae47` |
| Runtime description before and after | `548967f18b4b4b25de0968e0bd913b33c4a539867cb6ec2c09f8973eff2fe2bf` |
| Full workflow report | `95aed5f0d9274db083abba601f78a3d4992fbf4619e18fec4aca9ee2447493e8` |
| Visual-category and warning comparison | `737880ad98a5fadc416072f3016ee0842dc2b2f12ac8dca2f9d1f275b088ace3` |
| Recorded-point score | `856127d475c58374426aeb06439e167dcf78998b03e6a5d509ecf1fa473c0262` |

Private source images, labels, and per-region evidence remain project-local
while the remaining incomplete-crop investigation continues.
