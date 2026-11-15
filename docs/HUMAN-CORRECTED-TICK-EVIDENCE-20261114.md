<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Human-corrected tick evidence with zero OCR confidence

An owned regression exposed a recovery defect after
[per-row label correction](TICK-LABEL-CORRECTION-20261114.md): a failed or empty
OCR reading could have confidence zero. Entering its correct numeric value
produced a valid local correction, but fitting still rejected the original zero
confidence. With exactly three labels on that axis, the corrected row could not
complete a review draft. This source-level defect was not established as a cause
of any private aggregate failure.

## Implementation and ownership

Only these App files and existing tests changed:

- New `Services/CalibrationTickFitEvidence.cs`: transient raw-row reference and
  optional human-entered literal, outside the worker and saved schemas.
- `Services/CalibrationTickProposal.cs`: internal review-draft overload verifies
  exact cached-row association and keeps human validation support separate from
  the original OCR confidence.
- `ViewModels/MainWindowViewModel.CalibrationLabels.cs` and
  `MainWindowViewModel.CalibrationTicks.cs`: pass that evidence through cached
  refitting without another OCR call.
- `CalibrationTickProposalTests.cs` and `CalibrationTickViewModelTests.cs`.

An explicit valid human correction supplies one unit of support to the existing
validators. That weight is never exposed as OCR confidence or copied into raw
receipts. Original text, confidence zero and failure remain unchanged. Public
uncorrected zero-confidence paths remain rejected. Detached rows and invalid
corrections fail; geometry bounds, association, monotonicity, all-inlier spacing,
observed zero and numeric range checks remain. Explicit review and Apply are
still required, following the existing
[tick proposal workflow](CALIBRATION-TICK-PROPOSALS-20261113.md).

The VM regression confirms one cached OCR call and one axis call, edit-driven
review invalidation, explicit Apply, `UserConfirmed` `manual_three_anchor`
calibration with no tick `EvidenceRegionId`, and Undo restoring the prior state.
No worker DTO, frozen schema, model, dependency, localization or other UI file
changed in this fix.

## Validation

From the active worktree, with the existing E-local SDK home/package cache and
TEMP/TMP under `artifacts/tick-human-correction-check/tmp`:

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false '-p:OutDir=E:/Graph-auto-reader/artifacts/goal22-worktrees/resume-20261111-phase2/artifacts/tick-human-correction-check/bin/' --filter 'FullyQualifiedName~CalibrationTickProposalTests|FullyQualifiedName~CalibrationTickViewModelTests' --logger 'trx;LogFileName=human-correction.trx' --results-directory 'E:/Graph-auto-reader/artifacts/goal22-worktrees/resume-20261111-phase2/artifacts/tick-human-correction-check/test-results'
```

The initial invocation failed compilation after 43.1864385 s with CS8602 at the
new test's nullable `SessionLattice` dereference; zero tests executed. Adding
`Assert.IsNotNull(calibration.SessionLattice)` corrected the assertion without
weakening it. The final invocation passed **63/63**, zero failed/skipped,
zero warnings/errors, in **16.7346061 s** wall time. `git diff --check` passed.

TRX SHA-256:
`53563456ad6644418500b67ac2a34c7bfd0e9afad4320e4bf456d2425b8295d6`.
Compiled test App DLL SHA-256:
`f46a5d10e5765a9e3060e126c1b028baf5712e793d33d62e318d6d7462d7269e`.

This is owned service/STA VM evidence, not native GUI verification, accuracy,
production approval or a private rerun. The separately frozen
[450 diagnostic](PREPARED-CALIBRATION-450-DIAGNOSTIC-20261114.md) does not contain
this later fix and still reported zero complete proposals. License/dependency
scope is unchanged. Retain the bounded `artifacts/tick-human-correction-check`
outputs until this outcome is pushed and active integration consumers finish,
then retire them under the existing retention policy.

The completed output folder was subsequently [retired with keeper checks](COMPLETED-DIAGNOSTIC-OUTPUTS-RETIREMENT-20261114.md).
