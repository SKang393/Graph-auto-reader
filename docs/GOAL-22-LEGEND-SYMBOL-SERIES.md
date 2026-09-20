<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Connect legend symbols to series without creating data points

## Problem

The workflow could match a legend only if the point detector also found its
symbol. A legend symbol could enter calibration before later exclusion from CSV.

## Implementation

The workflow locates a compact symbol beside recognized text inside a visible
legend frame, using original pixels. The existing classifier receives that crop
alongside point crops. Its accepted shape and fill connect the label to a series.
Every symbol remains recorded for audit but is excluded from calibration, data
grouping, point projection and exported rows. An exact detector duplicate reuses
the crop. An offset duplicate cannot supply a competing series label.

Invalid symbol geometry retains completed evidence and returns a structured
failure before classification. Stage identity includes the new workflow version.

## Files changed

- `ProductionAutomaticDetectionAdapter.cs` and new `ProductionLegendSymbolInputs.cs`.
- `FramedLegendRoleResolver.cs` and corresponding workflow/OCR tests.
- This report, [bound evidence](GOAL-22-LEGEND-SYMBOL-SERIES.json), and readiness.

## Tests and commands

Release-mode `dotnet test` passes 35 selected app tests covering the automatic
adapter, candidate workflow, and mask composer. The full OCR suite passes 308
tests with 16 existing optional skips. No test fails. Builds use the local package
cache, disabled package auditing, and isolated compiler output under
`artifacts/goal22-runs/legend-symbol-series-v1/build`.

The workflow cases run real PNG decoding, pixel symbol extraction, deterministic
grouping, legend/phase reasoning, and CSV preview. Axis, OCR, point detection and
classification use controlled adapters. Cases cover missing, exact-duplicate,
offset-duplicate, classifier-rejected, and invalid-geometry symbol inputs.

## Metrics and timing

Each successful workflow case exports two rows for two plotted observations,
with zero legend symbols exported and unchanged source-image bytes. The offset
case retains both symbol detections for audit and uses one for label assignment.
Test-run times, hashes of the five changed sources/tests and four assemblies,
and the verified 14-file prior-source archive are in the bound evidence.

No trained-model accuracy metric is inferred from these controlled cases.

## License review

No dependency, model, native runtime, font or external data is added. New code
uses Apache-2.0. No private or sealed data is read.

## Known limitations

Visible frames and separate symbols are required. Unframed and occluded legends
remain unresolved. Actual classifier accuracy on these symbol crops still needs
evaluation. All five latest OCR development bars remain failed. The earlier OCR
score is bound to its historical source and assemblies, not this changed workflow.

## Integration notes

Prior checkpoint `d9e6c2e` passed Windows CI run `35486779845`. This source-only
repair changes no model approval and creates no model revision, package, tag or
release. Portable build 433, version 0.4.33, is retained. Development continues
while CI verifies the next pushed checkpoint.

## Acceptance status

**FAIL** for Goal 22 completion. The mechanical repair is verified, but the four
required product outcomes and full 2.0.0 distribution remain incomplete.
