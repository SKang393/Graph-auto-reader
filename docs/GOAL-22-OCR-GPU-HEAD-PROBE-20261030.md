<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Synthetic OCR head GPU probe

The project-local probe in `ml/ocr/gpu_db_head_probe_v1` ran the existing
`FrozenDbHead` topology with constant synthetic inputs and parameters on the
AMD Radeon RX 6600 XT (`gfx1032`, HIP `cuda:0`). Forward and backward produced
finite GPU outputs and gradients; parameters remained unchanged and no
optimizer step ran. Five focused checks passed, including unsupported-device
and CPU-tensor rejection. Peak additional PyTorch allocated memory was
67,593,728 bytes for a `1 x 96 x 4 x 4` input.

This is a small backend compatibility check. It does not authenticate real
checkpoint weights, establish trunk integration or ONNX parity, predict
full-size memory use, or demonstrate training or recognition accuracy. No
private or sealed inputs were read. Existing MIOpen tuning-file warnings were
non-fatal in this probe.
