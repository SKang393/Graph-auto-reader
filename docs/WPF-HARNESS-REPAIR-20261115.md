<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Bound WPF test rendering and release screenshot owners

## Trigger and diagnosed defects

Internal-preview workflow [43712247817](https://github.com/SKang393/Graph-auto-reader/actions/runs/43712247817),
job 132806152783, failed on source
`20edf2f41f13a451e7d1e4bcc9c46aff31aa2885` before publishing or creating a package.
Its full App suite passed 1,146 tests and failed two, with no skips:

- `AccessibleShellThemeSelectorAppliesRuntimeOverride`: the existing 20-second
  STA deadline expired at `ResourcesAndLocalizationTests.cs:97` / `StaTestHost.cs:59`.
- `FakeGraphWorkspaceRendersDeterministicLightTheme`: the off-screen host did not
  render content, at `ScreenshotSmokeTests.cs:154` / `WpfScreenshotHarness.cs:86`.

The same source's preceding complete Windows CI passed all 1,148 App tests.
That difference indicates a host-sensitive failure; it does not prove the cause.
Source review found screenshot fixtures detaching MainWindow content without
closing the owner, replacing its constructor-created view model without disposal,
and creating the ManualPreview view model outside the render STA. The STA helper
also disposed its completion event on timeout while its background thread could
still call Set later. Those are concrete harness defects independently of the
two hosted failures.

## Repair

- Use a task completion source for the STA result and preserve the existing
  20-second deadline and original exception propagation.
- Select software rendering in this test process only. Microsoft's
  [ProcessRenderMode documentation](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.renderoptions.processrendermode?view=windowsdesktop-9.0)
  describes this process preference for software rendering. No production
  rendering, CPU/GPU selection or resource policy changes.
- Capture and close screenshot owners on their STA, dispose replaced/current
  view models, and construct the ManualPreview view model on that STA.
- Run screenshot cleanup even if its factory or host setup fails. Close the
  theme-selector window in finally.

No test is disabled. Pixel assertions, the render timeout and STA timeout remain
unchanged. This is test lifetime/render configuration work, not a UI behavior fix
or proof that GPU rendering caused the hosted failure.

## Validation and failed-attempt accounting

```powershell
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-build --no-restore --filter 'FullyQualifiedName~ScreenshotSmokeTests|FullyQualifiedName~ResourcesAndLocalizationTests|FullyQualifiedName~AxisCandidateReviewTests|FullyQualifiedName~CalibrationLocalizationTests' --logger 'trx;LogFileName=focused-normal-host.trx' --results-directory artifacts/wpf-harness-repair-check/focused-normal-host
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-build --no-restore --logger 'trx;LogFileName=full-app.trx' --results-directory artifacts/wpf-harness-repair-check/full-app
```

Build: zero warnings/errors, 14.10 seconds. The reviewed OpenCV native DLL
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`
was staged into App and test output after compilation.

The first focused invocation mistakenly used the sandbox, with the default
90-second VSTest connection wait. It aborted before discovery/execution: zero
tests, no assertion result. Its testhost process had exited before the retry.
The corrected normal-host invocation set `VSTEST_CONNECTION_TIMEOUT=30`:

| Run | Passed / failed / skipped | Test duration | TRX SHA-256 |
| --- | --- | --- | --- |
| Sandbox launch | 0 / 0 / 0, aborted | Connection failed at 90 s | `3cfd2736c2ed43040f3a0b672cc076d8dfe60a550b156034f278877144837f8c` |
| Normal-host focused | 36 / 0 / 0 | 13 s | `096340ec112b1a29b4cae8a078852f527848707064fd7d6fc5c7f7366303ed3c` |
| Normal-host full App | 1,148 / 0 / 0 | 49 s | `1a68b070c5dd47b9f6e8d6445ead0417cd250f613927fb4e50849ffe0694ff58` |

Both originally failing tests passed in the focused run. The full run also
passed `ManualCalibrationExposesModeLocalizedXInputsAndValidation` and both
session/printed-X cases of
`CalibrationAccessibleTextRefreshesOnLanguageChangeWithoutResettingReview`.
These cover accessible Name, help, validation and language-change notification
from the earlier contract repair. The focused filter's final term matched no
tests because the localization file contributes to `CalibrationTickViewModelTests`;
the cited localization evidence is from the full run, whose individual passing
results were verified. `git diff --check` passed.

## Integration and retention

No dependency, production model, frozen contract or license changed; source is
Apache-2.0. Exact-head [Windows CI 43714851749](https://github.com/SKang393/Graph-auto-reader/actions/runs/43714851749)
completed successfully at `2026-11-15T12:48:51Z` on
`e1da376b497757add3329d0aabd14c3121edf9a4`: 13 .NET suites, 2,937 passed,
30 skipped, zero failed, including all 1,148 App and 189 Axis tests. Rust passed
55/55; version policy 62/62, ledger 6/6, portable retention 12/12 and all remaining
packaging/evidence checks succeeded. Public-release jobs were skipped. This
verifies that source revision; later Rust review integration and an actual
replacement package require separate verification.
The failed attempt created no package: 0.4.52 stays the
sole retained build, and 0.4.53 remains the next actual package version.
After this source/outcome reached main in `9e694ca5` and its consumers finished,
the three TRXs totaling 1,731,165 bytes in `artifacts/wpf-harness-repair-check`
were retired at `2026-11-15T13:33:54Z`. This document is the retained record.
