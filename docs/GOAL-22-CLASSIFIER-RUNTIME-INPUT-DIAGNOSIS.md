<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Goal 22: classifier input diagnosis

The classifier still confuses symbol identity before grouping. This checkpoint
adds matching data preparation and records two diagnostic results. It does not
change app behavior, train a model, or pass Goal 22.

## What the app actually sees

In one owned synthetic graph, all 24 circles are detected, but eight are labeled
diamond or other. Replaying the exact production decoder, seed masks, residual
masks and connection builder finds 22 of 23 adjacent connections, including eight
between differently classified shapes. The grouping rule then separates those
shapes. Removing shape separation would risk merging genuinely different series.
This replay does not claim full grouping parity because the saved observer did
not retain legend-symbol classifications.

The historical classifier generator draws at the final patch resolution.
Production instead samples a small original symbol into a 32-pixel crop.
`runtime_patches.py` now reproduces the original-image alpha/color conversion,
crop coordinates, padding and bilinear sampling in the data tools. Against all
534 actual C# inputs from 30 saved panels, **all 546,816 float samples match
exactly**. The declared absolute tolerance was 0.000001; observed error is zero.
Historical generator data, model weights and runtime code remain unchanged.

## A tighter crop was rejected

One fixed diagnostic whitened only the pixels beyond each predicted symbol's
radius. It initially improved the 24-circle example but damaged other shapes:

| Saved matched markers | Original correct shape | Tighter crop |
| --- | ---: | ---: |
| Training inventory | 219 / 340 | 241 / 340 |
| Development inventory | 169 / 179 | 119 / 179 |

The inventory includes all 534 accepted detections, including 15 unmatched ones.
Every saved baseline classification is reproduced; maximum probability
difference is 0.000002027. No threshold sweep or runtime change follows.
This inventory's matched training shapes are only circles/squares, while its
development shapes are triangles, diamonds and stars. It is not balanced
classifier acceptance evidence.

## Balanced diagnosis

The new unsealed diagnostic uses the existing owned graph renderer. Each of the
nine shapes sees 240 identical combinations of fill, radius, stroke, subpixel
position and context. Each native-raster patch is paired with a directly sampled
fine-raster reference of the same intended geometry. The reference is neither
the historical training pipeline nor a proposed upscaling repair.

| Context | Cases | Native correct shape | Fine-raster reference |
| --- | ---: | ---: | ---: |
| Isolated symbol | 432 | 322 | 332 |
| Horizontal connecting line | 432 | 284 | 285 |
| Sloped connecting line | 432 | 125 | 133 |
| Nearby second symbol | 432 | 218 | 233 |
| Axis through symbol | 432 | 123 | 164 |
| **Total** | **2,160** | **1,072** | **1,147** |

The reference repairs 179 errors and loses 104 correct shapes. Artifact rejection
also rises from 319 to 362; correct accepted shapes are 994 and 1,036. Raster
resolution contributes but does not explain the larger context/glyph coverage
deficit. Cropping or increasing image size alone is not an adequate repair.

Observable fill is correct on 1,006 / 1,120 native cases and 1,007 / 1,120
reference cases. Degraded fill is not assigned a guessed classifier label.
Open/filled crosses and asterisks are also unscored because this renderer draws
them identically. No scientific labels are inferred from visual ambiguity.

## Verification and limits

The preparation/coverage tests pass **29 / 29**, with no skips, in 4.76 seconds.
C# parity takes 2.40 seconds. Generating all paired cases takes 7.93 seconds;
frozen CPU inference and tensor writes take 0.86 seconds, using one worker.
No repeated detector/OCR or complete-workflow inference was needed.

Tests: `python -m pytest ml/markers/classifier/tests/test_runtime_patches.py
ml/markers/classifier/tests/test_runtime_diagnostic.py -q -p no:cacheprovider`.
The [evidence record](GOAL-22-CLASSIFIER-RUNTIME-INPUT-DIAGNOSIS.json) binds exact
sources, snapshots, tests, model, reports and diagnostic definitions. Local
failed extraction attempts remain recorded: an inaccessible internal panelizer
type was repaired using its frozen assembly, and named floating-point values
were parsed using the observer's existing serialization options. Neither failure
read sealed data or consumed a candidate.

The helper covers identity-transform original-pixel input, not enhanced
transforms or legend-specific content isolation. Diagnostic centers/radii are
known, so this does not evaluate detection, OCR, grouping or export. These are
new owned diagnostic primitives, not new training/dev/sealed acceptance splits.
The existing approval and historical evidence are preserved. All new code is
Apache-2.0; existing licensed dependencies and model weights are unchanged.

Next: use verified runtime preparation in a separately defined classifier
coverage repair and continue independent OCR/calibration integration defects.
Do not weaken series identity or guess fill to hide these errors.

Full workflow evidence remains eleven completed sources and twelve failures,
226 / 706 correct unique values and 218 / 724 correct relational rows. No
training, private/sealed read, new model revision, production activation or
package occurred. Build 433 remains 0.4.33. **Goal 22 remains incomplete.**
