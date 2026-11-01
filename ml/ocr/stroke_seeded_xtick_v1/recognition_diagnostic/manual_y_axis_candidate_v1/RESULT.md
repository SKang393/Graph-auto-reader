<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Vertical-axis label proposal result

The frozen train-only proposal rule found boxes overlapping all 76 visually
reviewed numeric labels at IoU 0.5. It produced 78 boxes in total: 28 boxes for
28 labels in the fit group and 50 boxes for 48 labels in the comparison group.
After scoring, visual inspection identified the two unmatched boxes as parts
of a rotated axis title.

The application's existing official OCR composition read 75 of the 76 matched
boxes exactly; the remaining reading had a trailing mark. The two title
fragments produced nonnumeric text. Applying the existing numeric-format rule
to the 78 outputs retained 75 readings and withheld the other three. The
numeric V5 reader produced 33 exact, 7 wrong, and 36 empty readings on the 76
matched boxes. No model training or production change occurred.

This result has important limits. The vertical axis was supplied by manual
calibration, both source groups had been visually inspected, and aggregate
geometry for both was observed before the candidate was frozen. The reviewed
label boxes were initially proposed by a similar threshold/component method,
which can favor this comparison. Labels are incomplete and assistant-reviewed,
not independent expert ground truth. Historical and sealed source overlap is
not fully established. This is a small exploratory training result, not a
whole-graph, held-out, automatic-axis, or product acceptance pass.

| Evidence | SHA-256 |
| --- | --- |
| Candidate source | `b4ffb2712c0eac839916f920d22245faec222ffd37aaf9cb85bec6fbae91a823` |
| Candidate tests | `e9cf90440c643306254b51ffe186d860df235cbb085faf5a19694a454cb090f7` |
| Frozen protocol | `d14ce4df3d15509d96798e25e4a0421fd7503f708249b43556ff961693399ca4` |
| Truth-blind proposals | `2068620325689356908821320755a201fcc118f31b335a32e2203e0616459324` |
| Box score | `6bc80aeb948d797a311a75b915a42195bbcf62736947c2b6a66b32e168ce998a` |
| OCR predictions | `08b904a71515a49d3e9d03dba19b3934882d38582dc25e263e9dd1057be72a21` |
| Combined score | `3f85b7fae6ab7b4133a4d355a3d739ada9a5a4d03540a339f8e9ee509dc8e968` |
| OCR diagnostic assembly | `ae3a4c10aff40dc80447027cd0d33074e2a40c85ead43be78e77b53a5df8bef8` |

Two focused component/IoU tests and pyflakes passed. The OCR runner's Release
build and cropper self-test passed, then both readers ran with CPU inference.
The private input and outputs remain under project-local ignored artifacts for
the active automatic-axis follow-up; they must not be committed or packaged.
