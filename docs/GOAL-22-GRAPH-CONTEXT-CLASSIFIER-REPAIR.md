<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Graph-context classifier repair

The classifier clears every unchanged development bar. The complete graph
workflow still fails. Hold these weights fixed while diagnosing geometry,
calibration, grouping and export; this result is not production approval.

## Classifier result

| Unchanged development examples | Native-context V3 | Graph-context V4 |
| --- | ---: | ---: |
| Correct shape | 2,453 / 2,880 | 2,813 / 2,880 (97.7%) |
| Correct fill | 2,773 / 2,880 | 2,854 / 2,880 (99.1%) |
| Markers retained | 2,878 / 2,880 | 2,880 / 2,880 |
| Non-markers accepted | 1 / 256 | 0 / 256 |

The [saved-crop diagnosis](GOAL-22-CLASSIFIER-GRAPH-CONTEXT-DIAGNOSIS.md)
identified missing coverage for line contact and printing effects on owned
synthetic training images. The cache retains all 7,840 prior training examples
and adds 12,416 procedural examples and 3,562 complete-graph crops. The latter
contain all 500 authored training points at three crop sizes and 2,062 text
components. Text crops overlapping authored markers are omitted as invalid
negative labels, independently of model predictions. No evaluation source or
failure is excluded. All 3,136 development examples remain byte-identical.

Two preparation failures remain recorded. Five procedural markers disappeared
under halftone sampling. A generic stroke-width constraint, applied to both
positive and negative recipes, prevents that invalid input. No optimizer ran
on an invalid cache. The final cache has zero invisible positive patches,
cross-split identical tensors or conflicting training targets. The prior 28
ambiguous authored rows remain with their recorded observable targets.

All 64 final Python checks pass in 4.85 seconds. The fixed final checkpoint
follows 36 epochs and 6,732 optimizer steps in 597.61 seconds. Architecture,
runtime crop, temperatures and artifact threshold remain unchanged. ONNX
parity covers all 26,954 train/development tensors: maximum absolute difference
is 0.0000067354, with no shape, fill or artifact decision changes.

All twelve logical processors remain eligible. Synchronous work/rest duty
peaks at 79.984% per completed cycle, alongside the 80% Windows job backstop.
This is a cycle-average limit, not an instantaneous per-core measurement.
Training fill accuracy is 94.1%, a remaining limitation despite the development
pass.

## Complete workflow result

| Same 23 synthetic sources | Native-context V3 | Graph-context V4 |
| --- | ---: | ---: |
| Sources that export | 11 / 23 | 9 / 23 |
| Correct unique values | 152 / 706 | 140 / 706 |
| Correct relational rows | 148 / 724 | 132 / 724 |
| Extra unique points | 17 | 4 |

The run takes 138.94 seconds. All 30 prior panels remain available, with 32
reached in total, and common axis/OCR outputs remain unchanged. Every failed
source remains in the denominator. There are 566 missing truth points and five
wrong phase rows. Fewer extra points do not offset the regression. Calibration,
first-session and export guards must remain in force.

Next, diagnose deterministic integration using these fixed weights. A poor
whole-source result alone does not justify another training revision. Sealed
and real confirmation, followed by mechanical promotion, still precede
production activation.

## Evidence and scope

The [aggregate record](GOAL-22-GRAPH-CONTEXT-CLASSIFIER-REPAIR.json) binds the
cache, preparation failures, tests, full workflow and hashes. The
[revision outcome](../ml/markers/classifier/graph_context_v4/P1_RESULT.json)
closes P1 as `dev_pass_workflow_incomplete_unconsumed`; no sealed budget is
consumed. Preregistration, authorization, implementation and outcome form one
outcome commit.

Source is Apache-2.0. No dependency or font binary is added. Generated images,
tensors, weights and runtime output remain ignored. No private/sealed data is
read; no production approval, activation or package is produced. Retained
build 433 remains 0.4.33. All four Goal 22 outcomes remain incomplete.
