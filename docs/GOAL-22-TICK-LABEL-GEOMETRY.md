<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Numeric labels and visible tick positions

Glyph-box centers are not always tick centers. The development diagnostic
measured offsets up to six pixels for x labels and four for y labels. The
runtime now associates each OCR label with a unique outward tick stroke in
original pixels. Several candidate strokes or competing labels remain
unresolved, with warnings. Unmatched labels retain their previous OCR-center
input. Text, confidence and polygons are unchanged. Rejected OCR is excluded
from calibration. Existing robust-fit and first-session safeguards remain.

All 362 application tests pass. The x64 tool builds with zero warnings/errors
and the fictitious private-runner check passes. The unchanged 23-source run
takes 157.60 seconds. Previously observed axis, OCR and marker outputs are
identical. Valid observed calibrations increase from 12 to 16; the workflow
reaches three additional panels and completes eight sources instead of six.

Under the same independent reference, correct unique points/values increase
from 49/706 to 69/706 and correct relational rows from 46/724 to 61/724. There
are no incorrect numeric values among the 69 matched points. Artifact integrity
passes. Fifteen sources still fail, 637 truth points are missing, 29 exported
detections are extra, and five comparable rows have wrong phase labels.

The run exposes an unresolved series-relation defect. Two previously exported
baseline points disappear while a two-point fragment spanning intervention and
baseline is selected as shared baseline. Grouping uses provisional boundaries,
then breaks equal-size baseline ties by series UUID. Final phases already
identify the mismatch, but export still uses the provisional roles. This is
recorded as an open defect, not a passing complete workflow. Next: reconcile
roles and shared-baseline relations from final phases without regrouping points,
and reject missing or empty intervention exports.

[Exact metrics and source/runtime hashes](GOAL-22-TICK-LABEL-GEOMETRY.json)
retain all results and the earlier test-only compiler failure. No training,
new model revision, private/sealed read, production activation or package
occurred. Build 433 remains 0.4.33. Goal 22 remains incomplete.
