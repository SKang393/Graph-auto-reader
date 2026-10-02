<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Spatial confidence projection V35 outcome

**FAIL. Keep V44/English/V27/V5.** The independent confidence projection
does not solve the precision failure on complete development.

The [diagnosis](GOAL-22-SPATIAL-CONFIDENCE-DIAGNOSIS.json) preceded implementation.
The [single revision outcome](../ml/markers/center/confidence_spatial_v35/P1_RESULT.json)
records full-population metrics and binds the complete local per-case audit.

## Isolated change

Train a separate 768-to-64 SiLU-to-1 confidence projection initialized from
retained V27 weights. Its 49,281 parameters adapt while all 63,452 original V27
parameters and normalization buffers remain fixed. Both branches share one
convolution pass; geometry uses the original projection and output layer.
Across every cached training row, initialization changes confidence by at most
1.1920929e-07; raw threshold crossings:
0. This is numerical parity,
not a claim of bit identity.

All 276,618 owned training rows, labels and flags remain unchanged. The
unweighted BCE objective, optimizer, seed, eight epochs, 17,296 steps and
final-epoch selection match V34. No width sweep or threshold search occurred.

## Complete development

All 167 component and nine family scenes, 2,210 truths and 270,236 proposals
remain included. The current threshold stays 0.10 and shared precision/recall
bars stay 95 percent. Other thresholds remain descriptive.

| Split | Matched | Extras | Precision | Recall |
| --- | ---: | ---: | ---: | ---: |
| component | 1889/2004 | 233 | 89.02% | 94.26% |
| family | 202/206 | 75 | 72.92% | 98.06% |

The frozen geometry has a localized positive proposal for every native truth
when confidence is ignored. This is diagnostic opportunity, not attainable
recall: support, NMS, classifier and downstream exclusions can still reject it.
The adapted projection's recall does not compensate for its precision failures.

The saved-output audit reproduces every development match. Its 119 misses
comprise 68 with supported geometry below the confidence cutoff, 41 lost after
suppression or matching, and ten 1-pixel markers without supported geometry.
Every truth has a nearby decoded proposal before the support check. These are
diagnostic categories; all cases and the shared bars remain unchanged.

## Complete application comparisons

Each native arm includes all 21 sources, 31 panels and 838 marker truths.
The compiled runtime, V5 classifier, OCR within each arm, original inputs and
thresholds remain fixed against 64f5749339cf56938deb59f67621b794467c04dc.

| OCR arm | Retained matches | V35 matches | Retained extras | V35 extras | Recovered truths | Lost truths |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| v44 | 745 | 771 | 29 | 21 | 32 | 6 |
| v46 | 749 | 774 | 23 | 17 | 31 | 6 |

Each CSV arm includes all 23 sources, 39 panels, 706 unique points and
724 relational rows. Failed sources stay in every denominator.

| OCR arm | Retained correct values | V35 correct values | Retained completed sources | V35 completed sources | V35 exported rows |
| --- | ---: | ---: | ---: | ---: | ---: |
| v44 | 414 | 416 | 15 | 15 | 426 |
| v46 | 429 | 431 | 16 | 16 | 440 |

The authenticated audit records all OCR, marker, axis, calibration and CSV
changes. Frozen per-patch geometry does not preserve confidence ordering,
NMS or final centers. Raw accepted-set additions/removals can include numerical
movement; use paired truth counts for recovered and lost markers.

## Verification and disposition

All 127 focused tests pass. They cover initialization parity, unchanged
geometry and buffers after training, cache admission, exact recovery and CPU
export with dynamic batch counts. The exporter emitted three recorded warnings
about its legacy API and the fixed input-shape check; batch sizes one and five
were verified. The unchanged application runtime retains its 1,402 passing
tests, 16 optional OCR skips and successful prior CI evidence.

Complete CPU export parity: maximum absolute error
6.19888306e-06; raw 0.10 boundary crossings:
0. Maximum geometry change against
retained ONNX: 3.81469727e-06.

Preflight: 36.401s. Training and complete development:
213.478s. Four workflow comparisons:
877.531s. Heavy work uses the existing
12-processor, Idle-priority, 80 percent guarded execution policy.

Owned Apache-2.0 code, model and synthetic inputs only. No dependency, imported
weight or private/sealed input was added. Canonical ledger row 99 closes
without consuming sealed budget; the previous 98 rows remain unchanged.

Goal22 remains incomplete. Build 433 stays 0.4.33. Accuracy, sealed prerequisites,
real acceptance, production activation and the final 2.0.0 installer/portable
distributions remain outstanding. No model approval, package, tag or release.
