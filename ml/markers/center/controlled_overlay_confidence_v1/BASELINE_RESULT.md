<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Controlled-overlay confidence baseline

On the 214 verified build patches at the existing 0.1 threshold, the retained
V27 model selected 75 of 108 injected markers and 13 of 106 distractors. V37
selected 87 markers and 22 distractors. Relative to V27, V37 gained 17 marker
decisions and lost five, while gaining 18 distractor decisions and removing
nine. These are patch decisions after proposal emission, not whole-image
precision or recall. The 52 images without emitted targets remain outside this
patch-level denominator.

This is an exposed, controlled training sample with repeated shapes across 11
contributing groups. The internal holdout arrays were not opened for this
baseline. A later experiment must evaluate held-out and broader development
data without treating this in-sample comparison as production acceptance.

The run used AMD Radeon RX 6600 XT `gfx1032` on `cuda:0` through HIP
`7.16.26385`; forward inference took 4.048 seconds. All model parameters and
input/output tensors were checked on the GPU. No optimizer, CPU fallback,
development or sealed payload, or model approval was used. The retained V37
backbone was checked against the authenticated V27 checkpoint. ROCm emitted
its existing missing grouped-convolution and tuning-database warnings, but the
GPU probe and this inference completed successfully.

| Evidence | SHA-256 |
| --- | --- |
| Protocol | `829d21cbd893daf80965c8e7c037aed024f19c62acde95acc61b1ed596708f37` |
| Pre-run registration | `5b058f521bc369a16d0710233650c2183e8ecab5349e62171fda576cb01f204d` |
| Dataset receipt | `d1390de8797a75bc0c821996a327938b1dcd729de4f7b1f67d130e3c5fe29de6` |
| Aggregate baseline | `ee12538d645a072e75885fa72ae72bc28fc572e715d87acb57defb0d1fc79701` |

Two focused summary tests and pyflakes passed. Per-image results and the
controlled tensors remain project-local and are not release artifacts.
The later [overlap audit](OVERLAP_RESULT.md) identifies repeated input pixels
across the source-grouped partitions. This baseline remains an in-sample
measurement over the original 214 build patches.
