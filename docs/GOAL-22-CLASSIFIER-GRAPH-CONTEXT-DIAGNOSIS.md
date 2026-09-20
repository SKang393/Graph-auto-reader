<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Classifier coverage in complete graphs

The native-context adaptation improved shape recognition but learned to reject
some legitimate printed markers. The failure is present in owned synthetic
training graphs. It does not justify another private-data run or a weaker
acceptance bar.

## Evidence from the saved inputs

The same original-pixel crop calculation reproduces the application's decisions
on all 541 previously accepted markers and all 557 markers accepted by the
adapted candidate, including newly reached panels. Maximum probability difference
is 0.000004530, within the existing 0.00001 numerical tolerance. This replay takes
4.76 seconds and does not repeat the OCR or center detector.

| Same previously retained true points | Existing classifier | Native-context candidate |
| --- | ---: | ---: |
| Training graph points retained | 342 / 342 | 305 / 342 |
| Development graph points retained | 179 / 179 | 179 / 179 |
| Correct training shape labels | 221 / 342 | 262 / 342 |
| Correct development shape labels | 169 / 179 | 176 / 179 |

All 37 newly rejected true points come from four training images: 23 filled
circles and 14 open squares. Their median center error is 0.394 pixels, with a
maximum of 0.756 pixels. Estimated radius is 0.916 to 1.084 times the authored
radius. Incorrect localization or grossly wrong crop sizes do not explain them.
Their images use erosion, ink bleed, halftone and line-contact damage that the
small native-context curriculum did not contain.

The candidate also retains 16 of the 20 previously accepted non-marker crops.
Visible text fragments require component-sized training crops; centering a crop
on a whole word does not cover all inputs that the detector supplies.

## Complete denominator and limitations

The replay also samples all 706 authored point locations independently of
detection. Candidate shape recognition is 419 / 500 on training graphs and
205 / 206 on development graphs. Candidate retention is 429 / 500 and 205 / 206.
These supplied-location measurements diagnose classification only. They cannot
establish detection recall or successful automatic calibration and export.

Saved accepted detections do not include proposals rejected by both classifiers.
The earlier full workflow result, eleven complete graphs and twelve failures,
remains the end-to-end result. No source or failed graph is removed.

## Corrected next step

Extend the owned training population with varied incoming/outgoing line angles,
thick strokes, print damage and text fragments, plus original-pixel crops from
all twenty existing training graphs. Preserve the exact previous development
tensors and labels. Use image visibility and exact-pixel ambiguity checks before
training. Keep all previous examples and retain invalid preparation attempts.

This is a data-coverage repair using the existing classifier architecture,
runtime crop and artifact threshold. No private or sealed data is read. No
production model is replaced, no release is made, and build 433 stays 0.4.33.
All four Goal 22 outcomes remain incomplete.
