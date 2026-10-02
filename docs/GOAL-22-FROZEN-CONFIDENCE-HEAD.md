<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Frozen confidence-head V32 outcome

**FAIL. Keep V44/English/V27/V5.** The fixed confidence-only adaptation produces
far too many extra centers. No model approval, package, tag or release.

The [single revision outcome](../ml/markers/center/confidence_head_v32/P1_RESULT.json) records complete
development and workflow measurements. The
[initial diagnosis](GOAL-22-FROZEN-CONFIDENCE-HEAD-DIAGNOSIS.json) preceded
implementation. All 276,618 existing owned training rows were retained.
Only 64 confidence weights and one bias were adapted for eight fixed epochs
and 17,296 optimizer steps. The other 63,387 parameters, every normalization
buffer and geometry output row remained fixed. The final epoch was used without
development-based checkpoint selection.

## Complete development at the current 0.10 operating point

The unchanged shared bars require 95 percent per-item precision and recall.
Historical 0.25 measurements remain descriptive and reproduce the original V27
counts. No threshold was selected or changed.

| Split | Retained matched | V32 matched | Retained extras | V32 extras | V32 precision |
| --- | ---: | ---: | ---: | ---: | ---: |
| component | 1862/2004 | 1935/2004 | 358 | 8787 | 18.05% |
| family | 201/206 | 206/206 | 75 | 1187 | 14.79% |

All 167 component and nine family scenes, 2,210 truths and 270,236 proposals
remain included. Freezing geometry per input patch does not preserve confidence
ordering, NMS choices or the final set of accepted centers.

## Complete application comparisons

The application runtime, V5 classifier, OCR configuration within each arm,
0.10 threshold and all native/CSV inputs were held fixed against checkpoint
64f5749339cf56938deb59f67621b794467c04dc. Each arm includes all 21 native
sources, 31 panels and 838 marker truths.

| OCR arm | Retained matches | V32 matches | Retained extras | V32 extras | Recovered truths | Lost truths |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| v44 | 745 | 792 | 29 | 240 | 50 | 3 |
| v46 | 749 | 796 | 23 | 232 | 50 | 3 |

Each CSV comparison includes all 23 sources, 39 panels, 706 unique truth points
and 724 relational truth rows. Failures stay in the denominator.

| OCR arm | Retained correct values | V32 correct values | Retained exported rows | V32 exported rows | Finite, valid-calibration rows |
| --- | ---: | ---: | ---: | ---: | ---: |
| v44 | 414 | 7 | 426 | 11 | 11 |
| v46 | 429 | 5 | 440 | 8 | 8 |

All OCR, axis, calibration and marker-stage changes, source status changes and
CSV values are recorded in the outcome. Valid calibration status and pixel
traceability do not imply a detected marker is correct. Synthetic row counts
are not a substitute for tests of the runtime safety property or real acceptance.

## Export recovery and verification

The original execution became void after complete development because of an
additional zero-raw-threshold-crossing check. All raw numerical errors already
met the existing absolute tolerance of 1e-5. One probability was
0.1000001058 in Torch and 0.0999999344 in ONNX; both versions reject that proposal
at the existing ink-support check. This numerical sensitivity remains visible.
The [written parity diagnosis](GOAL-22-FROZEN-CONFIDENCE-PARITY-DIAGNOSIS.json)
explains the policy-based correction. The model, recipe, splits, threshold,
numerical tolerance and shared product bars remain unchanged.

Recovery restored the completed final epoch and ran **zero additional optimizer
steps**. Both the checkpoint and ONNX payload are byte-identical to the original
attempt. All 270,236 predictions meet numerical parity, with maximum error
6.19888306e-06. Maximum geometry-output
change against retained ONNX is 3.81469727e-06.
Operators, input/output contracts and every non-confidence initializer match
the retained model exactly. The naive sorted-output comparison in the one-scene
diagnostic remains false and is not claimed as downstream parity.

There are 65 passing focused tests, including unchanged geometry, exact epoch
recovery, prevention of further optimization during completed recovery,
cache tampering, population/label contracts and numerical parity. The original
Boolean hard-negative format and diagnostic path-type failures are preserved
with their corrected attempts. The unchanged application runtime retains its
1,402 passing tests, 16 optional OCR skips and successful CI evidence.

Original execution: 263.663s.
Completed-epoch recovery and development: 272.862s.
Full parity diagnosis: 68.415s.
Four workflow comparisons: 865.078s.
All heavy work used the existing 12-processor, Idle-priority, 80 percent guarded
execution policy. Timing details and failed attempts remain in the JSON outcome.

## Scope and disposition

Project-owned Apache-2.0 code, model and synthetic data only. Existing reviewed
model inputs remain checksum-bound. No new dependency, imported weight,
private/sealed read or production approval. Canonical ledger row 96 closes
without sealed-budget consumption; the preceding 95 rows are unchanged.
Local generated evidence stays in the session worktree and is not bundled.

Goal22 remains incomplete. Build 433 stays 0.4.33. Accuracy gates, sealed
prerequisites, real acceptance, production activation and the final 2.0.0
installer/portable distributions remain outstanding.
