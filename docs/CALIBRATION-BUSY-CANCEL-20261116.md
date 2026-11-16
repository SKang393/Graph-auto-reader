<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Visible cancellation for calibration work

## Observed defect and narrow change

ManualPreview geometry searches and label reads call `RunBusyAsync`, which
sets `IsBusy` without changing the Ready/Reviewing surface to Analyzing.
The only visible Cancel button was inside the Analyzing-state panel, so those
operations had a working Escape shortcut and cancel command but no visible
mouse control. This is a source-demonstrated presentation defect, independent
of private graph answers or model accuracy.

`MainWindow.xaml` now exposes the existing `CancelCommand` beside the footer's
busy indicator. Its visibility follows `IsBusy`; its text and accessible name
use the existing `Workflow.Cancel` dynamic resource. Separate footer columns
give status, progress and Cancel their own space. The control collapses when
idle. The original Analyzing-panel control remains available during analysis.
No command behavior, state transition, model, threshold or dependency changes.

## Verification and complete attempt accounting

The new `BusyCancelSurfaceTests.cs` test uses the existing STA host, an unshown
real WPF window and a cooperative in-memory OCR service. It checks the actual
button binding, Visibility, automation identifier and accessible name, starts
a label read, invokes the bound command, and verifies cancellation clears busy
state without publishing a label suggestion or enabling Apply. It disposes the
constructor's default ViewModel and closes the window. Application paths are
null and test Data is isolated; no user window or desktop input is used.

The normal Release build initially failed before tests on **CA1861**, an inline
test resource-name array, after **67.513 seconds**. Moving the array to a static
readonly field fixed that analyzer error. Rebuild passed with **zero warnings
and errors in 11.498 seconds**. There were no failed test invocations.

```text
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release -m:1 -nr:false -p:Platform=x64 -p:UseSharedCompilation=false -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:Platform=x64 -p:UseSharedCompilation=false -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-build --no-restore -p:Platform=x64 --filter "FullyQualifiedName~BusyFooterCancelIsVisibleAndCancelsPendingLabelRead|FullyQualifiedName~CalibrationLabelCancellation|FullyQualifiedName~ShellContractTests" --logger "trx;LogFileName=focused.trx" --results-directory artifacts/busy-cancel-surface-check
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-build --no-restore -p:Platform=x64 --logger "trx;LogFileName=full.trx" --results-directory artifacts/busy-cancel-surface-check
```

Focused results: **12 passed, zero skipped/failed in 6.763 seconds wall**.
The cancellation filter used a source filename rather than its partial-class
method names, so those earlier cancellation cases were covered by the full
suite, not that filter. Full App results: **1,349 passed, zero skipped/failed
in 41.516 seconds wall**, with the current actual Rust worker enabled.
Independent read-only review found no scoped issue. These results establish
WPF binding/visibility and cooperative token behavior, not real screen layout,
keyboard reachability or assistive-technology interaction.

## Identities and closeout

The compiled artifacts embed base commit
`5e4af6ddd86de733c83ceb0530e185aa2f75c364`, with the working source changes bound
below. The concurrent CI scheduling commit changes no application source.
Reviewed OpenCV `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`
was copied only into Release outputs after normal compilation; source Debug
was read, not modified. Existing caches and the MSVC environment were reused.

| Item | SHA-256 |
| --- | --- |
| MainWindow XAML | `98bd1fd1ec7e144b0b48ef15f540a18d8c138a718ca388da4e171c7b9f229b17` |
| New test source | `eabc85337bef7aef07cdb539dcc203efe2cee93fa0ce12590d7c76182b1fae4c` |
| Tested App DLL | `4c71cdedca6cac5498d8939e59205625395247ed81829c2602bf326ee7982ca9` |
| Tested App.Tests DLL | `1e5af6a6c1225c5c65480c40172a3bc17d577485385b4c3c9cc04b474397897d` |
| Rust worker | `eeb2515a48546c9b50f9c8221f334c028a9dadce34cb1fa32ca7c5d477e87430` |
| Focused TRX | `438743764ea3d8f1594c05d4266bb07bd29cfd9f4e116bb1484a71c056cbd268` |
| Full TRX | `0ccd6a879e1c9de31427981b0ed6a20292aa31ff12c4c1db9381172b88bd970a` |

Source App Debug remains
`b8401a6432d41b7ac7cb9c4437069392d7cc28f9ac138f63cf8ac198b467d410`.
Primary unrelated README and marker-record hashes are unchanged. No private
input, optimizer, model inference, new license, package or production approval
is involved. New source retains Apache-2.0 notices. The sole selected package
remains 0.4.59; this source-only control is not yet in that package.

Source integration and full CI remain pending. Once the result/source is pushed
and CI/review consumers finish, retire `artifacts/busy-cancel-surface-check`
(six files / 2,014,169 bytes) and the newly regenerated App/App.Tests x64 Release
compiler outputs. Retain active shared build caches, Debug/Data, current
package/runtime assets, original data and independent evaluation boundaries.
The commands above regenerate this owned test; historical analyzer failure
requires its earlier test-source form.
