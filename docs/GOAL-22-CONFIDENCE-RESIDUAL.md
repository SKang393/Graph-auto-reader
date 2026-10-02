<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Nonlinear confidence-head V34 outcome

**FAIL. Keep V44/English/V27/V5.** The small nonlinear confidence residual
does not solve the precision failure on complete development.

The [diagnosis](GOAL-22-NONLINEAR-CONFIDENCE-DIAGNOSIS.json) preceded implementation.
The [single revision outcome](../ml/markers/center/confidence_residual_v34/P1_RESULT.json)
records full-population metrics and binds the complete local per-case audit.

## Isolated change

Add a 64-to-64 SiLU-to-1 confidence residual over the existing 64 features.
Its 4,225 parameters are trained; all 63,452 original V27 parameters and every
normalization buffer remain fixed, including original confidence weights.
Zero-initializing the residual output preserves the retained full-patch
predictions bit-for-bit before training.

All 276,618 owned training rows, labels and flags remain unchanged. The
unweighted BCE objective, optimizer, seed, eight epochs, 17,296 steps and
final-epoch selection match V33. No width sweep or threshold search occurred.

## Complete development

All 167 component and nine family scenes, 2,210 truths and 270,236 proposals
remain included. The current threshold stays 0.10 and shared precision/recall
bars stay 95 percent. Other thresholds remain descriptive.

| Split | Matched | Extras | Precision | Recall |
| --- | ---: | ---: | ---: | ---: |
| component | 1908/2004 | 643 | 74.79% | 95.21% |
| family | 206/206 | 166 | 55.38% | 100.00% |

The frozen geometry has a localized positive proposal for every native truth
when confidence is ignored. This is diagnostic opportunity, not attainable
recall: support, NMS, classifier and downstream exclusions can still reject it.
The nonlinear head's recall does not compensate for its precision failures.

## Complete application comparisons

Each native arm includes all 21 sources, 31 panels and 838 marker truths.
The compiled runtime, V5 classifier, OCR within each arm, original inputs and
thresholds remain fixed against 64f5749339cf56938deb59f67621b794467c04dc.

| OCR arm | Retained matches | V34 matches | Retained extras | V34 extras | Recovered truths | Lost truths |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| v44 | 745 | 787 | 29 | 41 | 42 | 0 |
| v46 | 749 | 791 | 23 | 34 | 42 | 0 |

Each CSV arm includes all 23 sources, 39 panels, 706 unique points and
724 relational rows. Failed sources stay in every denominator.

| OCR arm | Retained correct values | V34 correct values | Retained completed sources | V34 completed sources | V34 exported rows |
| --- | ---: | ---: | ---: | ---: | ---: |
| v44 | 414 | 384 | 15 | 14 | 403 |
| v46 | 429 | 399 | 16 | 15 | 418 |

The authenticated audit records all OCR, marker, axis, calibration and CSV
changes. Frozen per-patch geometry does not preserve confidence ordering,
NMS or final centers. Raw accepted-set additions/removals can include numerical
movement; use paired truth counts for recovered and lost markers.

## Verification and disposition

All 100 focused tests pass. They cover zero-residual identity, unchanged
geometry and buffers after training, cache admission, exact recovery and CPU
export with dynamic batch counts. The exporter emitted three recorded warnings
about its legacy API and the fixed input-shape check; batch sizes one and five
were verified. The unchanged application runtime retains its 1,402 passing
tests, 16 optional OCR skips and successful prior CI evidence.

Complete CPU export parity: maximum absolute error
6.19888306e-06; raw 0.10 boundary crossings:
0. Maximum geometry change against
retained ONNX: 3.81469727e-06.

Preflight: 26.374s. Training and complete development:
190.272s. Four workflow comparisons:
844.152s. Heavy work uses the existing
12-processor, Idle-priority, 80 percent guarded execution policy.

Owned Apache-2.0 code, model and synthetic inputs only. No dependency, imported
weight or private/sealed input was added. Canonical ledger row 98 closes
without consuming sealed budget; the previous 97 rows remain unchanged.

Goal22 remains incomplete. Build 433 stays 0.4.33. Accuracy, sealed prerequisites,
real acceptance, production activation and the final 2.0.0 installer/portable
distributions remain outstanding. No model approval, package, tag or release.
