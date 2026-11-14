<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Bottom-left tick lane classification

An owned coordinate control exposed a deterministic role error. A literal zero
box `(68,376,12,10)`, centered at `(74,381)`, was classified as YTick beside plot
`(100,80,480,300)`, but XTick when only plot width changed to 1000. The existing
X rule ran first and its 5% horizontal tolerance captured the unchanged Y label
one pixel below the baseline. The same error occurred before recognition with
`NumericExpected`, so both axis suggestion and recognized role were wrong.

This can remove the observed zero from an otherwise valid three-pair Y axis.
It is an owned reproduction, not evidence that this caused any private result.

## Change

[GraphTextRoleClassifier.cs](../src/GraphReader.Ocr/GraphTextRoleClassifier.cs)
now gives Y-lane precedence only to a numeric/expected-numeric box wholly left
of the Y axis that strictly straddles its baseline and satisfies the existing
vertical tolerance. Boxes fully below the baseline or crossing the Y axis keep
the existing X rule. Explicit and semantic context overrides still run first.
No numeric tolerance, confidence, model, coordinate transform, contract, fit or
export gate changed; no zero or endpoint value is inferred.

The classifier version is now
`graph-text-role-classifier-v12-bottom-corner-tick-lanes`. Existing
`OcrResultCache` keys and `ProductionAutomaticDetectionAdapter` fingerprints
already include that version, so earlier classification results cannot silently
reuse the old identity. No approval or activation switch changed.

Other owned files are `GraphTextRoleClassifierTests.cs` and
`CalibrationTickProposalTests.cs`. The six-literal App control follows the actual
X/Y/other pre-recognition switch. Both plot widths retain three X and three Y
pairs, with anchors `(140,380)`, `(140,80)`, `(540,380)`. The glyph center remains
381 while the zero anchor uses measured tick 380.

## Commands and evidence

Commands ran from the active worktree with the existing E-local SDK home/cache,
TEMP/TMP under `artifacts/corner-tick-classification-check/tmp`, serialized builds
and normal-host test IPC. `$check` below is the absolute artifact directory.

```powershell
$check = "$PWD/artifacts/corner-tick-classification-check"
dotnet test tests/GraphReader.Ocr.Tests/GraphReader.Ocr.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false "-p:OutDir=$check/ocr-bin/" --filter 'FullyQualifiedName~BottomCornerYAxisLabelDoesNotChangeAxisWhenPlotWidthChanges' --logger 'trx;LogFileName=corner-baseline.trx' --results-directory "$check/test-results"
dotnet test tests/GraphReader.Ocr.Tests/GraphReader.Ocr.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false "-p:OutDir=$check/ocr-bin/" --logger 'trx;LogFileName=corner-ocr-final.trx' --results-directory "$check/test-results"
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false "-p:OutDir=$check/app-bin/" --filter 'FullyQualifiedName~CalibrationTickProposalTests' --logger 'trx;LogFileName=corner-app-final.trx' --results-directory "$check/test-results"
```

| Check | Result | Wall time |
| --- | --- | ---: |
| Unchanged v11 baseline | 2 narrow passes, 2 wide failures, expected YTick/actual XTick | 27.4679179 s |
| Final OCR suite | 870 passed, 16 skipped, 0 failed | 23.056818 s |
| Final App proposal suite | 39 passed, 0 skipped/failed | 58.8426433 s |

The 16 OCR skips are existing opt-in external payload/validation probes, not
newly weakened tests. An initial App build stopped before tests after
45.3269828 s with CS2012 on the shared `GraphReader.Pdf/obj` DLL. The compiler
reported a possible Defender lock; ownership was not independently established.
After other builds ended, the unchanged source passed in the serialized slot.
No Defender or host setting changed. Final builds had zero warnings/errors;
`git diff --check` passed.

TRX SHA-256 identities:

- Baseline: `1f97e83bd388d013e3e56418a8fdb2eaa770a993281ef49ce6f15cd7a3232495`
- OCR: `e86afc6b872b5e83cd2256d5bd0c1a011df6ea66b2f049c7029cfb91e0917e5e`
- App: `22c80040e78e570f1b27c57fc33eb7f9ff78989cb5eb86a63574f9247d073722`

This validates supplied literal/coordinate handling, not neural recognition,
private-set improvement or native GUI behavior. No private rerun, model change,
download or package occurred. License/dependencies remain unchanged. Retire
`artifacts/corner-tick-classification-check` after the outcome is pushed and
integration consumers finish; it currently holds 401 files/202854766 bytes.
