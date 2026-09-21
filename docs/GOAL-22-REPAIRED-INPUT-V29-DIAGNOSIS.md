<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Existing V29 model on repaired native inputs

The existing V29 center model remains below the shared acceptance bars. The
repaired pipeline finds more native points, but the full export gains only one
correct value. Keep V27 as the development baseline; the original V29 failed
outcome remains closed and no model is approved by this diagnostic.

The intervening original-pixel, mask, provider and OCR repairs justify this
bounded rerun. Existing V29 weights and their frozen 0.25 threshold are compared
with V27 at 0.10. This compares two complete center configurations, not weights
alone. Source images, classifier, OCR, geometry, native libraries and managed
assemblies are identical. No training, threshold search, new weight import,
private access or sealed evaluation occurred.

| Measure | V27 baseline | Existing V29 |
| --- | ---: | ---: |
| Matched native points, all 838 | 579 | 637 |
| Extra native points | 86 | 98 |
| Missing native points | 259 | 201 |
| Native precision | 87.07% | 86.67% |
| Native recall | 69.09% | 76.01% |
| Complete CSV exports, all 23 sources | 12 | 12 |
| Correct unique exported values, all 706 | 403 | 404 |
| Correct relational rows, all 724 | 399 | 400 |
| Extra unique exported points | 14 | 11 |

The native comparison retains 533 baseline matches, recovers 104 and loses 46.
The 201 missing points include 153 without a nearby above-threshold decoded
center, 27 classifier rejections, eight text exclusions, seven pre-suppression
losses and six one-to-one matching collisions. Of the 153, 90 are in the
hexagonal/other-symbol cohort. All 838 truths and all 31 panels remain scored.
OCR is unchanged at 339/453 exact readings; axis geometry remains 85/93 corners
within two original pixels.

All source completion states remain unchanged in the full workflow. One
already failed source changes an error detail about the first observed session
column; it still lacks sufficient numeric calibration evidence. Exported
coordinates change across all 12 successful sources, so outputs are not
identical and previous point retention is not claimed. No wrong-scale rows,
duplicate rows or residual exports from failed sources occur. Three relational
rows still have wrong phases. All observed models use CPU on all 31 native and
39 full-workflow panels.

Native comparison takes 132.54 seconds and the full workflow 144.85 seconds.
Both complete with their expected failure exit code. The
[evidence index](GOAL-22-REPAIRED-INPUT-V29-DIAGNOSIS.json) authenticates 30
artifacts, the original failed outcome, model/manifest/license and unchanged
runtime inputs. The source checkpoint fa1862a passed CI run 35616871698.

Continue the measured missed-symbol and OCR work without retraining from this
failure alone. Any proposed marker-search change must be verified independently
on owned synthetic data under the existing bars. No production approval,
package or release is produced. Build 433 remains 0.4.33. All four Goal 22
outcomes are not yet jointly verified.
