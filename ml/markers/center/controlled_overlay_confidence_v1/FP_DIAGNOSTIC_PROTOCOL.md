<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Synthetic family false-positive diagnosis

The fixed 0.1 development evaluation of the controlled-overlay candidate had
58 unmatched detections across nine family scenes, with 206 annotated markers.
Use the exact synthetic development cache, authenticated proposal coordinates,
frozen experimental ONNX, and recorded scene predictions to locate those
unmatched detections under the evaluator's existing five-pixel matcher. Rerun
ONNX inference on the same reconstructed patches and verify the recorded
predictions before attributing any errors.

Report distances to annotated marker centers and synthetic hard-negative
locations, plus a contact sheet of synthetic ink crops for investigation.
These categories are descriptive, may overlap, and are not new acceptance bars.
The analysis uses only the existing synthetic development split. It performs no
optimizer step, threshold selection, real-corpus or sealed-payload read,
production approval, or release.
