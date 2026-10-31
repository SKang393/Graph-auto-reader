<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Fragmented X-tick detector V2: exploratory training

This train-only experiment used 96 procedural images (64 positive and 32
background). It is not held-out evaluation, production approval, or evidence
of accuracy on real graphs. The trained checkpoints are not distributable
production payloads.

The first run failed after eight optimizer steps because an all-background
batch reduced an empty box-loss tensor. Its started and failed receipts were
retained locally. A corrected 36-step run completed on the AMD Radeon RX 6600
XT with ROCm HIP 7.16.26385, but ordered minibatch loss did not establish a
useful detector. A subsequent full-training-set assessment showed positive
recall at 0.5 falling from 1.0 to 0.0.

The cell-relative revision used balanced positive/background losses and
deterministic shuffling. It completed 36 steps on the same GPU; at threshold
0.5, full-training-set positive-cell recall again fell from 1.0 to 0.0 and
background false-positive cells per image fell from 384 to 0. Mean positive
objectness after training was 0.4542. The result rejects this candidate for
integration and does not measure generalization.

- Source manifest SHA-256: `f3035a6401cd35369decde67c517b07ceb8948e953b558ee01317f79224b0f1e`
- Executed runner SHA-256: `2fcd1afcaf06b9d46bd0615307c53d235bff1026fe676623db284a7e1df95780`
- Cell-relative report SHA-256: `2b00fdf1a7c2dc02d8a613dd1e2ffb02d89556fc4cca5969143223acbd3e5077`
- Cell-relative checkpoint SHA-256: `d3c84b1bc1ef78eb6d967841b3396cd121ad535638805f319e2a72428bbcb310`
- Materializer and trainer tests: 12 passed.

No development, sealed, or private image payloads were used in these runs.
Further work must improve localization and test against genuinely independent
data before any production decision.

## Stratified batching follow-up

V3 retained the V2 model, loss, 36 steps, and images, but guaranteed a mix
of positive and background images in every batch. Each epoch visited all 96
images once in 12 batches. Positive-cell recall at 0.5 still fell from 1.0 to
0.0 and background false positives from 384 to 0 per image. The training-only
result does not support promotion, and rules out an all-background minibatch
as a sufficient explanation for this particular collapse.

- Runner SHA-256: `1f52140c542308e5cd07e5ed3abae2048d9f38230ca866b4fcc02f8059efaf83`
- Report SHA-256: `6f26f5b69166b84a1b60a1c7192afe79f76680f7137229ed66da3d93a9f9df10`
- Checkpoint SHA-256: `ed205368ab644dbd1f3d5514437551f6febb54f7114d0208f6bbe99615693c5a`
- Focused tests: 11 passed; all optimizer steps ran on the RX 6600 XT.
