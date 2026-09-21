<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Bounded divider geometry, 2026-09-21

The explicitly open tall-image fixture produced two fitted dividers extending
45 and 70 pixels beyond the left image edge. The fitter intersected only the
top and bottom plot boundaries. OCR correctly rejected those invalid lines.
The fitter now clips against both side boundaries too, preserving line slope
and supporting skewed axes. Nonintersecting segments are withheld. Original
pixels, calibration checks and invalid-coordinate rejection are unchanged.
The axis stage identity advances to `axis-opencv-v5-bounded-dividers`.

Four new geometry cases cover left/right slopes and nonperpendicular axes.
All 68 axis and 428 application tests pass, along with 13/30/15 native archive,
memory and worker checks. All 26 imported panels from 21 open sources now
produce valid OCR masks. The build has no warnings or errors. The full repair
job takes 205.57 seconds. Complete native baseline inference takes 144.49 seconds
and preserves every prior scientific output, metric, export status and model
weight across 23 sources and 39 panels. In-memory OCR baseline metrics also
remain identical. See the [bound evidence](GOAL-22-AXIS-DIVIDER-BOUNDS.json).

Runtime clarification: composed OCR already uses the reviewed source-built
OpenCV binary `c96f91...`. The older full-workflow diagnostic used the NuGet
development binary `1fa122...`. The new full-workflow comparison uses the
reviewed c96 binary required by release policy and reproduces baseline outputs.
The older development binary is not a proposed release replacement. Existing
provenance and distribution gates remain in force.

The complete broader OCR run now returns metrics instead of crashing. It does
not pass: 233/453 text regions match, 186/453 read exactly, 159/453 have the
correct role, and character error rate is 60.87%. All inputs and failures count.
Its inference takes 32.50 seconds. Earlier success on the narrower nine-panel
development slice does not establish general OCR readiness.

Open per-case diagnosis finds lost source context before OCR: some skewed or
stacked graphs are cropped down to short axis fragments, omitting headings,
legends and complete panels. Other small/large/degraded text failures also
remain. Fix the importer's source coverage on owned development before choosing
new weights. These OCR coverage fixtures are not end-to-end CSV acceptance
cases; most deliberately omit printed x labels. Never fabricate their x values.

No training, private or sealed read, approval, package or dependency change.
Prior sealed execution-error accounting and two unused reserves are preserved.
Build 433 remains 0.4.33. Goal 22 remains incomplete and development continues.
