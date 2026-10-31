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

## Duration follow-up

The same V3 recipe, extended to 30 epochs, completed 360 RX 6600 XT steps.
Training-set positive-cell recall at 0.5 recovered to 0.984, while background
false-positive cells averaged 20.06 per image and box target MAE was 0.141.
These are cell-level training diagnostics, not image-level detection accuracy.
The run indicates that three epochs were insufficient but does not establish
a usable detector or justify a production decision.

- Runner SHA-256: `620b27122a257c9791917bac0c3423e09b3ca4d36b9b1a652a32c371a63c6f62`
- Report SHA-256: `835e8a4d7845e7654e1888075fa66d6579af1d54996523ea1999698c6fd01f9f`
- Checkpoint SHA-256: `ec2276cc85dacf89a064bfbbdf1aed5ca98b8bab40c429c4d2d84ccc1ca4dd15`
- Focused tests: 11 passed; no CPU optimizer execution.

An image-level check of that same checkpoint revealed that cell recall was a
poor proxy for localization. On the same 64 positive training images, the
highest-scoring predicted box had mean IoU 0.356 and reached IoU 0.5 on
21.875% of images. With score threshold 0.5 and both NMS and match IoU 0.5,
image recall was 0.281 and the 32 background-only training images averaged
20.06 false detections each. These metrics do not use independent data.

- Evaluation script SHA-256: `916a68eebcf22fa169886428b5abc7952b5324d9e43b2d909e12116666a51112`
- Corrected evaluation report SHA-256: `d99ef9b76fb4be445c903d867715b3770b334dc04c61b720e5bf8ac233d5dc16`
- Evaluation tests: 4 passed; inference used the RX 6600 XT.

## Wider-context follow-up

The P3 network added a stride-one convolution to increase receptive field from
15 to 31 pixels, while retaining the train images and 30-epoch schedule. It
completed 360 steps on the RX 6600 XT. Mean box IoU at the target-assigned
cell was approximately `1.51e-15`, and no positive training image achieved
IoU 0.5 after detection. The output boxes therefore failed even at the assigned
cell. Encoding/decoding round-trip checks passed. Adding the layer also changed
the randomly initialized heads, so the comparison does not isolate receptive
field alone. This is not independent validation or a production candidate.

- P3 report SHA-256: `ca30bebdff8aa98040e1b58d784e0dba500ab68543fbe6c6dcc54a2f93970fe6`
- P3 checkpoint SHA-256: `359c42174f2b946c29a7768b9105f065fceb20bf716014eac0bbe121fd4255f6`
- Image evaluation SHA-256: `bdceab1751f561f1d35bd1a07fa89fe132e44c980bf56101b01a32e52aced56e`
- Training and evaluation tests: 16 passed.
