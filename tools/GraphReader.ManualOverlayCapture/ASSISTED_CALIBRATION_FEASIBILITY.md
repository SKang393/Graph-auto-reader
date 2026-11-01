<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Assisted calibration feasibility result

All eight previously extracted training-only digitization examples matched a
source in the frozen 20-image candidate capture. Their three stored graph-to-
screen anchors define an affine mapping. Seven mappings produced positive,
normally oriented session-1/zero, Y, and X anchors within one captured panel
and within the application's two-pixel axis-alignment allowance. One mapping
exceeded that skew allowance. No examples were excluded for reversed axes,
unsupported ranges, missing or ambiguous panels, or singular anchors.

The derived session-1 and zero positions may be interpolated or extrapolated
from the recorded points. They have not been separately checked against the
printed graph, and the source digitizations are imperfect. Feasibility is not
verification of calibration accuracy, a new user confirmation, or automatic
recognition. The seven eligible examples are candidates for an isolated
assisted diagnostic, not production inputs or an acceptance set.

| Evidence | SHA-256 |
| --- | --- |
| Pre-run registration | `2c62a33f055769e297deade508635d98f9ab0b6266cb15d956b8e534e17cea94` |
| Protocol | `da581830a5c300790779dc3f569f5354efc3f58508fd1a7564acd7d987edd866` |
| Feasibility source | `2328ca8b02b0ac7e57fdafee7b49b12bbab7126906542451f247300c98355d0c` |
| Existing private anchor capture | `899cbd19b7baa39ed00b9882ec3166d83cb80c2d359b4410af39290b601fac12` |
| Frozen workflow capture | `f6d7e6e0dcfa88e3f77c398d4ce6071da59f2d885cba785bb834d985c02f0016` |
| Aggregate feasibility result | `acf38be2abe65a20f7203f08d4f8d41104005d358d16291f9a774ae030efff36` |

One focused synthetic test and pyflakes passed. The diagnostic performed no
inference, optimizer step, development-payload read, or sealed-payload read.
Private anchors and panel identities remain project-local.
