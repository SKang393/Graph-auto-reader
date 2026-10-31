<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Camera-document synthetic development set

The isolated `camera_document_dev` profile provides one exploratory nonsealed
development family. It leaves the shared generator and reserved hand-drawn
family unchanged. Its packaged Noto Sans font is bound to an exact upstream
commit, font digest, and accompanying OFL license; the license assessment for
rendered outputs remains unassessed. The profile does not claim an independent
internal train/dev split or historical-benchmark visual independence.

The fixed four-scene dataset has four distinct images and 169 unique annotated
text IDs, including 54 X-tick and 51 Y-tick labels. Labels bind to their source
scene, tick identities, transforms, and pre-degradation glyph boxes; those
boxes are not pixel-perfect post-JPEG segmentation. Two same-host generations
produced identical artifact bytes. Three focused wrapper tests passed, and the
dataset verifier succeeded against the committed profile.

This data is diagnostic development material, not an approved training set or
sealed acceptance evidence. Cross-host bitwise reproducibility remains
unverified.

- Profile: `ml/synthetic/camera_document_profile_v1`
- Dataset manifest: `artifacts/camera-document-dev-dataset-v2-20261030/manifest.json`
- Manifest SHA-256: `187c3810440474f400f6148834f90438230ab0cb7d51cff96624dae5087bf468`
