<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recovering Y labels from measured tick rows

On a project-owned 612 × 328 graph with a 31-pixel left margin, the primary text detector returned three boxes. All six Y labels fell outside them. The original-pixel component detector found ink for each label, but the previous recovery path required a recognized Y label to locate others. The recognizer received no Y crops. The fitted-axis stroke verifier separately found five of the six drawn ticks.

When the original pass has no verified Y anchors, OCR can now propose a crop from a glyph on the same row as a unique outward tick. A second aligned glyph on a different tick must support the lane. Two fragments claiming one tick cannot establish a pair. Each proposal retains its own identifier, recognition alternatives and original polygon. For automatic calibration, the fitted-axis verifier must independently associate the numeric reading with an attached stroke. An unverified association keeps calibration in Review.

| Original raster, same axes and markers | Y roles before | Y roles after | Verified Y fit inputs after |
| --- | ---: | ---: | ---: |
| Six printed labels | 0 | 6 | 5 |
| No Y labels | 0 | 0 | 0 |
| Two off-row numerals and nearby text | 0 | 0 | 0 |

The six primary readings on the positive control matched the printed values. Alternatives still disagreed on two Y readings and three X readings. Native WPF Auto Detect showed the calibration-required status, exposed six candidate centers through the toggleable overlay, and left the point table empty. The installed preview was outside this test.

Render the controls on Windows with `python tools/GraphReader.ManualOverlayCapture/render_y_tick_controls.py <empty-output-directory>`. The script uses the local Arial font. The three regenerated PNGs matched the evaluated images byte for byte. Focused validation passed 28 OCR tests and 58 application tests covering crop selection, independent stroke verification, conflicting labels and candidate workflow behavior.

On two nonsealed development graphs, image-only inference still produced zero fully validated calibrations and zero numeric rows against 26 reference observations. The new Y evidence improved diagnosis on one graph. Marker matching at five original pixels remained 17 matches among 38 retained centers against 26 sampled centers. These development runs provide no estimate of general accuracy.
