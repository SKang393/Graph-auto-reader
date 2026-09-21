<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Reject graph structures before export

The classifier now rejects the false points that blocked seven synthetic graph
exports. A connecting line crossing a phase divider had been classified as a
marker. The existing export guard correctly refused its unknown session value.
The repair improves classification; calibration and export safeguards remain.

| Same synthetic workflow | Earlier classifier | Structural refinement |
| --- | ---: | ---: |
| Images producing an export | 10 / 23 | 17 / 23 |
| Correct unique values | 191 / 706 | 479 / 706 |
| Correct relational CSV rows | 185 / 724 | 474 / 724 |
| Development markers found | 200 / 206 | 201 / 206 |
| Extra development markers | 1 | 0 |
| Remaining calibration failures | 6 | 6 |

All 39 panels remain observed. Axis and OCR outputs are identical. The final
development marker precision is 100% and recall is 97.57%. Training graphs have
487/500 matched centers and nine extras. CSV matching additionally depends on
series, phases and calibration: sixteen unique extra values, 227 missing values
and three wrong phase rows remain. Failed images remain in every denominator.
These are synthetic diagnostics, not sealed or real acceptance.

## Implementation and verification

The [diagnosis](GOAL-22-CLASSIFIER-STRUCTURAL-DIAGNOSIS.json) identified missing
native structural backgrounds in the classifier's previous whole-graph training
extension. The new cache adds 38,694 crops from authored axes, ticks, dividers,
connecting lines, oblique intersections, arrows, brackets, bars and legend
frames. It protects real data markers and legend glyphs from negative labels.
Only the twenty owned training images supply new crops; model predictions do
not select them. All 23,818 earlier training rows and tensors and all 3,136
development rows and tensors are retained unchanged.

One fixed 12-epoch refinement from V4 takes 466.18 seconds and 5,868 optimizer
steps. The existing architecture, losses, native bilinear crop, probability
temperatures and 0.5 classifier cutoff remain. The final epoch is selected
without comparing development checkpoints. On the unchanged classifier dev
set, shape accuracy is 2,812/2,880 (97.64%), fill accuracy is 2,864/2,880 (99.44%),
and one of 256 non-markers is accepted. These clear the unchanged shared bars.
ONNX parity covers all 65,648 train/dev tensors, with maximum absolute error
7.868e-6 and zero shape, fill or rejection decision changes.

All 67 targeted coverage and admission tests pass. Their only warning is an
unwritable optional pytest cache. Full-workflow inference takes 143.81 seconds.
Its nonzero process exit records the six retained calibration failures.

The CPU guard leaves all twelve logical processors eligible, uses Idle priority
and passive worker waits, and records a maximum completed work/rest duty of
79.9934%. That is a work/rest average, not an instantaneous per-core guarantee.

## Evidence and remaining work

The [outcome](../ml/markers/classifier/structural_context_v5/P1_RESULT.json) and
[evidence index](GOAL-22-CLASSIFIER-STRUCTURAL-REPAIR.json) bind the source
snapshot, fixed recipe, training cache, model hashes, checks and full workflow.
The canonical ledger closes this synthetic-only candidate without consuming a
sealed run. Earlier revision records remain unchanged.
One file-specific Git attribute preserves the historical V3 protocol's tested
line endings when checking out the source snapshot; its JSON content is unchanged.

The model and examples reuse Apache-2.0 project-owned sources. No dependency,
private image, sealed case, font binary, production approval or release is
added. The evaluated proposal cutoff stays at the earlier diagnostic 0.10;
the normal application default remains 0.25. The retained package is still
build 433 / 0.4.33.

Next, measure the complete proposal/classifier combination on the existing
component evidence, finish current stage-evidence adapters and investigate the
remaining calibration failures. Raw component-model results remain recorded
and are not replaced by these final-marker measurements. All four Goal 22
outcomes remain incomplete.
