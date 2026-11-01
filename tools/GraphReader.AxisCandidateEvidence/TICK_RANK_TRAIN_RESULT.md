<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Tick-based axis ranking on synthetic training panels

The existing exploratory tick-rank rule was developed after inspecting manual
images. It was replayed on 96 frozen, owned synthetic training panels before
their synthetic targets were opened. Ninety-three panels have matched plot
targets; three fallback or unlabelled panels remain in the denominator but
cannot be scored. Python OpenCV 4.10.0 provided the diagnostic line segments.
Its line output and grayscale decoder were not verified against the reviewed
production provider, so this is a feasibility comparison rather than runtime
parity or model acceptance.

The original rule changed five selections, four of them on scored panels. All
four scored changes worsened mean normalized four-corner error; 89 scored
panels kept the same error. Mean error across the 93 scored panels increased
from 0.01165690 to 0.02112957. In the synthetic image comparison, the four
selected alternatives placed the Y axis at interior phase lines. One native
selection was already substantially displaced.

A follow-up guard rejects a candidate whose own observed X-axis line extends
more than six pixels left of its proposed origin. Six pixels is twice the
current geometry merge distance; the guard and comparison were chosen after
the preceding training results and visual inspection. It changed no selection
on any of the 96 panels, so the 93 scored results exactly matched the baseline.
This shows neither a benefit nor safety on other graphs. Neither rule is
enabled in the application, and no development or sealed evaluation followed.

| Evidence | SHA-256 |
| --- | --- |
| Source extraction receipt | `588f1375544477505864a228922452266c396b0008e2d66efe20beb56f95bd58` |
| Synthetic training groups | `d9c5928ffea0407fd5b1041d3c3ca9146f128d71054cca0026a51e88a3dc67b5` |
| Original probe predictions / score | `76cfeba9668f86641382fc138b5896d24bf41a8a323b51accc25f2623de4b332` / `83afb3a2638fd5a4b979f9aba81a6a159b444877af9b82852298dd5d0e29ea12` |
| Guarded predictions / score | `9764e87b71e41fbdd8f27ec9bd7d571dacec5ca8ce5d07df5b3213007872a3fa` / `17e3362239e6e50ed5bb01e13f4536b3f311b28241383163d70eb6d3aa006ac6` |
| Synthetic visual comparison | `c7c67720fad6a241a8131c8854cff95d046e47f3ef007dfa16bc1de2d7ac878d` |
| Python OpenCV build description | `3b3d376cd2022c9bc8106025d5b72a599ea2c2e2c8075aaddc6ea87c54232dee` |

The prediction and scoring scripts in this folder bind their inputs and
candidate identities. Eight focused tests and pyflakes passed. Both captures
completed in under eight seconds locally. They used no optimizer, development
or sealed payloads, and did not approve or release a model. Generated results
remain in the project-local run directories for the active axis investigation.
