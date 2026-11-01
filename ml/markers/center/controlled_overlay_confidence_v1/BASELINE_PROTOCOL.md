<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Controlled-overlay confidence baseline

On the 214 native-parity controlled build patches, compare the authenticated
V27 base and V37 confidence model at the existing 0.1 candidate threshold.
Both use the same verified three-plane tensors. Report aggregate true-positive
and false-positive counts and the number of changed decisions. Do not read the
internal holdout arrays while forming this baseline or choose a threshold.

Use the project-local RX 6600 XT ROCm launcher with device and tensor
residency checks. This is forward inference only: no optimizer, model update,
development or sealed payload, or production approval is permitted. Keep all
patches and per-image data private. The controlled shapes repeat across just
11 contributing build groups, so these measurements are exploratory and do not
establish natural-image accuracy or full-workflow acceptance.
