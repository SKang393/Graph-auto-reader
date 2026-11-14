<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Calibration label selection and local refit

## Behavior and scope

Recognized axis-label rows now have an inclusion checkbox, initially checked.
The original row remains visible when excluded. The separate row text still
selects the row for the existing manual value-fill actions.

**Refit selected labels** uses a filtered copy of the cached, source-bound OCR
response and the original axis geometry. It does not rerun OCR or geometry,
modify raw evidence, select a subset automatically, or apply calibration.
Existing strict per-axis checks, partial results, estimated-position guidance,
and explicit review followed by Apply remain in force.

Changing inclusion immediately clears the previous fit, numeric fields and
review, and restores the read-time anchors. Busy, stale and detached-row edits
are refused; rejected checkbox changes notify the view to redraw. A new read,
tab change or Dismiss resets selections. No persisted schema, model, dependency
or license changed.

## Source validation

The prepared 0.4.47 compiler output was validated before packaging:

| Check | Result | Wall time |
| --- | --- | ---: |
| Initial App build | 0 warnings/errors | 67.9216533 s |
| Final build after checkbox/text separation | 0 warnings/errors | 25.0822653 s |
| Full App suite | 1006 passed, 2 failed / 1008 | 68.0924309 s |
| Isolated screenshot-test retry, fresh process | 2 passed / 2 | 6.7531437 s |
| Probe-only build | 0 warnings/errors | 6.6228471 s |

All seven new refit cases passed. Deterministic fake workspace tests cover
extra-row exclusion becoming complete without another read, unchanged raw
rows, invalid/insufficient selections, immediate review/anchor invalidation,
new-read resets, stale/tab/Dismiss handling and pending-read guards.

The full-suite failures were existing
`ScreenshotSmokeTests.FakeGraphWorkspaceRendersDeterministicLightTheme` and
`FakeGraphWorkspaceRendersDeterministicDarkTheme`. Both threw
`InvalidOperationException: The off-screen WPF screenshot host did not render content.`
at screenshot harness line 86 before capture. Both passed in isolation using
the same App DLL. No test or timeout changed; the exact cause remains
unestablished. The isolated pass does not replace a fresh successful full suite.

SHA-256 identities:

- App DLL: `006fb1190b06b0dde60c3055dc8201985fe02d8468711d9fd3c9a42431a91159`
- Full-suite TRX: `09d80de7e06dd1a7c193e909d3a708984b688514e6556fa44afd293031ee9840`
- Isolated TRX: `5695a00595ad4d7d0641bb3097600f6a60cb73240db7facdb07ea3b2a05deab6`
- Probe DLL: `2b98bf24cd50a7cafb892b3e4ddfc878b08b29a72f4869901b72ee1993b1dd80`

Commands ran from the active phase2 worktree. Existing E-local SDK/cache were
used, with TEMP/TMP under `calibration-refit-check/tmp` and
`VSTEST_CONNECTION_TIMEOUT=30`:

```powershell
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false '-p:OutDir=E:/Graph-auto-reader/artifacts/goal22-worktrees/resume-20261111-phase2/artifacts/hardware-final-build/'
dotnet vstest artifacts/hardware-final-build/GraphReader.App.Tests.dll '/Logger:trx;LogFileName=calibration-refit.trx' '/ResultsDirectory:artifacts/calibration-refit-check/test-results'
dotnet vstest artifacts/hardware-final-build/GraphReader.App.Tests.dll '/TestCaseFilter:FullyQualifiedName~FakeGraphWorkspaceRendersDeterministicLightTheme|FullyQualifiedName~FakeGraphWorkspaceRendersDeterministicDarkTheme' '/Logger:trx;LogFileName=isolated-render-host.trx' '/ResultsDirectory:artifacts/calibration-refit-check/test-results'
```

The probe-only build used the same build flags/output directory with
`-p:BuildProjectReferences=false`.

## Actual owned workflow

The actual prepared-source tickless cropped-panel workflow kept all labels
included. It completed in 4.8887542 s process time / 4327.4522 ms pipeline time,
with three CSV rows and three reopened points. Explicit simulated review and
Apply produced `UserConfirmed`, `manual_three_anchor`, three anchors and zero
tick `EvidenceRegionId` values. Stderr was empty.

This confirms the default-all scientific edit/save/export path. Human
exclusion/refitting is covered by the fake workspace tests above, not by this
actual worker run. Neither check is native GUI verification, private-data
acceptance, an OCR accuracy claim or production approval.

- Owned image SHA-256: `e70e983cf790759fbe59163af77af69c8ffc010689e66b99d742fc6f5203aebc`
- Workflow report: `1154cda1d2f4b8ddcc0d4527d48c99130ee32e5da6977b996f4096b3b0cdb1e7`
- CSV: `249b49a16b036e5be69191a4aea4a867e75d4cdbe0ac6fd31fa2568a23b3edee`

Actual worker command, launched hidden with a 60-second owned watchdog
(`$active` is the phase2 worktree):

```powershell
& "$active/artifacts/hardware-final-build/GraphReader.CalibrationOcrProbe.exe" --marker-workflow-check "$active/artifacts/calibration-refit-check/request.json" "$active/artifacts/hardware-final-build/GraphReader.App.exe" 'E:/Graph-auto-reader/artifacts/dev-portable/builds/0.4.46-20261114T083718338Z-42b0d9e2/review-assistance/ocr' "$active/artifacts/native-engine/release/graphreader-engine.exe" "$active/artifacts/calibration-refit-check/source-workflow" prepared-crop
```

## Completion and retention

Delivery acceptance remains pending the pushed source/outcome, a fresh full
hosted CI suite and verified 0.4.47 packaging. No package was produced by these
routine compiler checks.

Temporary `artifacts/calibration-refit-check` contains metadata, TRXs, owned
image and workflow outputs, approximately 1.5 MB. Retain it for the 0.4.47
packaged consumer, then retire it after that consumer finishes and this record
is pushed. Keep active packaged model/runtime assets; do not retain scratch
outputs as a substitute for this record.
