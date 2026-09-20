<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Keep synthetic legends clear of graph content

## Problem

After the arrow-label repair, some legends still covered plotted markers and
arrow targets. Moving those observations would change the graph. The legend must
move instead. This defect is visible in project-owned synthetic pixels before
inference; no model answer or private graph is used to choose a location.

## Implementation

The opt-in `ml.synthetic.legend_clearance.separate_legends(scene)` transform
moves each colliding legend's frame, glyphs and text as a unit. Its measured
footprint includes text that extends beyond the nominal box. Clean rendered
occupancy determines the nearest clear position while preserving the declared
inside/outside placement. It returns a copied scene, change records and explicit
unresolved cases. It retains a case when a binding is ambiguous or no space exists.

The transform preserves all observations, calibration, connections, phases,
arrow targets and tails, series identities, label content and unrelated labels.
It updates affected text and artifact references. Callers must retain its report
and use new image checksums, not replace historical benchmark images. Generator
defaults and frozen renderers remain unchanged.

## Files changed

The legend transform and its tests, this report, [aggregate evidence](GOAL-22-LEGEND-CLEARANCE.json),
and the readiness snapshot.

## Tests and commands

`python -m pytest ml/synthetic/tests/test_legend_clearance.py ml/synthetic/tests/test_annotation_clearance.py`
passes nine checks in 19.24 seconds. Checks include immutable inputs, stable
placement, exact marker masks, preserved scientific structures, synchronized
legend elements, hidden legends, ambiguous bindings, missing space and private
input rejection.

`Check-LegendClearance.py` authenticates the previous repair and all 23 input PNGs.
It checks unchanged scientific structures and marker masks, preserves every text
label, and independently renders the background without legends to confirm the
moved footprints cover no existing content.

## Metrics and timing

| Synthetic split | Sources | Legends moved | Covered arrow targets, before / after |
| --- | ---: | ---: | ---: |
| Train | 20 | 5 | 1 / 0 |
| Development | 3 | 8 | 7 / 0 |

The final corpus check takes 220.86 seconds and has no unresolved cases. Its first
attempt found ten ambiguous bindings because measured text dimensions differed
from the legend entry's nominal dimensions. Matching the unique panel, text and
origin resolves that metadata difference while preserving both box sizes. The
initial report and source snapshot remain retained.

## License review

Apache-2.0 code, existing rendering dependencies and system-font resolution.
No external data, dependency, font or weight is added. No private or sealed read.

## Known limitations

This proves clearance from other graph content for the existing synthetic scenes.
It does not resolve every text-layout issue, including labels near axes or text
near its own legend border. OCR accuracy requires a separate actual inference
run. Historical OCR failures and scores remain unchanged.

## Integration notes

No training, new model revision, approval, package, tag or release. Retained
portable build 433 remains version 0.4.33. The next diagnostic uses distinct
derived-image identities and explicitly reports any replayed panel/axis geometry.

## Acceptance status

**FAIL** for Goal 22 completion. The four required product outcomes remain
incomplete; this is a verified generator repair.
