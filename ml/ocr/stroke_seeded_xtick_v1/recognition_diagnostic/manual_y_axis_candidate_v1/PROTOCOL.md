<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Training-only vertical-axis label proposal diagnostic

Freeze this candidate before proposal scoring. Given the supplied Engauge
calibration anchors, derive an original-pixel vertical axis and inspect a strip
to its left. Threshold the strip with Otsu, retain connected ink components at
least five pixels high and four pixels in area, join horizontally adjacent
components with at least 60% vertical overlap, and retain groups within four
group heights of the axis. Numeric label text and boxes are not inputs to the
proposal pass. Compare proposals with the separately reviewed boxes at IoU 0.5.

The first four build-group images are the fit cohort; the next four are an
exploratory comparison. The four-height gap was selected after inspecting fit
geometry. Both cohorts had already been visually inspected and aggregate gap
geometry for both had been observed before this protocol, so the comparison is
not blind validation. The 76 annotations cover selected numeric axis labels
only; unmatched proposals are not verified false positives.

This experiment uses supplied manual calibration, not an automatic axis
detector, and it does not train a model or modify product code. It cannot
establish whole-graph recognition, generalization, independent acceptance, or
release readiness. Source images and case-level results stay in ignored
project-local artifacts; no development or sealed project payload is opened.
