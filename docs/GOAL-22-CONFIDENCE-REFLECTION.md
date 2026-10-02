<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Fixed reflection confidence V37 outcome

**FAIL. Keep V44/English/V27/V5.** Fixed training reflections improve component
recall and reduce extras, but precision remains below the unchanged 95 percent
bar. This trial does not authorize production use or a sealed run.

The [diagnosis](GOAL-22-CONFIDENCE-REFLECTION-DIAGNOSIS.json) records the measured
generalization defect before feature preparation and candidate training.
The [revision outcome](../ml/markers/center/confidence_reflection_v37/P1_RESULT.json)
binds the complete development and application evidence.

## Isolated change

Relative to V35, retain all 276,618 original training rows byte-for-byte and add
horizontal, vertical and combined reflections of every patch. All three input
planes reflect together about the unchanged center pixel. Scalar anchor labels
and hard-negative flags remain unchanged. Twenty groups contain 1,106,472 rows
and 98,704 positive examples. Reflected text is augmentation, not a claim about
real typography.

The same V27 initializer, 49,281-parameter confidence projection, binary
cross-entropy, optimizer and fixed eight-epoch recipe produce 69,160 steps.
All 63,452 original parameters and normalization buffers remain unchanged.
There is no intermediate development selection, early stopping or threshold
change. Per-patch geometry stays frozen; confidence ordering can still change
suppression and the selected centers.

## Complete development

All 167 component and nine family scenes, 2,210 truths and 270,236 proposals
remain included. The operating cutoff is 0.10. The shared precision/recall bars
remain 95 percent and prohibited-structure maximum remains 2 percent. Threshold
sensitivity is descriptive.

| Split | Matched | Extras | Precision | Recall |
| --- | ---: | ---: | ---: | ---: |
| component | 1943/2004 | 166 | 92.13% | 96.96% |
| family | 202/206 | 58 | 77.69% | 98.06% |

Every development false positive and missed truth is retained in the local
geometry and error-attribution reports referenced by the outcome. Individual
nearby proposals are opportunities, not jointly attainable recall: suppression
and matching couple candidates.

## Complete application comparison

Both OCR arms retain all 21 native sources, 31 panels and 838 marker truths.
CSV evaluation retains all 23 sources, 39 panels, 706 unique points and 724
relational rows, including failed sources in the denominator. Runtime source
and compiled binaries remain fixed at the tested application checkpoint
64f5749339cf56938deb59f67621b794467c04dc. V5 classification and each arm's OCR,
source images, mask algorithms and operating thresholds retain their bindings.

| OCR arm | Matched markers | Extras | Recovered truths | Lost truths | Correct CSV values | Completed exports |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| v44 | 764/838 | 21 | 29 | 10 | 389/706 | 14/23 |
| v46 | 767/838 | 17 | 28 | 10 | 404/706 | 15/23 |

Recovered/lost counts compare exact truth identities against retained V27.
The authenticated complete audit records all changes to OCR, centers, axes,
calibration and CSV. Candidate additions/removals can include numerical movement;
they are not interchangeable with recovered/lost truths. Invalid calibration
continues to block export. These synthetic results do not establish real
acceptance or the runtime safety property by themselves.

The [saved-output export diagnosis](GOAL-22-CONFIDENCE-REFLECTION-EXPORT-DIAGNOSIS.json)
attributes 24 of each arm's 25 lost correct values to one newly blocked graph.
A false marker on an annotation leader has no supported session assignment;
export correctly stops instead of inventing an x value. The remaining net one
lost value is outside that graph and is not attributed by this diagnostic.
The 1200 by 350 owned source image was inspected. No guard or tolerance changed.

## Verification and disposition

All 186 focused tests pass in 15.29s. Every
original and reflected feature row is authenticated. Full reflected-feature
reconstruction error is zero. CPU ONNX parity over all 270,236 proposals has
maximum absolute error 6.19888306e-06, with
0 raw 0.10 boundary crossings.
Maximum geometry change against retained ONNX is
3.81469727e-06. Three existing exporter warnings
concern the legacy API and static input-shape checks; dynamic batches one and
five were tested. The unchanged application retains its earlier 1,402 passing
tests and 16 optional OCR skips; this trial does not claim another test run.

Preflight, including feature preparation: 410.632s.
Training and complete development: 406.597s.
Four application comparisons: 636.596s. Heavy
work uses 12 eligible processors, Idle priority and the shared 80 percent guard.

Owned Apache-2.0 code, model and synthetic inputs only. No new dependency or
imported weight. Canonical ledger row 101 closes without consuming sealed budget;
the previous 100 rows remain unchanged. No private/sealed inputs, production
activation, packaged build, tag or release. Do not repeat this unchanged trial.

Goal22 remains incomplete. Accuracy, sealed prerequisites, real acceptance,
production activation and the final 2.0.0 installer/portable distributions remain.
Build 433 stays 0.4.33.
