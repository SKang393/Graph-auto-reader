<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Unweighted confidence-head V33 outcome

**FAIL. Keep V44/English/V27/V5.** The single objective change reduces V32's
false-detection surge but does not clear the unchanged development bars.

The [diagnosis](GOAL-22-CONFIDENCE-OBJECTIVE-DIAGNOSIS.json) preceded implementation.
The [single revision outcome](../ml/markers/center/confidence_bce_v33/P1_RESULT.json)
binds the complete development and application audits. The full per-case
synthetic deltas remain in the referenced local comparison report.

## Isolated change

Replace the weighted focal objective with equal per-row binary cross-entropy.
Keep the same retained V27 initializer, all 276,618 training rows, their labels,
64 frozen features and 65 trainable confidence parameters. Every geometry
parameter and normalization buffer remains fixed. The seed, eight epochs,
17,296 steps, optimizer, batch size and final-epoch selection are unchanged.

At score 0.10, the old objective's expected logit gradient is zero at a positive
fraction of approximately 0.000493 for ordinary negatives or 0.002458 for hard
negatives. Unweighted BCE gives 0.10. This explains different score pressure;
it does not prove probability calibration on the native proposal population.
All rows, including mined negatives, remain admitted without resampling.

## Complete development

The operating threshold stays 0.10. All 167 component and nine family scenes,
2,210 truths and 270,236 proposals remain in the evaluation. Shared per-item
precision and recall bars stay 95 percent. Other thresholds are descriptive.

| Split | Matched | Extras | Precision | Recall |
| --- | ---: | ---: | ---: | ---: |
| component | 1901/2004 | 672 | 73.88% | 94.86% |
| family | 205/206 | 146 | 58.40% | 99.51% |

V32 component precision was 18.05 percent; retained V27 is 83.87 percent on this
same operating point. The loss change alone does not resolve the failure.

## Complete application comparisons

Each native arm includes all 21 sources, 31 panels and 838 marker truths. The
application runtime, V5 classifier, each OCR configuration and thresholds stay
fixed against checkpoint 64f5749339cf56938deb59f67621b794467c04dc.

| OCR arm | Retained matches | V33 matches | Retained extras | V33 extras | Recovered truths | Lost truths |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| v44 | 745 | 782 | 29 | 89 | 40 | 3 |
| v46 | 749 | 786 | 23 | 84 | 40 | 3 |

Each CSV arm includes all 23 sources, 39 panels, 706 unique truth points and
724 relational rows. Failed sources remain in every denominator.

| OCR arm | Retained correct values | V33 correct values | Retained completed sources | V33 completed sources | V33 exported rows |
| --- | ---: | ---: | ---: | ---: | ---: |
| v44 | 414 | 388 | 15 | 14 | 402 |
| v46 | 429 | 403 | 16 | 15 | 417 |

The audit records every changed OCR, marker, axis, calibration and CSV stage.
Frozen per-patch geometry does not preserve confidence ordering, NMS or final
centers. Small numerical coordinate changes can also appear as raw set
additions/removals; use the paired truth counts for recovered and lost markers.
Finite rows with valid calibration do not alone establish correct values.

## Verification and disposition

All 83 focused tests pass, covering the objective, exact recovery, unchanged
geometry and cache admission. The unchanged native runtime retains 1,402
passing tests, 16 optional OCR skips and prior successful CI. Complete CPU
export parity has maximum absolute error 6.19888306e-06;
raw 0.10 boundary crossings: 0.
Maximum geometry-output difference against retained ONNX:
3.81469727e-06.

Preflight: 30.858s. Training and complete development:
171.512s. Four workflow comparisons:
862.195s. All heavy work uses the existing
12-processor, Idle-priority, 80 percent guarded execution policy.

Project-owned Apache-2.0 source, model and synthetic data only. No dependency,
imported weight or private/sealed input was added. Canonical ledger row 97
closes without sealed-budget consumption; all previous 96 rows are unchanged.

Goal22 remains incomplete. Build 433 stays 0.4.33. Accuracy, sealed prerequisites,
real acceptance, production activation and the final 2.0.0 installer/portable
distributions remain outstanding. No model approval, package, tag or release.
