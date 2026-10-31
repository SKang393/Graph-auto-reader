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

The preflight used OpenCV 4.10 with a 512/128 diagnostic resize recipe. Its
caches are not approved training inputs. No optimizer steps or private/sealed
reads occurred. Report SHA-256:
`e3cf0880968123fb61164c068607298b90e119388363de6c60320b672d5927f3`.

Subsequent isolated comparisons passed the same four synthetic train images
through the production C# raster decoder and detector tensor methods. At both
the experimental 512/128 setting and the production 960/128 setting, the
Python and C# tensors matched byte-for-byte. The comparison was bound to a
locally pinned capture/source/app-local assembly identity before capture; eleven focused
checks passed. The 960 tensors were compared separately and do not change the
existing 512 feature caches. This validates these four preprocessing examples,
not target correctness, the complete workflow, or training approval. The 512
comparison separately checks decoded BGR bytes; the 960 comparison verifies
tensor bytes but does not itself assert BGR equality. The local identity excludes
the shared .NET framework, operating system, and hardware.

- Local comparison identity SHA-256: `81c3ea76acec8ce59f749387e603e98de99bbaad8c0326b1f105ef42af0c6bd0`
- Experimental 512 comparison SHA-256: `a586ab30b2a9b6050582c0a641dfb876246640b8316ae0c8af169cea23543eef`
- Production-size 960 comparison SHA-256: `97d5712832b6ca3e07c9d4ad086dc4525cec37873e6daec61ff59eb4ab59e982`

A train-only target audit found four connected target components that each
merge two distinct annotated text instances. All four contacts remain in a
no-minimum-extent counterfactual. The audit also found 12 width clamps, two
whose clamped widths exceeded the mapped source-text box. No model effect was
measured.

An unapproved loss-mask counterfactual omitted ambiguous components and
outgrown clamped targets, with a two-pixel margin in 512-aligned tensor
coordinates and transitive overlap closure. It retained 96 of 110 text
instances and 45 of 47 X-ticks, but only one of four participant labels. This
potential bias prevents treating it as a training policy without more evidence.

- Target audit SHA-256: `2ec1e0813300845114a385b09d4ac26d6a7f30d54e0dc3d7317a4ee46ff0f8a0`
- Loss-mask counterfactual SHA-256: `d82ff850eb6d0f426b827e7d92cb9f35c0a38c9f52175353bcdcc8c3a779ccdf`

A separate unapproved counterfactual masked only shared target pixels and a
two-pixel tensor-space margin. It retained 16,444 of 16,750 unique supervised
positive pixels, all 47 X-tick instances, 22 of 23 Y-tick instances, and all
four participant instances with some positive support. On these four images,
none of the original contact pairs remained adjacent in the retained mask.
Touch-only cases and two outgrown-clamp instances remain outside the policy;
this result does not establish model-output separation or training suitability.

- Conflict-pixel counterfactual SHA-256: `ce95ddda8e00e0045b62579b8e73fb6888d09d5126317674273b46d3d89b6961`
