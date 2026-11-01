<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Controlled-overlay proposal domain and patch parity

All 200 fixed train-only overlays completed through the unapproved candidate
raster, axis, OCR, artifact-mask, and marker-center path. The capture retained
the native plot polygon, emitted proposal centers, and three model-input planes.
No overlay labels were read during capture. The controlled shapes repeat across
20 source groups and do not represent independently labeled natural markers.

| Internal partition and controlled shape | Images | Nearby ink-supported positions | Positions emitted by runtime |
| --- | ---: | ---: | ---: |
| Build, marker | 75 | 145 | 108 |
| Build, distractor | 75 | 145 | 106 |
| Holdout, marker | 25 | 70 | 60 |
| Holdout, distractor | 25 | 70 | 60 |
| Total | 200 | 430 | 334 |

The remaining 96 ink-supported positions were not emitted and cannot be treated
as model inputs for this runtime. Fifty-two images had no emitted proposal near
their controlled shape; all remain in the denominator. The build and internal
holdout contain 15 and five study-plus-image groups, respectively. These are
train-derived partitions, not independent production acceptance data.

For the 148 images with emitted controlled positions, native C# re-enumeration
matched the complete frozen proposal sequence for each selected panel. All 334
selected Python patch hashes matched the native sampler input bytes. The sampler
runner returned fixed zero outputs and performed no model inference; the
earlier capture used CPU inference. No optimizer steps or sealed payload reads
occurred. Unmarked image content was never assigned a negative label.

Rebuilding the diagnostic after a Git revision change produced different
application assembly hashes. The parity run therefore used the exact 12
captured GraphReader assemblies recovered from the application test output and
rehash-verified in a new project-local runtime. The new diagnostic tool and
captured application assemblies are separately hash-bound; this is not a claim
that their source-to-binary relationship was independently rebuilt.

| Evidence | SHA-256 |
| --- | --- |
| Domain protocol / capture registration | `2efa57ed91631bd74dae98a7343c783860252391b5832e8963de73112773c009` / `d8625959106602d2938b3bfdec4e43163269c4205bd212ff450a772a2060dc00` |
| Analysis registration, amended before capture completed | `a9ebdafeb2a95c4930d3451d060d09f6e67b0cfb2d51845c5486261ac0087e9a` |
| Capture runtime before and after | `3819dccb77614ac69681a203f5ebe359b3eeba9879645624c31ac216dd5606b0` |
| Full capture / aggregate domain score | `52feaf7b9f5b6625d80446ecd847366e3885bd29d3eed87d8abc74929be62383` / `28dd02d5f5b34ffa509e5e6e40ba78c70c38187abb129882eb5fb090be424213` |
| Parity protocol / preparation registration | `11e5c95ecdca5135bfcf4765fa7a38245cf10c2ad3ea8471a7b76edb7746de9b` / `55122d0b06de467dcc451e62669c3dc51dcd2243cd0cb36ccba58a62117a0d2c` |
| Parity input / registration | `4015f68ad77f6be5268b85670f8758cb814e988fea7afdd1d81ae50bfb644437` / `d787f0481ed8c0d7fc24abdb5781e5e0db644a653fad0cb1ff0c8504fd31939f` |
| Parity runtime before and after / result | `cc38e51dcca7e2f34790f5d1977c19b0a9cf8a82959c385a1dcc84d6928bebbd` / `cf8ef8312d793ed48a7c802ccbb75d6208da202e59e782bb645bb47f707f01df` |

The Release diagnostic build had no warnings, its native sampler self-test
passed with the restored assemblies, and five Python domain/parity preparation
tests and pyflakes passed. This establishes input and proposal parity only.
The small controlled dataset, missing proposals, imperfect manual data, and
unverified natural-image false positives still require a separate bounded
GPU-only experiment and evaluation. No model or release gate was approved.

## Controlled dataset materialization

The verified positions were materialized into project-local arrays: 214 build
patches (108 injected markers and 106 injected distractors) from 11 groups, and
120 holdout patches (60 of each) from four groups. The original partitions
contained 15 and five groups; groups with no emitted proposal supply no patch.
The 52 images without an emitted target remain recorded as unavailable for
this patch-level dataset. No unmarked native pixels were assigned negative
labels. The selected groups are disjoint under the existing study-plus-image
grouping, which does not establish independence from historical or sealed data.

The [materialization protocol](CONTROLLED_DATASET_PROTOCOL.md) has SHA-256
`4840a41cc3a482b1a69d70e85f5ca82e79e38c15c5b40afb75972a3d716b70db`;
the pre-run registration has SHA-256
`c6aa90f64142bfb9f3845a914d7980a9cc3b33a2ee136f550217a0f9fbc10701`;
and the private dataset report has SHA-256
`d1390de8797a75bc0c821996a327938b1dcd729de4f7b1f67d130e3c5fe29de6`.
An independent readback verified every file hash, all 334 tensor hashes,
partition counts, and disjoint selected groups. Eight focused tests and
pyflakes passed. This step ran no optimizer and did not approve a model.
The subsequent [GPU confidence baseline](../controlled_overlay_confidence_v1/BASELINE_RESULT.md)
compares the existing candidates on build patches without opening the holdout.
