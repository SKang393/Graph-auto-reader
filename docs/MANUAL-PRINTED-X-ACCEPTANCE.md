<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Manual printed-X calibration

Select **Use printed X values** in the manual calibration form when the printed
axis supplies the X scale. Enter the values at the anchors and place all three
anchors on the original image. Each point then receives a calibrated X estimate.
Skipped observation sessions keep their spacing in the CSV. The default remains
observation order for a new calibration starting at session one.

Manual printed-X calibration uses both pixel coordinates of each anchor, including
small departures from horizontal and vertical. Existing projects reopen with their
saved mapping. Screen-vertical phase dividers still require an aligned calibration.

## Owned regression

On source `33116743`, a one-pixel tilt on each axis moved an expected X of 1 to
1.025. The native form also lacked the printed-X selector for manual anchors.
Both regression tests failed on that source. With the repair, the three known
X/Y pairs at sessions 1, 4 and 10 survived save, reopen and export within
`1e-9` graph units. Both audit outputs marked each X as estimated.

```powershell
dotnet test tests/GraphReader.App.Tests -c Release --filter 'FullyQualifiedName~PrintedX|FullyQualifiedName~RotatedManualCalibration|FullyQualifiedName~AffineAxisSuggestion|FullyQualifiedName~ManualCalibrationExposesModeLocalizedXInputsAndValidation|FullyQualifiedName~CalibrationAccessibleTextRefreshesOnLanguageChange'
```

All 62 tests passed in 4.54 seconds.

## Real development graph

One authorized graph had three recorded calibration anchors, ten human-digitized
centers and a separately stored reference CSV. An offscreen Windows WPF window
exercised the manual form through automation peers. The canvas handler received
the recorded anchors. The workspace received the recorded centers, saved the
project, reopened it and wrote the CSV before comparison. Both runs used the
same underlying image, anchors and centers. Original source hashes were verified.

Sessions were matched one-to-one after rounding X to the nearest integer.
The Y comparison used one original-image pixel, converted to graph units by
the magnitude of the affine Y gradient.

| Export | Reference rows | Matched sessions | Missing | Extra | Matched Y within one pixel | Matched Y outside one pixel | Elapsed |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Original manual form, observation order | 10 | 1 | 9 | 9 | 1 | 0 | 2.634 s |
| Manual printed-X selection | 10 | 10 | 0 | 0 | 5 | 5 | 1.988 s |

Across the ten matched sessions the mean absolute Y difference from the CSV
was 1.033 pixels and the maximum was 1.916 pixels. All ten Y values also agreed
when rounded to the whole-number precision written in the reference. The maximum
Y difference from the independent implementation of the three-anchor transform
on the original digitizer clicks was below `6e-14` pixels.

These measurements cover manual calibration, reviewed centers, persistence and
numeric export on one development graph. The reference supplies no phase labels.
Automatic recognition and installed-package operation require separate evidence.
