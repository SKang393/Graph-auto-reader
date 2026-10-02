<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Confidence training duration V36 outcome

**FAIL. Keep V44/English/V27/V5.** Increasing the fixed training duration
does not clear the unchanged development acceptance bars.

The [diagnosis](GOAL-22-CONFIDENCE-DURATION-DIAGNOSIS.json) preceded implementation.
The [single revision outcome](../ml/markers/center/confidence_duration_v36/P1_RESULT.json)
binds complete development, application comparisons and the local per-case audit.

## Isolated change

Increase eight epochs to 24, starting from the same V27 initializer. All 276,618
owned training rows, labels, flags, seed, optimizer, objective and model remain
unchanged. The 49,281-parameter confidence projection adapts while all 63,452
original parameters and normalization buffers stay frozen. Exactly 51,888
optimizer steps complete before one final development evaluation. No scheduler,
intermediate development selection, early stopping or threshold change occurs.

The first eight epoch records reproduce V35 exactly:
True. Training loss falls from
0.07005690 at epoch eight to
0.04075944 at epoch 24. This is training-fit
and reproducibility evidence. It does not establish generalization.

## Complete development

All 167 component and nine family scenes, 2,210 truths and 270,236 proposals
remain included. The operating cutoff stays 0.10, with the existing 95 percent
precision/recall bars and 2 percent prohibited-structure maximum. Threshold
sensitivity remains descriptive.

| Split | Matched | Extras | Precision | Recall |
| --- | ---: | ---: | ---: | ---: |
| component | 1867/2004 | 194 | 90.59% | 93.16% |
| family | 200/206 | 75 | 72.73% | 97.09% |

The saved-output diagnosis reproduces every development match and attributes
every truth without new inference or optimization:

| Split | Diagnosis | Truths |
| --- | --- | ---: |
| component | above_threshold_support_lost_after_nms_or_matching | 45 |
| component | matched | 1867 |
| component | no_supported_localized_geometry | 10 |
| component | supported_geometry_below_threshold | 82 |
| family | matched | 200 |
| family | supported_geometry_below_threshold | 6 |

Nearby proposals are individual opportunities. They are not jointly attainable
recall because suppression and matching couple candidates. No case is removed.

## Complete application comparisons

Each native arm includes all 21 sources, 31 panels and 838 marker truths. Runtime
source and compiled binaries remain fixed at the tested application checkpoint
64f5749339cf56938deb59f67621b794467c04dc. OCR within each arm, V5 classification,
source images, mask-generation code and operating thresholds retain their bindings.

| OCR arm | Retained matches | V36 matches | Retained extras | V36 extras | Recovered truths | Lost truths |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| v44 | 745 | 755 | 29 | 23 | 30 | 20 |
| v46 | 749 | 758 | 23 | 18 | 29 | 20 |

Each CSV arm retains all 23 sources, 39 panels, 706 unique points and 724
relational rows. Failed sources remain in the denominator.

| OCR arm | Retained correct values | V36 correct values | Retained completed sources | V36 completed sources | V36 exported rows |
| --- | ---: | ---: | ---: | ---: | ---: |
| v44 | 414 | 414 | 15 | 15 | 426 |
| v46 | 429 | 429 | 16 | 16 | 440 |

The complete audit records changes to OCR, centers, axes, calibration and CSV.
Frozen per-patch geometry does not freeze confidence ordering, suppression or
selected centers. Raw candidate additions and removals can include numerical
movement; paired truth counts establish recovered and lost markers.

## Verification and disposition

All 137 focused tests pass, including fixed-duration configuration, exact
recovery, initialization, frozen geometry/buffers, cache admission and CPU
export. Three recorded exporter warnings concern the legacy API and static
input-shape validation. Dynamic batch counts one and five were verified.
The unchanged runtime retains its prior 1,402 passing tests and 16 optional
OCR skips; this trial does not claim a new application test execution.

Complete CPU export parity: maximum absolute error
6.19888306e-06, with
0 raw 0.10 boundary crossings.
Maximum geometry change against retained ONNX:
3.81469727e-06.

Preflight: 38.517s. Training and full development:
372.272s. Four application comparisons:
720.656s. Heavy work uses the existing
12-processor, Idle-priority, 80 percent guarded execution policy.

Owned Apache-2.0 code, model and synthetic inputs only. No dependency, imported
weight or private/sealed input was added. Canonical ledger row 100 closes
without consuming sealed budget; the previous 99 rows remain unchanged.

Goal22 remains incomplete. Build 433 stays 0.4.33. Accuracy, sealed prerequisites,
real acceptance, production activation and the final 2.0.0 installer/portable
distributions remain outstanding. No model approval, package, tag or release.
