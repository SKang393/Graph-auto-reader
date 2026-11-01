<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Frozen internal holdout result

The predictions bound by `HOLDOUT_FREEZE.json` were created before the five
source images and their printed labels were visually reviewed. The review
found 52 numeric Y-axis labels across six visible panels in those five images.
Matching each of the 39 proposed boxes to at most one label at IoU 0.5 gave:

| Measure | Count |
| --- | ---: |
| Matched labels read exactly | 23 / 52 |
| Matched labels read incorrectly or empty | 0 / 23 |
| Labels without a matching box | 29 / 52 |
| Unmatched proposal boxes | 16 / 39 |
| Unmatched proposals with numeric OCR text | 2 / 16 |

Unmatched numeric text was not adjudicated as a false positive. The images
were sent directly to one axis detector per image, without the application's
panelizer; one source contains two visible panels. This is a candidate failure
on the tested composition, not proof that panelized application import fails.
The label boxes and transcriptions were checked by the assistant after the
predictions were frozen, not independently adjudicated. Their windows were
selected manually and boxes were formed with a fixed intensity threshold.
This small split is within the authorized training data; historical and sealed
source overlap is not fully established. The result is not release acceptance.

The private label manifest SHA-256 is
`0d80b0663457f25a89e7d39a0e60f5e156604a0d382dcb9e8f132f7c29331713`,
the frozen OCR predictions are
`75705954477b11aa99a0d54881bcb7c414f8cda8731a696f6e99d3dedcaa1d30`,
and the aggregate score report is
`c9568e34d7c36720bdfd150cd0dc25e8b3f965f6b5552e7cfa4d68328453a268`.
No optimizer training, development payload read, sealed payload read, or
production model promotion occurred. Private outputs remain only for the active
panel and axis investigation and will be retired when that consumer finishes.

## Panelization and axis follow-up

After label review, the actual raster panelizer split the two-panel image into
two panels. Across all five images it produced five panels from four images;
the remaining image produced `RASTER_NO_PANEL_DETECTED`. The normal image
importer retains that source as a full-image review panel on this result, as
covered by `GenuineNoPanelResultRetainsFullImageForManualReview`.

Both detected panels of the two-panel image received axis results with very
short reported extents: approximately 8.5 pixels vertically on one panel and
14 by 72.6 pixels on the other. They required review and are not evidence of
correct geometry. This prompted a targeted correction: axis confidence and
partial-axis reasons now use the shorter of the observed line-family span and
the reported plot extent. The change does not select a different axis or solve
the localization failure. Two synthetic regression cases failed before the
correction; the full axis test suite subsequently passed 105 tests.

The pre-correction panel report SHA-256 is
`0ebeb0a39f9e6393c55b2b8275848d6f7634b14112096d9889c507061fa107cf`.
The post-correction report SHA-256 is
`7c0e5fbc6c4a174d110344b69ee14bf968da0f0e4d5ad5b37afcc484351cade7`.
All five returned panel geometries were unchanged and none gained confidence;
both short-axis panels carried partial-axis reasons. The panelizer's 21 focused
application tests passed. This follow-up is exploratory on already exposed
images, not an independent evaluation.

The broad solution run passed 2,364 tests, skipped 25, and failed one Git ignore
contract test because the checkout was not marked safe for the subprocess. The
same test passed separately after `safe.directory` was supplied in its process
environment. This was not a full clean rerun of the solution.
