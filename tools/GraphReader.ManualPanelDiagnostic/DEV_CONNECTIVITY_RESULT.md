<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Development axis-connectivity review

The frozen Y-label proposal and local connectivity rules were applied to 16
study-partitioned development images using the previously captured axis
geometry. All 16 panels were measured and produced 134 proposals. Proposal
boxes and connectivity flags were saved before review; the assistant reviewed
private crop sheets without displaying those flags.

| Assistant classification | Proposals | Connected to vertical axis at 128 | Connected at 200 |
| --- | ---: | ---: | ---: |
| Numeric text or a numeric fragment | 119 | 0 | 0 |
| Other text or a text fragment | 12 | 0 | 0 |
| Graph structure | 2 | 1 | 2 |
| Uncertain | 1 | 0 | 1 |

The review classifies only emitted boxes, so it does not count labels the
proposal rule missed. These images were already inspected for axis geometry;
the 16 images come from five studies and contain repeated visual layouts. The
assistant's classifications are not independently adjudicated, and only two
clear structural proposals were present. This is evidence for investigating a
review signal, not evidence that connected regions are safe to delete or that
automatic recognition passes. A synthetic touching-label fixture also shows
that genuine text can share an axis component.

| Evidence | SHA-256 |
| --- | --- |
| Frozen protocol | `3e8f703e4ade6595b3ccfd9a72d901dbe2ebee8833b9b8eee6d4178363c68c2c` |
| Truth-blind proposals and connectivity | `49efae065135c157de683458d751d16e0d6f3c24efbec0300d410ef5fc13197b` |
| Private flag-blind review sheet manifest | `b9f90e3a2aca3ecd725c1596d6769fd0c5f3fb939bff490a327aa0e82f7977dc` |
| Aggregate score | `3d1f57b73c5b36cccf5c11d5e68a1411205a6e0cda11ff08c81132b0d3daca47` |

Eight focused Python tests and pyflakes passed. The first proposal fixture
placed ink partly outside the existing left search band; moving it inside the
band corrected the test's expected box. CPU image processing was used, with no
optimizer or sealed-data reads. Review sheets and per-proposal labels remain
private project-local artifacts for the active axis investigation.
