<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Measure the marker run before declaring a pitch conflict

The [frozen diagnosis](GOAL-22-SESSION-RUN-PITCH-DIAGNOSIS.json) reproduces a
false scale conflict using exact authored geometry from an owned development
scene. Four outlying single gaps seed 17 matching three-gap windows. Every
window's measured pitch agrees with the printed scale at the existing
12-percent tolerance, yet the old check calls the seed itself a second grid.

The check now compares the measured span of three consecutive gaps with the
printed pitch. Run length, tolerance, strict-majority consensus, harmonic
checks, origin checks, residual checks and point assignments are unchanged.
Workflow cache identity includes the new composition version.

## Evidence

The authored irregular replay changes from NeedsReview to Valid; the regular
control remains Valid. Both preserve every assignment, fitted origin and
pitch, and uncertainty measure. All twelve unsupported x values remain
unknown. Oracle geometry is used only for diagnosis and never enters the
application's input.

The same fixed model configurations run all 31 native and 39 CSV panels each.
Original-pixel OCR, axes, decoded marker proposals, classification, accepted
markers and exclusion membership remain unchanged. The audit records
2 calibration changes limited to false conflict reasons and
derived status/confidence. All fitted transforms, anchor positions and
point assignments are identical; 93 panels rebind exclusion IDs.
OCR-to-marker warnings rebind IDs in 25 panels while retaining
the same physical support. The initial audit's literal-ID comparison failed;
that attempt is preserved, and the repaired audit resolves every warning ID
to its accepted marker properties. No model evaluation was repeated.

| Configuration | Exact OCR | Markers | Correct CSV values | Correct CSV rows |
| --- | --- | --- | --- | --- |
| V44 | 358/453 | 742/838 | 414 -> 414 | 409 |
| V46 | 427/453 | 746/838 | 429 -> 429 | 416 |

Every prior exported audit row is preserved apart from identity rebinding.
All 866 exported rows have finite original-pixel traces and valid
calibration. Wrong-scale, duplicate and failed-source residual rows are zero.
The [complete outcome](GOAL-22-SESSION-RUN-PITCH-REPAIR.json) records status
changes, unsupported assignments, split metrics and full source bindings.

## Verification

`Run-SessionRunPitchChecks.ps1` passes 96 Axis, 562 App and 36 Export tests, with
zero skips and five builds reporting zero warnings/errors. Eight new cases
cover short/long seed gaps, order and scale changes, later contradictory
minorities, and unknown off-grid values. Checks and the oracle replay take
156.218 seconds; four workflow comparisons take
874.732 seconds.

Code retains Apache-2.0 headers. Models, dependencies, license inputs and
thresholds are unchanged. Keep V44/English/V27/V5; V46/server stays closed.
Irregular session mapping and other accuracy/export defects remain.
Goal 22 is incomplete. No private/sealed reads, optimizer, model activation,
packaged build, tag or release occurred. Build 433 stays 0.4.33.
