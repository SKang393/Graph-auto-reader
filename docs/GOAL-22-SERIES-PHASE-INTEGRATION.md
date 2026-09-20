<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Series roles follow the final phase evidence

The workflow previously exported provisional series roles even after final
phase reasoning contradicted them. Equal-size baseline fragments were selected
by run-dependent identifiers. Export could also report success with no selected
intervention or with an empty selected intervention.

The runtime now reconciles roles from final point-to-phase assignments, retaining
explicit maintenance/generalization evidence. It shares a baseline only when
there is one actual baseline series. Multiple baselines remain unlinked with a
review warning. Marker classifications, grouping memberships, point measurements
and numeric calibration are unchanged. Empty exports fail before serialization
or file writes.

All 370 application tests and 30 export tests pass, with no skips. The x64 tool
builds with zero warnings/errors and the fictitious private-runner check passes.
The unchanged 23-source synthetic run takes 157.01 seconds. All prior axis,
OCR, marker and calibration observations match. Completed sources increase
from eight to nine; correct unique points/values increase from 69/706 to 85/706
and correct relational rows from 61/724 to 77/724. Artifact integrity passes.

This repair does not solve grouping accuracy. Seven previously exported true
points are omitted after unsupported relationships are removed. The detections
and grouping memberships remain, with explicit source warnings. The two points
lost at the previous tick-label checkpoint are also still absent: classifier
shape errors split that series into different groups. No shapes were overwritten
and no points were joined merely to match the reference. All 91 point locations
common to the two exports retain identical numeric values and x provenance.

Fourteen sources still fail; 621 truth points are missing, 30 detections are
extra, and five matched relational rows have wrong phases. These results are
failed development evidence, not acceptance. Next: repair calibration's use of
one smallest marker gap as a global pitch-conflict test while preserving real
contradiction, harmonic ambiguity and first-session safeguards.

[Exact metrics and evidence hashes](GOAL-22-SERIES-PHASE-INTEGRATION.json)
bind all six changed source/test files and 267 runtime source snapshots. The
earlier compiler failure and disproved recovery assumption remain recorded.
No training, private/sealed read, model revision, production activation or
package occurred. Build 433 remains 0.4.33. All four Goal 22 outcomes remain
incomplete.
