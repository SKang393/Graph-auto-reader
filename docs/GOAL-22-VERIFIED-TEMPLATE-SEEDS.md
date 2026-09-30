<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Verify original-pixel template seeds, 2026-09-30

Rejecting an off-center classifier crop used to remove the full glyph needed
to recover neighboring markers. A controlled replay attributes 53 missing
centers to that search dependency. Simply admitting rejected crop templates
regresses V5 to 720 matches and 55 false points, so that probe is rejected.

The application now measures complete, unambiguous ink components from
text-filtered detector inputs. It recenters and classifies each isolated glyph
through the existing original-pixel content-isolation path, then chooses at
most two accepted glyphs per shape/fill. Legend evidence stays available.
Seed inputs never become observations directly. Every recovered center still
passes the unchanged classifier cutoff, text/legend exclusions and duplicate
guards. Model weights, thresholds, source pixels and acceptance bars stay fixed.

| Configuration | Correct native markers | False markers | Exact text | Native seconds |
| --- | ---: | ---: | ---: | ---: |
| Retained V5 | 726/838 | 25 | 348/453 | 192.85 |
| Unapproved V9 | 744/838 | 26 | 348/453 | 166.88 |

V5 keeps its aggregate result, retaining 725 previous matches and exchanging
one match for another. V9 retains all 675 previous matches and recovers 69,
while false points fall from 45 to 26. Against V5, repaired V9 gains 27
matches and loses nine. Its 88.78% recall still fails the existing 95% bar.
All 31 panels and 838 truths remain per arm; axes stay at 84/93 corners.

| Configuration | CSV exports | Correct values | Correct rows | Wrong-phase rows | CSV seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Retained V5 | 12/23 | 409/706 | 405/724 | 3 | 263.39 |
| Unapproved V9 | 11/23 | 387/706 | 384/724 | 2 | 278.31 |

V5 preserves every exported numeric row and source completion/failure state
exactly. Both arms retain all 23 sources, 39 panel observations, 706 unique
points and 724 expected rows, with the original train/dev membership. OCR and
axes are unchanged. No scored wrong-scale, duplicate or failed-source residual
rows occur; application safety checks still govern export. Source failures
remain counted, including the nonzero native execution exit codes.

All 508 App tests pass, including four new seed/geometry/rejection checks.
The native build has zero warnings or errors. Checks take 114.26 seconds.
The actual workflow runs use CPU with the existing aggregate cap and all
processors eligible. Seed verification adds runtime; this is not an
instantaneous per-core utilization guarantee.

An inherited audit flag incorrectly described the model weights as changed.
Separate corrected metadata verifies that all four model bindings match each
prior run. Original reports remain; metric files are byte-identical and no
inference was repeated for this correction.

The remaining 94 V9 misses include 50 without a nearby recorded above-threshold
center and 25 with a nearby classifier rejection. Nine are near a center
assigned to another truth; other misses involve suppression or exclusions.
These are descriptive stage counts, not proof of a single cause or permission
to weaken a threshold. Crowded hexagons account for 61 remaining misses.

The [aggregate evidence](GOAL-22-VERIFIED-TEMPLATE-SEEDS.json) binds sources,
tests, replay controls, native runs, CSV scores and metadata corrections.
Keep V27/V5 as the model selection. V9 remains unapproved and its original
failed revision outcome stays intact. No private/sealed reads, optimizer
steps, new dependency, packaged build, production switch or release occurred.
Existing Apache-2.0 provenance is unchanged. Build 433 remains 0.4.33;
Goal 22 remains incomplete.
