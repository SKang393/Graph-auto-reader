<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Source-group holdout protocol

Use the five `internal_train_holdout` groups already assigned by the v7
manual-train overlay plan. Before viewing their images or labels, run the
existing `AxisGeometryDetector` with the reviewed OpenCV runtime, the unchanged
v1 vertical-axis proposal rule, and the existing official OCR composition.
Freeze the axis, proposal, and recognition outputs before inspecting the
holdout images. Use no Engauge calibration anchors or text values as inference
inputs. Do not alter thresholds or rules after examining the holdout.

After inference, visually review numeric Y-axis labels and their locations in
the five images. Record annotations only in ignored project-local artifacts.
Compare predictions with those labels and report matches, incorrect or empty
readings, missed labels, and unmatched numeric predictions. Mark any unreviewed
or ambiguous regions separately; do not automatically treat them as negatives.

The split is source-study-plus-exact-image disjoint from the 15 build groups,
but it remains a small internal split within authorized training data. Historical
and sealed-source overlap is not fully established, and assistant-reviewed
annotations are not independent expert ground truth. This check is not a
release gate, full-graph workflow, or product acceptance test. No optimizer
training, development payload read, or sealed payload read is authorized here.
