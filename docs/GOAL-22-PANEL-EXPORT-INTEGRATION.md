<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Multi-panel exports receive distinct filenames

Each panel previously reserved filenames independently inside the same output
directory. Identical participant and series names could therefore collide in
an otherwise empty destination. Preview also returned duplicate names.

Multi-panel workflow exports now use readable panel prefixes, assigned in a
stable order. Single-panel names, participant information, serialized scientific
rows and existing-file protection are preserved. Prefixes use the existing
filename sanitization and length limits. No frozen schema changed.

All 373 application and 32 export tests pass without skips. Coverage includes
multi-panel preview/write byte parity, reversed input ordering, unchanged
single-panel names, duplicate and long names, unsafe filename characters and
preservation of an existing destination. The x64 tool builds with zero
warnings/errors; the fictitious private-runner check passes.

The same 23-source synthetic run takes 159.16 seconds. Raw axis/OCR/marker
evidence and all calibration measurements are unchanged. The previously blocked
six-panel source exports successfully. Completed sources increase from nine
to ten, correct unique values from 85/706 to 218/706 and correct relational
rows from 77/724 to 210/724. All 115 previous exported locations retain their
values and x provenance. There are 135 newly exported points, of which 133
match truth; the two additional unmatched predictions remain counted.

Thirteen sources still fail. There are 488 missing truth points, 32 extra
predictions and five matched rows with wrong phases. Unknown point values still
block export. Diagnosis found a legend glyph and a decorative glyph among those
unsupported detections; neither was removed or assigned invented values.
Batch export is not a cross-panel transaction, and this repair does not change
failure cleanup behavior.

[Metrics and source bindings](GOAL-22-PANEL-EXPORT-INTEGRATION.json) preserve
the full denominator and every failure. This is train/development evidence,
not acceptance. No training, private/sealed read, model revision, activation or
package occurred. Build 433 remains 0.4.33. All four Goal 22 outcomes remain
incomplete. Next: trace remaining OCR, marker, calibration and phase failures
from the saved development evidence while preserving review safeguards.
