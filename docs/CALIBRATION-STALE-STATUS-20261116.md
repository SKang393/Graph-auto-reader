<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Ignore calibration status from an abandoned tab

## Defect and correction

At source baseline `14c4a57a3c33bd3f8ef8570b728f95d3a364c1cd`, switching
tabs while the combined axis/label suggestion was awaiting OCR allowed the
old completion to set `Manual.CalibrationLabels.Unavailable` on the newly
selected tab. Tab selection remains available while the read is busy. The
receipt/currentness checks already prevented applying stale calibration, but
their combined failure branch still changed the destination tab's status.

`MainWindowViewModel.CalibrationTicks.cs` now returns immediately if the tab
or axis suggestion changed. A label failure in the current context still
sets the existing localized unavailable message. Receipt validation, refitting,
scientific values and application guards are unchanged.

The owned `StaleLabelReadDoesNotSetCalibrationStatusOnAnotherTab` regression
uses two generated images and a pending OCR stub. It switches tabs before
completing the old read, checks the destination status and project identity,
and verifies that neither tab acquires calibration and Apply stays disabled.
Independent source review found no issue. No private data, model inference,
native desktop interaction or sealed evaluation was used.

## Validation and failures

The normal targeted command, first before and then after the fix, was:

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter "FullyQualifiedName~StaleLabelReadDoesNotSetCalibrationStatusOnAnotherTab" --logger "trx;LogFileName=red.trx" --results-directory artifacts/stale-label-status-check --nologo
```

The second invocation changed only the log filename to `green.trx` and used
the corrected source. VSTest ran on the normal host with the existing local
SDK/package cache and VS2022 build environment.

| Check | Passed / failed / skipped | Wall time |
| --- | --- | ---: |
| Targeted before fix | 0 / 1 / 0 | 30.5041217 s |
| Targeted after fix | 1 / 0 / 0 | 64.2306661 s |
| Full App Release suite | 1,341 / 0 / 2 | 42.0267192 s |

The failing assertion expected empty destination status and received
`Manual.CalibrationLabels.Unavailable`; test time was 494 ms. The repaired
test took 429 ms. Full VSTest duration was 39 s. Full-suite command:

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore --no-build -p:Platform=x64 --logger "trx;LogFileName=full.trx" --results-directory artifacts/stale-label-status-check --nologo
```

Both full-suite skips were the boolean variants of
`ActualWorkerRetainsOnlyObservedAxisThroughPngService`: local
`GRAPHREADER_RUST_WORKER` was unset. They are not counted as passes. Reviewed
OpenCV SHA-256 `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`
was staged in the Release test output only. No rerun was used to hide skips.

Two setup attempts preceded the normal tests:

- A 9.7864003 s `DesignTimeBuild=true` attempt exited before tests with
  `AffineAxisSuggestionTests.cs(23,29): error CS0246: The type or namespace
  name 'RustAffineAxisGeometry' could not be found`. This mode omitted normal
  project references; it is a void setup attempt, not the regression result.
- A temporary `CustomAfterMicrosoftCommonTargets` override was interrupted
  before testing when normal builds were required. Some Release dependencies
  compiled; no test result or reliable duration was retained. The temporary
  WPF project was confirmed absent after the subsequent normal build.

No attempt start/end timestamps were retained. The completed ordinary Rust
baseline had no remaining consumers, so the normal App build intentionally
advanced its generated worker from `08538da44ff7f680f951c7cbe08c806801b74d9c44cff982bf507ae0f995edca`
to current-source `eeb2515a48546c9b50f9c8221f334c028a9dadce34cb1fa32ca7c5d477e87430`.
The source App Debug DLL stayed `b8401a6432d41b7ac7cb9c4437069392d7cc28f9ac138f63cf8ac198b467d410`.

## Evidence identities and closeout

SHA-256 identities before disposable-output retirement:

| Item | SHA-256 |
| --- | --- |
| Changed ViewModel source | `00e36515b7d0d4585e2de828739d1a3055cb5747dcb3a7c24e7ad90c54103bdb` |
| Changed test source | `33db975156957dab101b7359669c2f852de2f51f1fc5a8ffe55c9e0530ceee32` |
| Tested App x64 Release DLL | `36afa90b5ce3b8f109cd0938734d2e893e499ad0c4ec0b718cffef93dabbfde5` |
| Tested App.Tests x64 Release DLL | `ca42b38f46fdccfca13a277fe5962e41e60d1f4567748ad97876a7d0217f3705` |
| Red TRX | `986c2ed07556716c8aa1bab94f608d8cc52acf337ba19eae965784554c622ee8` |
| Green TRX | `ace9e93a82ed6a22905f2ac6d6d52823a9a276b62b3c1329abe3717d180f19a9` |
| Full TRX | `fef176cc0bb6d0de8823cd2439342c6bdf902fea160eb0d9dcdad921e9fa76f2` |

Local regression and App checks pass with the two explicit skips. Source
`57b9f1a915241e97c475dcd6bd76a4d4d7adfb73` is integrated and pushed to
`origin/main`. [Full Windows CI 43873769828](https://github.com/SKang393/Graph-auto-reader/actions/runs/43873769828)
passed on that exact head from **2026-11-16T13:13:46Z** to **13:27:05Z**:
**3,271 .NET passes**, 30 explicit skips, zero failures, including all
**1,343 App tests** with no skips. All 89 Rust tests, 44 CI-scope assertions,
68 packaging checks, 62 version checks, six ledger checks, 12 portable checks
and nine localization checks passed. Scope was `full_changed_path`; release
jobs skipped. Scoped source integration passes. No dependency or license
change was made. The sole delivered build is still 0.4.58; this small source
fix does not justify another package or establish native visual acceptance.

After CI consumers finish, retire
`artifacts/stale-label-status-check` and closed App/App.Tests Release compiler
outputs after ownership and consumer checks. Preserve Debug runtimes and Data,
the current package, owned native-QA inputs, original corpus, independent split,
protected runtime checkpoint and other tasks' work. These commands regenerate
the tests; historical red output additionally requires the baseline source.
