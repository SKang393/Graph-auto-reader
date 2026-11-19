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
