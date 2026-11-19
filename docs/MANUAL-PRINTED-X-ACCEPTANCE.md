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

## Recovering a refused printed scale in Review

When **Review measured tick positions** is available in the inspector, open it
and check the original image against the marked positions. The form shows the
endpoint OCR readings and leaves both X fields empty. Check the Y scale, enter
the two printed X values, select **I checked anchor pixels and actual labels**,
and apply. This reuses measured positions without requiring three anchor clicks.
Add points to the intended series and place its phase dividers. The saved project
records the detection run, original-image hash, measured positions, endpoint
readings and confirmed calibration. Its point IDs link to both export audits.

On one nonsealed development graph the image-only candidate retained 23 centers.
Five matched the selected reference within five original pixels, 18 were extra
and five were missing. Two horizontal endpoint conflicts prevented calibration.
The candidate produced zero numeric rows. Its eight remaining divider detections
were available for inspection as marker candidates.

An offscreen Windows WPF window imported that graph and ran the frozen local
candidate. A simulated reviewer used the printed labels to supply two X values,
confirmed the offered Y scale and applied the calibration. The reviewer selected
one filled-circle series and its track, placed ten centers and three dividers
from the image, and assigned phase codes. The window saved, closed and reopened
the project before export. The reviewer acknowledged two warnings about
unspecified later phase semantics. No candidate boxes were bulk accepted.
File-dialog destinations and all reviewer actions were scripted. Center inputs,
series selection and calibration choices were fixed before numeric comparison.

| Outcome on ten reference rows | Image only | After review |
| --- | ---: | ---: |
| Matching rounded X sessions | 0 | 10 |
| Missing / extra sessions | 10 / 0 | 0 / 0 |
| Y within one original pixel at matched sessions | — | 7 |
| Y outside one original pixel at matched sessions | — | 3 |
| Mean / maximum absolute Y error in graph units | — | 0.365 / 1.000 |

Seven Y values also agreed at the reference's whole-number precision. One pixel
represented 0.260 Y units. The mean absolute difference in unrounded X was
0.0453 sessions. Both audits identified all ten points as manually corrected
with estimated X and linked them to the reopened project. The native run took
37.639 seconds through detection and 39.235 seconds through CSV export after
candidate initialization. Script timings do not measure human effort. The
independent reference supplies no phase labels. Package activation, accuracy
across studies and fully automatic numeric extraction remain unverified.

The owned controls exercise ambiguous X, missing scale input, invalid Y evidence,
skew, stale panel evidence, duplicate receipts, explicit review and persistence.

```powershell
dotnet test tests/GraphReader.App.Tests -c Release -p:NuGetAudit=false --filter 'FullyQualifiedName~MeasuredCalibration|FullyQualifiedName~PrintedX|FullyQualifiedName~NativeMarkerPreviewToggle' --nologo
```
