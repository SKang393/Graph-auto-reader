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

A separate check reconstructed the hash-bound historical V46 head and compared
its Radeon output against CPU ONNX Runtime on one authenticated 32-by-32
synthetic training crop. Maximum absolute difference was `6.7221e-08` against
the existing `1e-4` tolerance, with no decision changes at 0.3. Five focused
checks passed, including altered image, model, protocol, and split bindings.
The reported probe-script hash is descriptive, not a preregistered guard. This
check does not establish full GPU-trunk parity, full-size memory requirements,
training viability, or model accuracy.

A separate CPU-inference preflight extracted candidate-only trunk features and
targets for four authenticated synthetic training scenes. It included 110
visible text boxes and ignored 36 marker-artifact candidates without treating
them as OCR negatives. Its inverse-target construction recorded four contacts
and 12 minimum-extent clamps. Six focused checks passed, including held-out
raster/annotation read guards and source/output containment failures.

The preflight used OpenCV 4.10 with a 512/128 diagnostic resize recipe; its
tensors have not been compared with the production OpenCvSharp 4.13 path. The
caches are not approved training inputs. No optimizer steps or private/sealed
reads occurred. Report SHA-256:
`e3cf0880968123fb61164c068607298b90e119388363de6c60320b672d5927f3`.
