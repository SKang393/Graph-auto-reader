<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Separate synthetic arrow labels from legends

## Problem

Saved development OCR contains missing and mixed legend words where the generator
draws an arrow label over a legend. These are project-owned synthetic images.
The collision is visible before model inference and does not depend on an OCR
answer. It is a generator layout defect, not evidence for changing model weights.

## Implementation

The opt-in `ml.synthetic.annotation_clearance.separate_arrow_labels(scene)`
transform measures the existing font and rendered obstacles, then moves an
overlapping arrow label and its arrow tail together to the nearest clear location
inside the same panel. It retains the arrow target, all label text, observations,
calibration, phases and series. It returns a copied scene plus explicit changes
and unresolved cases. Historical scenes, generator defaults and renderers are
unchanged. Callers must retain the returned change report and assign the derived
image its own checksum; it must not replace a historical benchmark image.

The transform leaves a case unchanged when a unique arrow cannot be identified,
its target lies inside the legend, or no clear label and arrow path is available.
It does not delete inconvenient examples. Only project-owned procedural scenes
are accepted.

## Files changed

The transform, focused tests, this report, its [aggregate evidence](GOAL-22-ANNOTATION-CLEARANCE.json),
and the [readiness snapshot](1.0-READINESS.md).

## Tests and commands

`python -m pytest ml/synthetic/tests/test_annotation_clearance.py` passes four
checks in 3.53 seconds. They cover deterministic and idempotent placement,
unchanged input and scientific structures, exact marker-mask preservation,
rendered label clearance, hidden frames, retained unresolved cases and segment
geometry. An initial invalid test fixture used an empty degradation list; the
fixture was repaired to retain the schema-valid existing configuration.

The local `Check-AnnotationClearance.py` check authenticates all 23 historical
source PNGs and the frozen generator bindings before creating separate derived
images. It checks every scene's scientific structures, arrow targets, marker
masks, text content and unrelated labels. It checks the new rendered text bounds,
not just the proposed boxes. No inference is run.

## Metrics and timing

| Synthetic split | Sources checked | Labels moved | Unresolved collisions |
| --- | ---: | ---: | ---: |
| Train | 20 | 6 | 0 |
| Development | 3 | 2 | 3 |

The corpus check takes 84.57 seconds. All 23 sources retain their observations,
calibration, marker masks, arrow targets and label content. All three unresolved
cases have an arrow target inside the legend. They require a separate legend
placement repair; moving a data point would be incorrect.

## License review

New code is Apache-2.0. Existing renderer, system font resolution and dependencies
are reused. No font, weight or external data is added. No private or sealed input
is read.

## Known limitations

This repairs arrow-label placement only. Other text collisions and transparent
legends over data remain. Derived images have not been evaluated by OCR. Neither
an accuracy improvement nor a model gate pass is claimed. The historical OCR
failures remain valid for their original images and archived runtime.

## Integration notes

No optimizer step, model revision, production approval, packaged build, tag or
release occurs. Portable build 433, version 0.4.33, is retained. Preceding source
checkpoint `8d915c7` passed Windows CI run `35488129750`.

## Acceptance status

**FAIL** for Goal 22 completion. The layout repair is verified on the existing
synthetic scenes; the four required product outcomes remain incomplete.
