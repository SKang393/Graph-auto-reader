<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native manual-preview workflow check, 2026-11-11

## Scope and exact inputs

The native Windows session exercised the retained internal ManualPreview
`0.4.36`, built from `0ffdfa51f7d4db3e141b1bf8968a46792f572267`.
Its executable SHA-256 was
`bf9e152fbd5a5276849c75e39ef1ebf681617ec1927385a6592b1e6f8f87e14d`.
The [delivery record](INTERNAL-PREVIEW-DELIVERY-20261111.md) binds the package
and remote artifact. No private acceptance image or model was loaded.

The existing project-owned synthetic input was
`artifacts/goal19-realesrgan/public-synthetic/seed-393/images/fce8d985-cb56-55f9-a1f6-0b1cd18f27d0.png`,
SHA-256 `75edb079ccfa8a3a2806bef467ec2dd10d13905bee762fd48ee9be21361be98d`.
The supported `--open-image <absolute-path>` argument imported that image;
the native Open dialog itself was not validated. The process-only
`GRAPHREADER_DEV_PORTABLE_DATA_ROOT` pointed to
`artifacts/manual-preview-0436-qa-20261111/Data`, separate from user Data.

## Observed workflow

Native mouse and keyboard input set Y maximum 10, first session 1 and final
session 24, then placed the three manual calibration anchors. The footer
confirmed calibration for sessions 1 through 24. Adding three points updated
the series count from one to two to three. The fixed inspector magnifier and
original-pixel coordinates were visible. Review showed zero outstanding
issues for the edited panel.

The native Export preview showed three points, one series, one phase, zero
blockers and zero warnings, with original-pixel provenance. This verifies
preview behavior, not a completed native CSV write. Input automation could
not enter a destination into the Windows file dialog: typed text did not
appear, and indexed actions reported that the element was not available in
the cached app state. The dialog was cancelled. Native export, explicit
Save/Save as and reopen therefore remain unverified. The separate
[recovery regression](BATCH-CALIBRATION-RECOVERY-20261101.md) exercises the
actual export and persistence services through the window model.

An autosave snapshot was independently read after these edits, before closing
the application. Its SHA-256
was `d6385d25301440340521fe9fd2c37c26c1003c137997d176d7897476a7159ac6`.
It contained six panels, exactly one populated, one series, one phase and
three manual points. Its confirmed `manual_three_anchor` calibration retained
sessions 1 through 24 and Y maximum 10. The referenced source hash and actual
source-file hash both matched the input above. The other five panels had no
points or calibration. This is an autosave serialization check, not a native
recovery-dialog acceptance result. Manual clicks are not accuracy evidence.

## Reproduced application defects

The dark-theme graph headers used nearly white text on default light Windows
tab chrome. `GraphTabStrip` bypassed the shared tab style, which also lacked a
theme-backed header template. The source fix applies the shared style and
uses existing surface, text, selected, hover and keyboard-focus tokens.
Warning, dirty-state, access-key and close bindings are preserved.
The first native check of that fix exposed clipped headers when a horizontal
scrollbar shared the fixed 42-pixel strip. The strip now uses a minimum height
and grows with its header and scrollbar content.

Closing the main window with the three unsaved points exited successfully
without a confirmation. The existing dirty-tab check guarded only individual
tab closure; `MainWindow` had no cancellable `Closing` guard. The matching
autosave does not make this safe for edits made between autosaves.
The source fix adds a cancellable window-close guard. Clean windows close
normally. Any dirty tab, including a nonselected project, requires an explicit
discard confirmation with No as the default. Busy operations block close with
a localized status message. The guard neither silently saves nor marks changes
as saved.

Native verification on routine compiler output with application DLL SHA-256
`36907f01b710a24b74c22f9f09e1d0a6382d90b5d484653241437902dd9da8e3`
created a disposable series and clicked the main-window close button. The
expected warning appeared. Pressing Enter accepted the default No, dismissed
the warning and kept the edited series and window open. A second close attempt
followed by explicit Yes exited 0. This check predates only the subsequent
tab-strip height correction; the close-guard source did not change afterward.

The compiler output used the existing reviewed OpenCV installer and private
local attestation. Both source-audit and provenance-review validators passed;
the adjacent native DLL matched
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`.
A disposable `portable.mode` marker enabled the supported process-only Data
override to `artifacts/manual-preview-0436-qa-20261111/fixed-Data`. This routine
compile was not a package. The retained 0.4.36 build and user Data were unchanged.

The final routine application DLL had SHA-256
`450fc472718ec6c1a685a414c4f8510426565b6f382ff87479a2d65403c3b326`.
After repeating reviewed-native staging, native inspection showed complete,
legible headers and close buttons above the horizontal scrollbar in the
system-selected dark theme and explicit Light theme. Clicking panel 2 changed
the active graph; the Left key returned to panel 1 with a visible focus border.
The clean window then closed without a prompt and exited 0. No further native
UI check is implied by these specific observations.

## Source validation and limits

The tab-theme regression ran on an STA thread and checked actual template
brushes and inherited header text under Dark, Light and System resources.

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release -m:1 -nr:false -p:RestoreSources=E:\Graph-auto-reader\tools\nuget-packages -p:NuGetAudit=false -p:UseSharedCompilation=false --filter FullyQualifiedName~ThemeAccessibilityTests --verbosity minimal
```

Seven tests passed, with 737 ms test duration and 16.286 seconds wall time.
The E-local SDK/package cache and isolated temporary directory were used.
A test-only CA1861 warning was repaired before the successful run.
`git diff --check` passed. Windows source/packaging CI for the preceding
`12e90117` checkpoint also passed; it predates these native-QA fixes.

The combined close-guard and theme filter initially passed 12/12 tests in
610 ms. Initial sandbox MSBuild execution failed without diagnostics; host
execution exposed CS7036 in the new test's three constructor calls. Supplying
the required `localizationService: null` argument fixed the test compilation.
No production code change was needed for those setup failures. An independent
source review found no concrete issue in window-close wiring, nonselected-tab
handling, safe dialog defaults or cleanup after accepted closure.

After the height correction, the final combined command was:

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~MainWindowCloseTests|FullyQualifiedName~ThemeAccessibilityTests' --verbosity minimal
```

It passed 13/13 in 27.124 seconds wall time (745 ms test duration). The added
layout regression loads the actual strip XAML, styles, template and item
headers, forces 320-pixel horizontal overflow, and verifies that the scrollbar
has separate space below the complete header and close button. It does not
replace the native visual check above. `git diff --check` passed.

No dependency, license, scientific value rule, frozen schema, model approval
or packaged version changes are implied. Automatic recognition, a production
candidate, the installer and clean-Windows acceptance remain open.

## Full App suite and package preflight

The first 0.4.37 builder invocation from `bd662600` stopped before publish in
76.765 seconds: 805 App tests passed and eight failed with
`XamlParseException: Cannot find resource named 'App.Style.TabItem'`.
The affected cases were the runtime theme selector, light/dark workspace
renders, four DPI-scale renders and the empty manual-preview render.
Those standalone test windows loaded resources only after construction;
production `App.xaml` loads them before creating its main window.

An internal constructor now accepts a per-window resource dictionary before
`InitializeComponent`. The two affected test setups use their existing theme
dictionaries through that seam, retaining all assertions and STA isolation.
Public startup constructors retain their behavior. No global Application
singleton or skipped test was introduced.

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false --logger 'trx;LogFileName=app-resource-order.trx' --results-directory artifacts/app-resource-order-tests --verbosity minimal
```

The corrected full suite passed 813/813, with no failures or skips, in 52.863
seconds wall time (27-second test duration). Result SHA-256:
`76a7317e198c21edb6c4230514db46ead02be44b09214488ac5311768a435ec0`.
`git diff --check` passed. The failed builder produced no package, so the
replacement remains version 0.4.37; it does not reuse a packaged build number.

## Disposable evidence

The pre-fix import screenshot SHA-256 was
`b0df19837d5f7d51c30272226e00f9195837e2784341d554897ebbbcbe683e5f`;
the export-preview screenshot SHA-256 was
`d69d006880011624bcca56b729e225b8091d0aa9ccd1a82644db35c8e472893c`.
The final Light-theme screenshot SHA-256 was
`5864ec8cda88789530ef119d42934ea970ce6ef128e4c01a2245108651ef2541`.
These screenshots, isolated QA Data and routine compiler/test outputs are
disposable after the source/outcome record is pushed and the native check
finishes. The original synthetic input, current package and user Data remain
required. This record does not substitute for a current runtime artifact.

After the corrected package build, closeout inventory covered the integration
worktree `artifacts/goal22-worktrees/resume-ocr-gpu-20261111`, isolated native-QA
directory, local verifier's duplicate ZIP staging, and two completed builder
logs: 6,394 files, approximately 623 MB. No directory contained a reparse point;
all task-owned native processes exited. These five targets are retired after
this source/outcome record reaches `origin/main`. Their source is retained in
pushed Git history. No downstream consumer requires the compiler output,
screenshots, synthetic QA autosaves or completed GPU test caches. The 0.4.37
candidate build, original verified ZIP and prepared upload tool remain for
the explicitly pending upload in REV-010. The retained
0.4.36 ZIP was rehashed and still matched
`c751d6d9953dd7f60a0c8480798646a8e228b5dc265966abd619d682034ea2ce`.
