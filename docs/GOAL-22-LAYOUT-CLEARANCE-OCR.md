<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR measurement after synthetic layout clearance

## Problem

The arrow-label and legend repairs change synthetic pixels. Their effect needs
actual application inference on separately identified images, not reused scores
or a replacement of historical failures.

## Implementation

The existing OCR evaluator has a separate layout-clearance input command. It
authenticates the original 23-source, 37-panel train/development inventory and
the generator sources. It retains the original application's panel crops, plot
bounds and phase-divider geometry explicitly as replayed context. It verifies
each new panel's Gray8 and BGR pixels against the declared crop of its full source.
Duplicate, missing, cross-split, mismatched or truth-bearing inputs fail closed.

Both runs use the same fixed V44 detector, official recognition payload, CPU
provider, postprocessing and executing assemblies. The application receives no
text truth. Model, manifest, license, native runtime, source and assembly hashes
are bound. Scoring authenticates those outputs before obtaining synthetic truth.
This is an OCR diagnosis, not a new model revision or end-to-end acceptance.

## Files changed

The existing evaluator and command dispatch, the separate layout input reader,
their self-checks, this report, [aggregate evidence](GOAL-22-LAYOUT-CLEARANCE-OCR.json),
and readiness. Product application behavior is unchanged in this batch.

## Tests and commands

Release compilation succeeds with zero warnings and errors. The evaluator's
`--self-test-official-head-candidate` passes 42 checks, including rejection of
private/truth-bearing input, missing cases, incorrect crop offsets, out-of-bounds
crops, and mismatched grayscale or color pixels.

`--evaluate-legend-context-candidate` completes the 37 original panels and
`--evaluate-layout-clearance-candidate` completes the 37 derived panels, with zero
runtime failures. The original run reproduces all 819 historical recognized
texts, roles, confidence values, review states and source polygons. The 18 panels
whose decoded pixels are unchanged retain all 425 recognized outputs exactly.

## Metrics and timing

The frozen V2 per-item matcher and full 709 train / 183 development denominators
are retained. Every original label is aligned to its derived counterpart through
the recorded generator IDs. No label is removed from scoring.

| Measure | Original images | Corrected layouts |
| --- | ---: | ---: |
| Exact development text | 141 / 183 | 146 / 183 |
| Matched development labels after OCR assembly | 152 / 183 | 161 / 183 |
| Extra development labels after assembly | 24 | 19 |
| Correct development roles | 134 / 183 | 138 / 183 |
| Development character edits | 396 / 1019 | 235 / 1019 |
| Exact train text | 396 / 709 | 405 / 709 |
| Correct train roles | 409 / 709 | 412 / 709 |

Development has seven exact-reading gains and two losses, plus seven role gains
and three losses. Train has eleven exact gains and two losses, plus four role gains
and one loss. Raw detector precision falls from 74.33% to 72.45%; raw recall rises
from 75.96% to 77.60%. These raw results remain distinct from the assembled output
above. All five existing development acceptance comparisons still fail.

Application inference takes 35.16 seconds before and 49.59 seconds after. Scoring,
including authenticated synthetic regeneration, takes 71.00 seconds. These are
local diagnostic timings, not a performance benchmark.

## License review

Existing Apache-2.0 models and reviewed native runtime are checksum-bound.
No new dependency, weight, font or external input is added. No private or sealed
data is accessed. All new source is Apache-2.0.

## Known limitations

The input images change, so this does not prove better performance on the original
images. Twenty-two development labels remain missed after assembly. Three labels
in the multirow legend are still assigned annotation roles; another legend entry
is missed. Participant/axis-title collisions and other OCR errors remain.
The replayed panel/axis context does not establish current import, axis detection,
calibration, marker detection, CSV or private-data acceptance.

## Integration notes

Initial compilation found two unnecessary array copies and a repeated constant
array in test code; the normal analyzer requirements were satisfied. The scorer's
first attempt compared assembly lists in different orders and stopped before
truth access; identity comparison now uses assembly names. Model inference was
not repeated for either repair. Prior source and runtime evidence is retained.

No optimizer step, production approval, package, tag or release occurs. Portable
build 433, version 0.4.33, is retained. The annotation repair passed Windows CI run
`35488736902`. Development continues while subsequent CI runs.

## Acceptance status

**FAIL** for Goal 22 completion. The measured layout repair helps OCR but all four
required product outcomes remain incomplete.
