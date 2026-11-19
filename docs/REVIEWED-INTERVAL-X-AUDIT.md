<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Auditing a calibration confirmed from printed labels

Review already lets a reviewer inspect raw OCR, correct or exclude a label, refit the remaining labels and explicitly apply the proposed scale. The project now saves the readings behind that decision in the existing calibration change event. Each row records the original text and polygon, any edit, inclusion and its fitted value and position when uniquely attributable. Estimated positions between strokes remain distinguishable from measured tick positions. The event also records the proposed fit's relationship to the confirmed anchors and range. Changing the label selection discards the old fit. Historical projects keep their existing audit records.

## Windows development checks

An independent native review reader found 15 individual X labels on a graph for which automatic OCR supplied just two numeric X regions. With both automatic regions withheld, the 13 disjoint literal readings predicted their original glyph centers with 0.409 px RMS error. At the disputed region the two readings had position errors of 0.039 and 26.675 px. This verifies that review could recover the printed evidence on this image. The second reader is approved for review assistance.

In an isolated WPF run a simulated reviewer excluded two merged text regions, refit 15 estimated X positions and seven measured Y ticks, and confirmed the displayed scale. No calibration values were typed. The saved project, subsequent detection project and reopened reviewed project contained the same 27 original OCR proposals and one calibration decision. A control with an edited label and overridden endpoint retained both the proposed pair and final confirmed value. A second control changed the input selection and checked that a manually entered calibration could not inherit the discarded fit.

The development candidate reopened the project from the review profile, detected 15 points and initially marked all 15 unreviewed. The reviewer moved one point horizontally, reassigned six points, withheld the disconnected series, linked a shared baseline and accepted one point. Both phase labels remained unconfirmed. After warning acknowledgement, the assisted preview and export produced 12 rows. The row audit CSV and JSON agreed on all 12 point actions and phase review statuses.

## Independent scoring

Image-only results and native review outputs were hashed before accessing the independent coordinates. Across two authorized development images the automatic candidate emitted zero of 26 numeric rows. One image retained an X-reading dispute, and the other retained two X disputes and a column residual above tolerance. Detection matched 17 of 38 proposed centers to 26 reference centers at the fixed 5 px tolerance. Automatic numeric error is undefined.

On the assisted image, 12 of 16 reference sessions matched, with four missing and zero extra exported sessions. Ten of the 12 Y values were within one reference pixel. Mean absolute error was 0.02395 graph units and maximum error was 0.08422. The before and after exports had the same numeric results. These checks cover project provenance and native review on exposed development images. Automatic calibration and generalization remain unverified.

## Review of two labels in one crop

A follow-up starting from `30d37e31` traced the candidate and review readers separately. On the image with the X dispute, the automatic detector returned two numeric X boxes and two small fragments near the printed row. None enclosed two labels. The review detector produced two merged regions, each spanning a pair of measured tick intervals.

The review worker can now reread both original ink groups when each covers the midpoint of its own interval and the detector has found at least three separate interval labels on that row. Both cropped readings must reproduce the whitespace-separated tokens in the uncut reading in order. A disagreement, a possible thousands grouping, crossing ink or insufficient geometric support keeps the combined region available for review. Its original text, confidence and polygon remain inspectable when a split succeeds. The reviewer still confirms the calibration.

On the development image, the draft improved from 15 to 19 individually read X labels. It retained seven measured Y ticks and required zero manual exclusions, down from two. All 19 X positions were estimates between strokes. The same four cropped readings and both parent readings survived save and reopen. The other development image supplied zero rows to the native label review step.

The isolated WPF review profile completed import, scale confirmation, manual placement of two points, save, reopen, preview and export in 8.08 seconds. Its mandatory series was supplied for this two-point workflow check. The audit recorded both manual placements. A separate candidate reopened the review profile's saved scale, detected the points and exported 12 sessions after the edits described above. This took 23.22 seconds. The CSV still had four missing sessions and two matched Y values outside one reference pixel.

The original source-scale V46 candidate continued to refuse numeric output on both images. End-to-end runs took 14.15 and 4.41 seconds. Across the two images, 17 of 38 proposed centers matched 26 reference centers within 5 px. We hashed the two candidate results, native projects and exports before scoring. No sealed evaluation was used. Temporary copies and inference outputs were retired after validation.

The Windows Release test host passed 23 focused OCR geometry tests and 249 review, receipt and calibration tests. To exercise the new owned controls directly, run:

```powershell
dotnet test tests/GraphReader.Ocr.Tests -c Release --filter FullyQualifiedName~IntervalCenteredLabelGroupingTests
dotnet test tests/GraphReader.App.Tests -c Release --filter FullyQualifiedName~CalibrationOcrIntervalPairTests
```
