<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve selected geometry when suggesting calibration

## Failure and scope

The [native representation repair](NATIVE-REVIEW-INTEGER-REPRESENTATION-20261115.md)
made review choices available on six of the fixed twelve development panels.
The current [full automatic trace](ENDPOINT-LABEL-LANE-20261115.md) still has
geometry and numeric-label limitations. Source inspection found a separate
general command-routing defect in that recovery workflow: after explicitly
choosing axes or marking a manual draft, Suggest calibration unconditionally
called Suggest axes again. That clears the choice before ordinary detection,
which can remain ambiguous and stop before OCR. The existing Read labels then
Refit route worked, but the combined command discarded the user's work.

An owned failing-before control reproduced ordinary ambiguity, Find/Use of a
valid measured choice, then the combined command. The ordinary-axis call count
increased from one to two instead of remaining one. This was one expected test
failure, not an accuracy or model experiment. The metric is preservation of
current source-bound geometry with zero redundant axis calls, one OCR read,
retained X/Y ticks and explicit review required before Apply. Sealed budget and
private reads are zero; existing development/independent assignments and frozen
unchanged451/Mirage comparisons are unchanged.

## Implementation

`MainWindowViewModel.CalibrationTicks.cs` now uses the existing
`CanReadCalibrationLabelsCore` freshness predicate to choose the route. Valid
current geometry uses the existing computed-value clear, read and refit helpers;
ordinary detection runs when there is no usable draft. The existing command is
also enabled for a readable manual draft without an automatic-axis adapter.
No new button, service, cache, model, fit tolerance or search budget was added.

An independent review found that a token cancelled before entry could clear
entered numeric values before observing cancellation. The final code checks the
token before any mutation. Cancellation during reading retains the axes while
clearing unfinished numeric/review state. Existing stale-image/tab/result guards
and explicit Apply review remain in force.

## Owned verification

Four added controls cover selected measured geometry after ordinary ambiguity,
manual draft with reader but no automatic adapter, cancellation during reading,
and cancellation before entry. The first verifies unchanged ordinary-axis call
count, exactly one OCR call, exact plot and both measured tick arrays, six paired
rows, the persistent geometry warning, no automatic calibration and disabled
Apply. The pre-cancel case preserves entered values/review and calls neither
worker. Existing no-draft, stale-source, tab and review tests ran in the same batch.

```powershell
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet vstest tests/GraphReader.App.Tests/bin/Debug/net10.0-windows/win-x64/GraphReader.App.Tests.dll '/TestCaseFilter:FullyQualifiedName~CalibrationTickViewModelTests|FullyQualifiedName~AxisCandidateReviewTests|FullyQualifiedName~AxisSuggestionTests|FullyQualifiedName~ManualAxisDraftTests' '/Logger:trx;LogFileName=focused.trx' '/ResultsDirectory:artifacts/selected-geometry-calibration-check'
```

Normal-host execution used the existing checkout-local .NET/NuGet/temp paths
and a 30-second VSTest connection timeout. The reviewed OpenCV native library
was restored and verified after compilation in the App and test roots.

| Check | Outcome | Time |
| --- | --- | --- |
| Red build | 0 warnings/errors | 13.0918192 s |
| Selected-choice red | 1 expected failure, 0 passes/skips | 475 ms test / 6.6634216 s wall |
| Intermediate build | 0 warnings/errors; no tests before pre-cancel correction | 78.2875803 s |
| Final build | 0 warnings/errors | 35.5617412 s |
| Final focused batch | 93 passed, 0 failed/skipped | 4 s test / 7.5165469 s wall |

Red TRX SHA-256 is `9702482da72a3817809f1d965786069335d1040bb76a95c1c96d708d2b357a1c`;
final TRX is `a04a6bcbf2542093df8fd974fb63429c400c27e8095eb20d9b329047003a85a9`.
The pre-version-preparation App DLL is
`5595738f8c8452f44abc466c93db141deb96ab5c52f56a4ce5fcb6588c7fb343` and EXE
`e73db3f2b8bfecc9b7213ca244207cfe066ca453eacb57064a6e9f9fc39c8199`.
These are compiler outputs, not a new packaged build identity or inference timing.

## Integration, delivery and retention

The local source checks pass. Full exact-source Windows CI passed 2,995 .NET
tests with 30 skips and zero failures, plus 63 Rust tests. Packaged 0.4.54 native
verification retained the selected anchors and warning through six measured
tick pairs, explicit review, Apply and Undo; see the
[delivery record](INTERNAL-PREVIEW-454-VERIFICATION-20261115.md).
`Prepare-CheckpointVersion.ps1 -PrepareNext`
prepared 0.4.54 once for one integrated internal preview containing the native
representation repair, endpoint label-lane repair and this routing correction.
The active pointer remains 0.4.53 until the verified replacement's ledger push
and activation finish. No public release or model approval
is part of this batch. Dependencies and licenses are unchanged; source remains
Apache-2.0.

No real-graph accuracy gain is claimed. The owned control supplies geometry and
literal OCR rows to exercise the application route; actual packaged pixels/OCR
are a separate check. After this source/outcome reached origin/main in
`ee2fd787`, the seven completed files / 149,621 bytes in
`artifacts/selected-geometry-calibration-check` were retired at
`2026-11-15T16:08:06.9148535Z`. Their integration consumer had finished; exact path,
inventory, reparse and live-process guards passed. Current runtime dependencies,
original inputs and independent data were preserved.
