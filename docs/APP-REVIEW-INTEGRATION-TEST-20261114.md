<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# App review integration test, 2026-11-14

The combined source changes for native review corrections and human-corrected
tick evidence passed the full `GraphReader.App.Tests` suite: **1,047 passed,
0 failed, 0 skipped**. Test execution took 45 seconds; command wall time,
including the isolated Release build, was 120.870 seconds. The checkout HEAD
was `e80b5486012338663273e6d2bd4700030b448818`, with the review changes still
uncommitted. The tested App DLL SHA-256 was
`f46a5d10e5765a9e3060e126c1b028baf5712e793d33d62e318d6d7462d7269e`.
Compilation started before the version preparation and outcome commits: the
tested assembly reports FileVersion `0.4.50.0` and ProductVersion
`0.4.50+99864561c4f4e18e6e39b34e258a0ed7434ed623`. The source fixes were present;
the subsequent version-only change prepares 0.4.51 for hosted packaging.

```powershell
$taskOutput = Join-Path (Get-Location) 'artifacts/app-integrated-20261114'
$env:DOTNET_CLI_HOME = 'E:\Graph-auto-reader\artifacts\dotnet-e-drive'
$env:NUGET_PACKAGES = 'E:\Graph-auto-reader\tools\nuget-packages'
$env:TEMP = Join-Path $taskOutput 'temp'
$env:TMP = $env:TEMP
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false "-p:OutDir=$taskOutput/bin/" --logger 'trx;LogFileName=app-integration.trx' --results-directory (Join-Path $taskOutput 'results') --verbosity minimal
```

The command ran with normal host access and task-local output and temporary
directories. Its TRX SHA-256 was
`392ef0442139c84900fc44d31c005c59623437e9080835eab114b7cb9f38828e`.
The test artifacts are disposable after this record is pushed and their active
consumers finish. This is source test evidence, not native UI verification,
packaged-build verification, model approval, or production release approval.

## CI test synchronization correction

Windows CI run `43653973013`, job `132628327191`, at source `15619ac7`
reported 1,046 App tests passed and one failed:
`BackgroundProfileChangesRefreshTheViewModelOnItsStaContext`. Its status
notification could arrive after settings became current but before the queued
selected-choice refresh. The test now waits for the selected-choice notification
with the expected GPU label; the WPF ComboBox identity and label assertions remain.
This change fixes the test's completion condition and does not establish a
separate production race.

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter FullyQualifiedName~CalibrationDeviceViewModelTests --verbosity minimal
# 4 passed, 0 failed; test duration 514 ms.
```

The focused command ran with normal host access and E-local .NET, NuGet and
temporary paths. During concurrent local compilation MSBuild issued `MSB3026`
for `GraphReader.Ocr.dll`; its first retry succeeded. The full suite has not
been rerun after the synchronization correction.

The final 451 [Windows CI run](https://github.com/SKang393/Graph-auto-reader/actions/runs/43655058286)
passed all 1,049 App tests, including the correction and two later corner-label
cases. The earlier local isolated outputs were [retired after their records were pushed](COMPLETED-DIAGNOSTIC-OUTPUTS-RETIREMENT-20261114.md).
