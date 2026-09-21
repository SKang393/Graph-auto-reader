<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Existing V28 model on repaired native inputs

The existing V28 center model still cannot replace the V27 development
baseline. It finds more points in the open marker fixture but loses three
successful full-workflow exports. Its original failed outcome stays closed.

The comparison was justified by the intervening original-pixel plot, axis
corner, boundary and residual-mask repairs. It reused the existing licensed
payload and its frozen 0.25 threshold. The baseline uses V27 at 0.10, so this
compares complete frozen center configurations, not weights alone. Classifier,
OCR, masks, geometry, runtime assemblies and source images were identical.
There was no training, threshold search, private/sealed read or model import.

| Measure | V27 baseline | Existing V28 |
| --- | ---: | ---: |
| Matched native points, all 838 | 579 | 664 |
| Extra native points | 86 | 106 |
| Missing native points | 259 | 174 |
| Native precision | 87.07% | 86.23% |
| Native recall | 69.09% | 79.24% |
| Complete CSV exports, all 23 sources | 12 | 9 |
| Correct unique exported values, all 706 | 403 | 336 |
| Correct relational rows, all 724 | 399 | 332 |

The native point comparison retains 549 baseline matches, recovers 115 and
loses 30. Cross-shaped points improve substantially, while dense hexagonal
symbols and small hollow circles remain weak. The 174 missing points comprise
105 without a nearby above-threshold decoded center, 34 classifier rejections,
seven text exclusions and 28 suppression/matching losses. All 838 truths and
all 31 panels remain in the denominator. OCR and axis results are unchanged.

The full workflow retains every existing failure and adds three export
rejections with `WORKFLOW_RECALIBRATION_REQUIRED`. Those three saved automatic
calibrations are valid; the shared export projection guard also uses this code
for incomplete point/series evidence. This report does not mislabel them as
measured axis failures or bypass that guard. Wrong-scale rows and residual
exports from failed sources remain zero. All stages use CPU.

Native comparison takes 129.17 seconds and the full workflow 145.24 seconds.
Both finish with their expected failure exit code, preserving all outputs and
errors. The [evidence index](GOAL-22-REPAIRED-INPUT-V28-DIAGNOSIS.json) binds
the prior failed outcome, existing model/manifest/license, unchanged executable
inputs, all source and output comparisons, and zero training/private/sealed use.

Keep V27 as the development baseline. Continue repairing measured OCR assembly
defects; any future marker adaptation must address the observed input and
context distribution gaps and pass the existing gates. No production approval,
new model revision, packaged build or public release is produced. Build 433
remains 0.4.33. All four Goal 22 outcomes are not yet jointly verified.
