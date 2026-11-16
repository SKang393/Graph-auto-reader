<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Cancel an abandoned calibration label read

## Observed defect and implementation

On baseline `57b9f1a915241e97c475dcd6bd76a4d4d7adfb73`, switching tabs
discarded an old label result but did not cancel its OCR request. The global
busy state consequently remained until the abandoned request finished.
An owned pending-reader regression reproduced the missing cancellation before
the source change; caller cancellation unwound that red test without hanging.

The label read now owns a linked cancellation source. Clearing its context
advances the generation before cancelling that source. The read disposes it
and clears the field only if it still owns the field. Cancellation of an
abandoned generation finishes quietly; explicit caller, Cancel command and
shutdown cancellation retain their existing parent-token behavior. No generic
tab-selection cancellation was added to import, export or other operations.

This connects context changes to the existing OCR client's cancellable path:
`CalibrationOcrWorkerClient` registers cancellation with `StopWorker`, cancels
pipe operations and releases its request gate. No worker protocol, model,
resource cap, numeric association, receipt check or review gate changed.

## Owned validation

Normal-host Release used the existing local SDK/package caches and VS2022 x64
compiler environment. All inputs were generated test images and stubs; no
private/independent split, OCR model inference or native desktop input was used.
The full suite additionally ran its existing actual Rust-worker tests.

| Check | Passed / failed / skipped | Test time | Wall time |
| --- | --- | --- | ---: |
| New tab-switch regression before fix | 0 / 1 / 0 | 404 ms | 62.2786287 s |
| Focused checks after fix | 10 / 0 / 0 | 524 ms | 2.6892092 s |
| Full App suite after fix | 1,348 / 0 / 0 | 38 s | 40.7249396 s |

The red assertion was `Changing tabs must cancel the abandoned label request`.
The normal green build took 29.7442806 s, with zero warnings/errors. No other
failed, cancelled or void build/test attempts occurred in this batch.

The five new cases cover tab-switch cancellation and busy-state release,
same-tab preservation, explicit caller/command cancellation and an unrelated
pending axis operation. Existing stale-binding, cancelled-read and
noncooperative-result checks also pass. Root and independent read-only review
cleared cancellation ordering, disposal, parent-token propagation and unchanged
publication guards without finding a concrete regression.

```powershell
# Red before changing production source:
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:Platform=x64 -p:UseSharedCompilation=false -p:NuGetAudit=false --filter "FullyQualifiedName~SwitchingTabsCancelsPendingLabelReadAndReleasesBusyState" --logger "trx;LogFileName=red.trx" --results-directory artifacts/calibration-label-cancel-check --nologo
# Build the corrected source normally, including its Rust dependency:
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:Platform=x64 -p:UseSharedCompilation=false -p:NuGetAudit=false --nologo
# Focused green:
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore --no-build -p:Platform=x64 --filter "FullyQualifiedName~SwitchingTabsCancelsPendingLabelReadAndReleasesBusyState|FullyQualifiedName~SelectingSameTabDoesNotCancelLabelRead|FullyQualifiedName~ExplicitCancellationStillCancelsLabelRead|FullyQualifiedName~SwitchingTabsDoesNotCancelUnrelatedAxisWork|FullyQualifiedName~StaleBindingDuringTickReadCannotPublishOrApplyProposal|FullyQualifiedName~CancelledTickReadCannotPublishOrApplyProposal|FullyQualifiedName~StaleLabelReadDoesNotSetCalibrationStatusOnAnotherTab" --logger "trx;LogFileName=green.trx" --results-directory artifacts/calibration-label-cancel-check --nologo
# Full green:
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore --no-build -p:Platform=x64 --logger "trx;LogFileName=full.trx" --results-directory artifacts/calibration-label-cancel-check --nologo
```

`GRAPHREADER_RUST_WORKER` named the current local release worker by absolute
path. Reviewed OpenCV was staged only into the Release test output. Both
actual-worker partial-tick controls ran. `git diff --check` passed; Git warned
only about normalizing CRLF. No invocation timestamps were retained.

## Identities, limits and retention

| Item | SHA-256 |
| --- | --- |
| CalibrationLabels ViewModel source | `6cb0308d695e61df59a028eb84662cd7cc8734ccffe9852b061a84f1b471e143` |
| New test source | `be8a4a4b1a6ccbd5dd4105651c66e050d92f7d65bb84f5f85abd3e86dc4379a0` |
| Tested App x64 Release DLL | `11a9e247e97dbf9eeb5c630ebf7d2631e699a8a76a17cfe1d2e79899467896a6` |
| Tested App.Tests x64 Release DLL | `ba3ef6128fb6321f58c0477cabaea7d569faa137c005cfbc3b66b911e2e94ac6` |
| Rust release worker | `eeb2515a48546c9b50f9c8221f334c028a9dadce34cb1fa32ca7c5d477e87430` |
| Reviewed OpenCV | `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31` |
| Red TRX | `4d921ecfed0890a81cc32975fb45eca565c3ff64bd26d492e8d048ff5b579f54` |
| Focused green TRX | `72cb8b5ff3f54220bc9594fc4acb0fa1c2326911b00fc14565031a6c876a0d21` |
| Full green TRX | `71c196660e78ce491d84f2cd59164429a469faf0f5439a2e1461c728f585e9db` |

The source App Debug DLL stayed
`b8401a6432d41b7ac7cb9c4437069392d7cc28f9ac138f63cf8ac198b467d410`.
There is no new dependency, license change or package. The sole delivered
preview remains 0.4.58. This proves token propagation and prompt completion
with a cooperative owned provider, not measured live OCR cancellation latency
or native UI acceptance. A provider that ignores cancellation can still delay
completion; existing guards prevent its stale result from being published.

Source `23910496a6ad158bde89a0c17900fcba059df14d` is integrated and pushed.
[Full Windows CI 43876309348](https://github.com/SKang393/Graph-auto-reader/actions/runs/43876309348)
passed on that exact head from **2026-11-16T13:29:52Z** to **13:41:18Z**:
**3,276 .NET passes**, 30 explicit skips, zero failures, including all
**1,348 App tests** without skips. All 89 Rust tests, 44 scope assertions,
68 packaging checks, 62 version checks, six ledger checks, 12 portable checks
and nine localization checks passed. Scope was `full_changed_path`; release
jobs skipped. Scoped source integration passes.

The subsequent single owned content-span integration control and its full CI
completed. After its outcome reached `origin/main`, this run's eight raw files
(2,019,307 bytes) and closed App/App.Tests Release compiler outputs were retired
with the preceding stale-status outputs at **2026-11-16T14:32:17.5510723Z**.
The [combined retention record](OWNED-CONTENT-SPAN-CALIBRATION-20261116.md#closeout)
accounts for all scopes and 509 unchanged protected hashes. Debug runtimes,
user Data, current models/package, original inputs, independent evaluation and
other tasks' work remain intact. The commands above regenerate these tests;
reproducing the historical red additionally needs the baseline.
