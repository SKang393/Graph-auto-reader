<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Synthetic calibration observations

The synthetic workflow now retains actual axis geometry, OCR regions, accepted
markers and calibration results before a failed calibration stops processing.
The application supplies an in-memory observation only when an explicit
candidate sink is installed. Only the authenticated synthetic CLI installs the
writer. Approved production and private acceptance do not write these details.
All existing calibration and export safeguards remain enforced.

All 333 application tests pass without skips. The Windows x64 diagnostic tool
build has zero warnings and errors; the fictitious private-runner self-test
also passes. Tests cover successful and failed candidate calibrations and the
absence of observations from normal production.

Actual inference on the same 23 synthetic PNGs finishes in 100.79 seconds and
records 24 calibrations before per-source failure handling stops later panels.
Twelve need review, two lack evidence, six have an invalid origin, and four
are valid. These are the visited panels, not all 39 physical truth panels.
The same one source exports and the same 22 sources fail. Minimal CSV bytes
are identical; audit values differ only in run-dependent identifiers. All
706 truth points remain in offline evaluation.

The observations expose accepted detections inside OCR text and legend labels.
The mask-preserving marker route deliberately treats masks as model inputs,
and downstream classification currently does not enforce text exclusion.
Fix that integration boundary without weakening calibration safeguards.
Other failures include missing/misread ticks, wrong panel geometry and missing
markers; this recording change does not repair them.

The comparison also exposes a scoring defect: equal-weight series matches are
ordered by UUID. Identical exported values therefore score zero versus one
correct relational row when run identities change. Unique value coverage stays
1/706. Both outputs remain failed diagnostics. Repair deterministic geometric
matching before using relational score changes to judge another repair.

The [evidence record](GOAL-22-CALIBRATION-OBSERVATION.json) binds the exact
sources, runtime, reports, retained preparation failure and checks. No training,
private/sealed read, model revision, activation, packaged build or release
occurs. Goal 22 remains incomplete; the retained portable is 0.4.33.
