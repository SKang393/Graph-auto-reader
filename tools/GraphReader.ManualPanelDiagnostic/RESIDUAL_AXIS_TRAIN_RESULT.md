<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Residual ranker against manual-training anchors

The frozen synthetic-trained candidate was compared with the native horizontal
axis on seven previously exposed training panels. Predictions were written for
all 21 captured panels from 20 original images before the local calibration
bindings were opened. The seven reference panels matched the captured source
and panel hashes. One of eight available anchor examples remained excluded by
the previously recorded skew constraint.

The candidate selected a different axis on three scored panels. The resulting
distance to the two derived zero-line anchors improved on those three panels
and remained equal on four. Mean normalized distance changed from `0.05091`
to `0.05037`; the median changed from `0.03674` to `0.03653`. Six of the seven
native panels were already marked as needing review. CPU and RX 6600 XT forward
inference selected the same candidates across all 21 panels, with maximum
absolute score difference `2.3842e-7`.

This small, previously exposed sample does not establish a generalizable
improvement. The stored digitization points may be imperfect; their derived
zero-line anchors may be interpolated or extrapolated and have not been checked
against the printed graphs. The metric uses infinite lines and says nothing
about axis endpoints, the vertical axis, OCR, graph values, or end-to-end
recognition. No runtime behavior, review threshold, acceptance status, or model
approval changed.

| Evidence | SHA-256 |
| --- | --- |
| Protocol / probe | `a60755d0bb88493b2486c9246802e3d1cec61af0679e688f955208f31aad2087` / `a6d9065e04f08f9c1da2d94932d619b669b611e9922ae8d36c5586ae4459179b` |
| Original candidate capture | `71177f3d6c14de7525d1eec0d03596fb35109248ef9b879fc0a81a94ed4a651b` |
| Existing private anchor bindings | `e092e6c352b3bd25ccf59debd72c6df60a5bfe2daf0b6a9b2384f0a8ad19cb9c` |
| Frozen predictions / aggregate score | `956f0ed275f8ecf9f90269d88233444cd305406dcc0a9242de5d3015e16f7831` / `2468260364ac0be253de337e9990b4ad1e6bcbd56810db0c6e12938b897f5077` |

Three geometry tests and pyflakes passed. The probe used no optimizer, real
development payload, or sealed payload. Its private predictions remain under
the project-local diagnostic directory for the active axis investigation and
must not be committed or packaged.
