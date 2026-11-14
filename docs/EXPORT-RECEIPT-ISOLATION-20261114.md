<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Export receipt isolation

## Change

An export completion summary now uses the selected tab's counts, provenance,
review issues, and expected filenames captured before the asynchronous export.
The returned filenames are combined with that snapshot. A successful export
remains an immutable receipt for its source tab through later projection and
selection refreshes. Confirm Export requires a pending destination, so a
completed receipt cannot authorize a second export of another selected tab.

Pending previews refresh for the current tab. Warning acknowledgement carries
over only when the refreshed warning issue sequence is identical to the prior
sequence. Opening a new preview explicitly binds it to the current tab.

## Validation

The production App and test build completed in 72.77 seconds with zero warnings and zero
errors. The focused test build completed in 8.90 seconds with zero warnings and
zero errors. The first test compilation found missing model namespace imports
and CA1861 inline-array findings (five errors after 76.38 seconds); these were
corrected before the successful build.

The focused run for `ExportSummaryIsolationTests` and
`CalmPrecisionStateTests` reported 33 passed and one fixture failure. The
failure was the test's null localization service returning the provenance
format key instead of rendered English text. After the fixture used the
existing English localization service, the new test alone passed: 1 passed,
0 failed, 505 ms test time (2.958 seconds command time). Together, 34 distinct
checks passed across the focused run and single-test rerun; the 34-case filter
was not rerun as one final suite.

The new regression delays export of tab A, switches to tab B with different
counts, provenance, and warnings, then verifies A's completed receipt and
actual filenames. It switches tabs again to exercise projection refresh and
checks the receipt remains unchanged, verifies Confirm Export is disabled, and
opens a fresh preview that reflects B. It also acknowledges A's warning in a
pending preview and verifies that B's different warning set remains
unacknowledged.

## Reproduction

Use the existing isolated build output at `artifacts/hardware-final-build`:

```powershell
$out = Join-Path (Get-Location).Path 'artifacts/hardware-final-build/'
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false "-p:OutDir=$out"
dotnet vstest (Join-Path $out 'GraphReader.App.Tests.dll') '--TestCaseFilter:FullyQualifiedName~ExportSummaryIsolationTests|FullyQualifiedName~CalmPrecisionStateTests' '--ResultsDirectory:artifacts/export-summary-check' '--Logger:trx;LogFileName=export-summary.trx'
# After the fixture-only correction, build with BuildProjectReferences=false,
# then rerun only the affected test:
dotnet vstest (Join-Path $out 'GraphReader.App.Tests.dll') '--TestCaseFilter:FullyQualifiedName~ExportSummaryIsolationTests' '--ResultsDirectory:artifacts/export-summary-check' '--Logger:trx;LogFileName=export-summary-recheck.trx'
```

The first TRX SHA-256 was
`d177b3cc0ee9caa595b6d3881f7b702bf46f1687042175d78827a2f98cb84947`;
the corrected regression TRX was
`c54c2c95c1b83afbe6e59c464912a150cbc88caa027a31b9bb8a842a12365531`.
The temporary `artifacts/export-summary-check` outputs may be retired after
this source and outcome reach main and active compiler consumers finish.
After `d7fe8b1046102af7d2bedb636b45d4d464951db7` reached main and its source CI
`43559499098` succeeded, guarded cleanup removed that contained, reparse-free
directory: four files totaling 55,085 path bytes. No compiler or probe remained
active, and a subsequent existence check confirmed the directory absent.
Source, user data, current package and the separate active native QA App were
preserved.

## Limits

No GUI verification or new package was produced. No dependency or export file
format changed. The regression exercises view-model behavior with a real
workspace/export service and a delayed export wrapper; it does not claim native
file-dialog acceptance. The outcome is based on `11771f6e` plus this change.
