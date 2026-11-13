<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Calibration label assistance, 2026-11-13

ManualPreview can request review-only OCR suggestions for the current axis
proposal. The client sends the original panel image in memory to a separate
local worker. The worker uses the pinned English recognizer on CPU and returns
candidate text, roles, numeric-literal checks, sequence warnings, and original
panel-pixel geometry. Every row requires review. A user explicitly selects a
valid numeric row for a calibration field and must still review and confirm the
calibration; OCR does not silently apply values or enable production analysis.
Source, panel, crop, transform, calibration, and corrected-axis bindings are
checked before results are used. Changing those inputs invalidates stale work.

## Evidence

The App test run recorded 831 passed and four UI fixture failures out of 835.
The four failures concerned fixture initialization and expected labels or
localization, not an established product UI outcome. After fixture correction,
the focused follow-up passed 6/6; the full suite was not rerun after that fix.
The probe also encountered a local CS0136 variable-shadowing compile error;
the lead corrected it before the successful lifecycle run.

The real self-hosted client lifecycle check used one owned synthetic panel,
request SHA-256 `96cefefce0b633ddfa505b34cc353f23ca15a05b3802e620bd524e93c011cd7d`
and source SHA-256 `75edb079ccfa8a3a2806bef467ec2dd10d13905bee762fd48ee9be21361be98d`.
The panel crop was encoded in memory. The probe exited 0 in 10.0467567 seconds;
worker timings were 3.2212639 seconds cold, 1.5929863 seconds warm, and
2.8889885 seconds after restart. Cold and warm requests used the same process
and one initialized session. The cancelled child exited, the next request
started a new cold child, and disposal ended that child. Each successful read
reported nine review-only candidates. This is lifecycle evidence for one source,
not an accuracy measurement.

The worker reported a verified Windows Job Object rate of 416/10000 across 12
active logical processors, equivalent to an aggregate 0.4992 logical core,
with BelowNormal priority and one CPU inference thread. This is an aggregate
scheduling budget, not a promise that an individual core never reaches 100%.
The lifecycle result SHA-256 is
`d764ba8b5a9b9bb3fb687bd09131fedaf8e946c50587297c4ed5a1b007bad828`;
the active local evidence is under `artifacts/calibration-label-tests/`.

## Reviewed assets and remaining work

The recognizer SHA-256 is
`7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`,
and its tracked manifest SHA-256 is
`17259b3260c0d39b292be70b36e68d53a067992de3afeaf0809d4d981de4327a`.
The PaddlePaddle PP-OCRv5 model Apache-2.0 license and notice are pinned at
`c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4` and
`8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729`.
The hosted materializer requires the exact reviewed model hash, but its hosted
download and conversion path has not yet been run. No private or sealed input
was read, no model was trained, and no production approval was granted.

## Native review and save

The lead exercised the WPF flow with App DLL SHA-256
`87fba9650bfac6d03566aefc5a19456ccde536b092f67f7b515e5b2ce81594d0`.
The label list showed nine rows while all three value fields were blank and
Apply was disabled. The lead explicitly selected `10` for Y maximum, `1` for
first session, and `24` for final session. Apply remained disabled until review
confirmation. Reading labels again retained the selected values but cleared
review and disabled Apply. After confirmation and Apply, the status reported
`Manual calibration saved for sessions 1 through 24.`

The saved QA project is 18,307 bytes, SHA-256
`6ed4bb652b395cbebd7591206a8e738b50b0d58c90250aec3e1ca0d241e1fccc`.
Its JSON records manual three-anchor, valid, user-confirmed calibration:
`(144,260) -> (1,0)`, `(144,96) -> (1,10)`, and
`(1055,260) -> (24,0)`. Printed minimum and maximum are null. The App exited 0
and no App children remained after close. A fresh launch of the corrected App
DLL (`58b4ec0d468ccffe4b99a4bfe26b047b66eff478259be32ed38026ce73bc454c`)
opened that project from Recent projects and restored `10`, `1`, and `24`.
The saved project hash remained unchanged.

Native review also exposed unreadable OCR rows in dark theme. The targeted XAML
fix uses existing theme resources and accessible `DisplayText`; the App then
built with zero warnings and errors in 22.30 seconds. The first 16-test focused
run aborted before any test executed because VSTest could not connect to its
test host within 90 seconds in the restricted runner. The same built bits
passed 16/16 with `--no-build` outside that restriction: 557 ms test time,
3.211 seconds wall time. Packaging helper fixtures passed 5/5 and the reviewed
runtime importer fixtures passed 7/7. Native screenshots showed readable
unselected and selected rows in System-dark and Light. UI Automation exposed
the row's display text as its accessible name. Selecting the nonnumeric
participant row left all three Use actions disabled. Changing to panel 2 and
back cleared the proposal and OCR rows while retaining the saved calibration.
The corrected App exited 0 with no remaining App processes. The local QA and
test outputs remain active until this outcome is pushed, then are retired.

The final App build and focused retry used the existing E-local .NET and NuGet
environment:

```powershell
dotnet build src/GraphReader.App/GraphReader.App.csproj -c Release --no-restore -m:1 -nr:false -p:BuildProjectReferences=false -p:NuGetAudit=false -p:UseSharedCompilation=false --verbosity minimal
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~ShellContractTests|FullyQualifiedName~CalibrationLabelViewModelTests' --logger 'trx;LogFileName=calibration-label-theme-recovery.trx' --results-directory artifacts/calibration-label-tests --verbosity minimal
```

This record does not establish real-data accuracy or release readiness.
