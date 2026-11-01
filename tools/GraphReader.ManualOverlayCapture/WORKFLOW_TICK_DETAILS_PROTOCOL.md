<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Candidate workflow tick-detail capture

Run the existing unapproved candidate workflow on the same 20 pinned original
training images, retaining every panel and failure. Record each panel's selected
Y-axis line, emitted tick-region bounds, primary numeric interpretation,
literal-number flag, alternatives, and calibration reasons. Capture does not
read transcriptions, run an optimizer, or open a sealed payload. The extra
observer records existing outputs and does not enable dense tick separation.

After the runtime and report are frozen, compare completion statuses, failure
codes, and marker-stage results with the preceding identical-input capture.
Report how many panels emit three or more numeric Y-tick regions, how many
emitted regions carry a competing numeric reading, and how many panels already
require axis or calibration review. These are opportunities for follow-up, not
accuracy metrics: unreviewed OCR outputs do not provide truth labels. Private
tick text and per-panel observations remain in ignored project-local artifacts.
This diagnostic does not change production policy, model approval, or release
gates.
