<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Isolated marker gaps do not override a supported scale

Calibration previously treated the single smallest adjacent marker gap as
evidence against the entire printed session scale. A duplicate or stray
detection could therefore reject an otherwise consistent scale.

The cross-check now requires a strict majority of compatible gaps at the same
existing tolerance. A coherent conflicting run of three gaps still requires
review, including when it is a minority. Regular half/double-pitch patterns
still trigger harmonic ambiguity. Scale fitting, point assignment, residual
checks and the first-session requirement are unchanged. Off-grid x values stay
unknown and produce a warning that reaches the workflow.

All 51 axis, 370 application and 30 export tests pass without skips. The x64
tool builds with zero warnings/errors and the fictitious private-runner check
passes. The same 23-source synthetic workflow takes 204.82 seconds. All prior
axis, OCR and marker evidence, fitted transforms, anchors and per-point numeric
assignments are unchanged. Valid observed calibrations increase from 16 to 18.
No formerly valid calibration becomes invalid.

All 115 previously exported point locations retain identical values and x
provenance. Full scores are unchanged: nine completed sources, fourteen failed,
85/706 correct unique points/values and 77/724 correct relational rows. The two
improved calibrations expose downstream failures: one export correctly rejects
an unsupported x value; another encounters duplicate filenames across panels.
Neither is called a completed workflow.

Next: keep rejected OCR labels out of downstream calibration and derive
automatic anchor maxima from accepted evidence. Then resolve batch export
filename collisions while retaining existing-file protection. Grouping,
detection and OCR quality still need work; 621 truth points are missing, 30
exported detections are extra and five matched rows have wrong phases.

[Metrics, source bindings and checks](GOAL-22-LATTICE-GAP-INTEGRATION.json)
retain the full unchanged denominator and all results. No training,
private/sealed read, model revision, production activation or package occurred.
Build 433 remains 0.4.33. All four Goal 22 outcomes remain incomplete.
