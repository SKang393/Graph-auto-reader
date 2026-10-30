<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Manual edit history localization outcome

The Windows application terminated after the third manual calibration anchor
because `Edit.Calibrate` was absent from the active English resource dictionary.
The same missing history labels affected point, series, and phase edits. All 13
history descriptions now use registered keys with English translations. The
localization regression checks the registered labels and rejects literal
`Edit.*` history arguments.

Validation on October 30, 2026:

- `dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore`: 694 passed, zero failed.
- The rebuilt manual-preview application imported a synthetic six-panel image,
  completed three-anchor calibration, created a series, added one point, and
  exported a CSV and two audit files under `artifacts/native-manual-smoke-20261030`.
  The calibration values were supplied for the smoke test; they are not an
  accuracy measurement or scientific interpretation of the graph.
- Automatic analysis was unavailable in this manual-preview build. No model
  approval, automatic-recognition acceptance, installer, or portable release
  was produced by this check.

The original unhandled exception also triggered Windows Error Reporting. Its
system-managed crash report was written outside the project by Windows.
