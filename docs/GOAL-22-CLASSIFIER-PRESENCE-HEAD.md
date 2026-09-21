<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Presence-only classifier repair: rejected

The small V6 repair does not meet Goal 22. It changes only 97 parameters in the
marker-rejection head. Shape, fill, embedding and encoder tensors are verified
byte-identical between the saved V5 and V6 checkpoints. Nevertheless, the
combined component tests still fail, and one previously exporting graph becomes
blocked by an extra false point. V5 remains the current native diagnostic
baseline. V6 is not activated or promoted.

| Same inputs and detector within each comparison | V5 | V6 |
| --- | ---: | ---: |
| V28 component centers recovered | 1,450 / 2,004 | 1,734 / 2,004 |
| V28 extra component centers | 53 | 116 |
| V28 family centers recovered | 199 / 206 | 203 / 206 |
| V28 extra family centers | 27 | 41 |
| Native V27 workflow exports | 17 / 23 | 16 / 23 |
| Correct unique exported values | 479 / 706 | 423 / 706 |
| Correct relational rows | 474 / 724 | 418 / 724 |
| Native development extra centers | 0 | 1 |

The V6 component precision/recall are 93.73%/86.53%, and family
precision/recall are 83.20%/98.54%. All cases remain in the denominators.
Native development recall remains 201/206; all 39 panels are observed and
axis/OCR outputs are unchanged. Export correctly blocks the added unvalidated
point instead of inventing its x value. No wrong scale, duplicate or wrong
export-mode row is introduced; three existing phase errors remain.

## What was tested

The new train-only cache adds 12,024 positive crops from every one of the 2,004
owned training component centers, plus 3,313 protected negative crops. Arbitrary
composite glyphs receive presence labels only. It retains all 62,512 previous
training rows and all 3,136 previous development rows, with no exact train/dev
pixel overlap or contradictory binary target. No shape or fill label is guessed.

A single class-balanced, full-batch binary fit uses the registered maximum of
64 LBFGS iterations on frozen features. It takes 39.36 seconds. Thirteen
coverage/admission tests pass. All 80,985 train/dev/supplement tensors pass the
existing 1e-5 ONNX parity check, with maximum error 7.868e-6 and no decision
changes. Previous patch-level dev shape and fill accuracy remain 97.64% and
99.44%; the patch rejection bar passes. These local results do not override
the failed combined stage or complete workflow.

The combined replay takes 4.44 seconds without rerunning a center detector.
The native workflow takes 153.08 seconds. CPU execution keeps all twelve
logical processors eligible, with Idle priority and passive waits; training's
maximum completed work/rest duty is 79.9917%, not an instantaneous per-core
claim. The first earlier fixed-detector check was void before inference because
its launcher omitted passive waits; that launcher was repaired without changing
models, data or thresholds.

## Next work

The unchanged features cannot adequately separate all newly covered glyphs
from structures using this small head alone. Do not rerun or expand the closed
V6 recipe. A future feature adaptation must retain known shape/fill supervision,
use only owned train data, and recheck all complete-stage and native outcomes.
Meanwhile, finish raw OCR observation and current evidence adapters, then the
remaining OCR/calibration defects. None of that requires maintainer input.

The [outcome](../ml/markers/classifier/presence_head_v6/P1_RESULT.json) and
[evidence index](GOAL-22-CLASSIFIER-PRESENCE-HEAD.json) retain recipe, admission,
source snapshot, tests, numerical checks and all failures in one outcome.
All 87 previous ledger entries remain unchanged. No private or sealed input,
new dependency, model activation, package or release is involved. Project-owned
code and training examples retain Apache-2.0 provenance. Build 433 / 0.4.33
remains the only retained portable. All four Goal 22 outcomes remain incomplete.
