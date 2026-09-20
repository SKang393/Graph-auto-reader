<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recognize rows in taller legend frames

## Problem

Development evidence shows readable multirow legend labels assigned annotation
roles. The frame search restricted both horizontal edges to four text-heights
from each row. A tall legend therefore failed even when all four edges and the
row's symbol were visible. Shorter labels also could not reach the wider frame.

## Implementation

Frame height now comes from its visible edges. Existing horizontal constraints
and continuous side-edge checks remain. A shorter row can share a frame already
established by another label only when it has a separate symbol whose horizontal
extent overlaps the verified symbol column. Missing or ambiguous symbol evidence
does not change the role. Explicit roles and reviewed text remain protected.

The resolver also uses this rule when locating symbols for series assignment.
Its composition version advances to `original-pixel-framed-legend-context-v2`,
which participates in OCR cache identity. Pixels, label geometry, text recognition,
trained weights and thresholds are unchanged.

## Files changed

The framed-legend resolver and its tests, this report,
[aggregate evidence](GOAL-22-MULTIROW-LEGEND.json), and readiness.

## Tests and commands

Release `dotnet test` passes 310 OCR tests with 16 existing optional skips and
35 focused application workflow tests. New cases cover a tall three-row frame,
shorter captions, a missing symbol, a missing frame edge and a detached letter
fragment outside the symbol column. Compilation has zero warnings or errors.

Actual fixed-model CPU OCR completes all 37 original and 37 corrected-layout
panels without failure. Source, executable, model and input hashes are bound.
The comparison confirms all 819 original and 827 corrected-layout non-role
outputs are unchanged, including raw detections, assembled geometry, recognized
text, alternatives and review state. Confidence changes only with a new legend
role. Saved synthetic truth is reused only after these checks, and must reproduce
every previous score exactly before scoring the new roles.

## Metrics and timing

| Correct roles | Before | After |
| --- | ---: | ---: |
| Original train images | 409 / 709 | 409 / 709 |
| Original development images | 134 / 183 | 135 / 183 |
| Corrected train layouts | 412 / 709 | 412 / 709 |
| Corrected development layouts | 138 / 183 | 141 / 183 |

The final rule changes five role assignments: one matched label and one partial
label on original images, and three matched labels on corrected layouts. There
is no loss of a previously correct role. Exact text, detection counts and
character errors remain unchanged. The development bars still fail.

Application runs take 50.01 seconds on original images and 42.70 seconds on
corrected layouts. Authenticated comparison takes 0.88 seconds. These are local
timings, not a performance claim.

## License review

Apache-2.0 source using existing reviewed model/runtime payloads. No new
dependency, weight, font, private input or sealed input.

## Known limitations

One originally split legend label remains partial. Another corrected-layout
legend label is missed and a row is still split into word and suffix. A frame
still needs a qualifying label to establish its horizontal geometry. Unframed
legends and the remaining OCR errors are not resolved by this change. These
diagnostics do not establish marker, axis, export or real-data acceptance.

## Integration notes

The initial tall-frame trial made seven role changes, including two letter
fragments that should not supply independent symbols. The final column check
removes those two assignments while retaining the four matched-role improvements.
Both trial sources, runtimes and results are retained. The historical V1 Python
scoring profile remains frozen; the V2 comparison checks its own explicit source
and runtime bindings without rewriting V1 results.

No training, model revision, production approval, package, tag or release occurs.
Portable build 433, version 0.4.33, remains retained. Source checkpoint `2881e24`
passed Windows CI run `35489519495`. The earlier legend-placement CI was superseded
by that checkpoint and cancelled, not reported as a pass.

## Acceptance status

**FAIL** for Goal 22 completion. All four required product outcomes remain
incomplete. This repair is integrated as an improvement below acceptance.
