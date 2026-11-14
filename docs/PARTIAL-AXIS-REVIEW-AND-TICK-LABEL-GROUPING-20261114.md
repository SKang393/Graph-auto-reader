<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Partial-axis review and tick-label grouping, 2026-11-14

## Reproduced failure

The immutable 0.4.45 owned dense-label capture has source SHA-256
`1adea2ca3e63b1779ae171d48b55812caec544f0471326e42de2a12d6bfac4cb`.
Three printed X labels were merged into one OCR region, text `1 2 3`, parsed
as `123`. The report had four numeric candidates, no complete proposal, and
was incomplete after 2.9776618 s. This is the reproduced input for the
grouping repair, not a private example or accuracy result.

## Source changes

`CalibrationTickProposal.CreateReviewDraft` now retains an independently
validated X or Y review draft when the other axis is incomplete. Existing
full-calibration checks and explicit manual review/Apply remain required;
partial evidence cannot complete or apply a calibration by itself.

Calibration OCR may optionally receive measured X-tick positions through its
same-build private IPC request. The tick array is snapshotted before scheduling,
validated as at most 128 increasing in-bounds positions, and ignored when
absent. The detector uses it only for conservative label separation: row-scoped
whitespace/gap checks require full-height evidence around a measured tick gap
and no component crossing the proposed separator. The default detector path is
unchanged. Focused OCR tests retain the known decimal/minor-tick case.

The calibration-only numeric-literal guard trims outer whitespace but rejects
internal whitespace before delegating to the existing parser. A merged `1 2 3`
therefore stays visible as raw text with no numeric value and cannot enter tick
pairing as 123. Generic OCR parsing is unchanged. Space-formatted thousands or
percent values are not auto-interpreted; enter those values manually.

No dependency, model, frozen schema, general parsing policy or model-selection
decision changed. This source batch is based on `c20d0577`; 0.4.46 was prepared
once for integration packaging.

## Evidence and status

Terra's focused OCR grouping progression completed at 5/5 tests in 98 ms,
preserving the known decimal/minor-tick case. Intermediate analyzer findings
CA1068 and CA1861 were corrected; independent review also caught a missing
`System.IO` import. Final validation passed with zero warnings/errors: App/tests
build 68.99 s, OCR tests build 7.12 s, probe build 6.24 s. App VSTest passed
1,001 with 0 skipped; OCR VSTest passed 859 with 16 skipped. Total: 1,860
passed, 16 skipped, 0 failed. Test durations were 35 s and 4 s. TRX SHA-256:
`6d2dc3a6ab68b187f11f0534195263df31aaa27207abd59e8d313878d8730d72`.

Source App, OCR and probe SHA-256 values are respectively
`2bdac8da9052e27f788028d878183855a0b3f64238a1dacef010cc33526bba36`,
`c0a79d5c5ae4d08c55a38b8d5c81c44ebe9182ef90b0939bc621309175c61032`, and
`162e7f0707395b03e319ba215aaaa5cbfd2acc49e7296cfe0b8a77cdcaf819fb`.

| Owned source control | Process / pipeline | Result |
| --- | ---: | --- |
| Dense labels | 3.2990128 s / 2,837.7748 ms | 6 measured pairs; complete, X 1..3 and Y 0..10 |
| Dense, tickless X labels | 2.898268 s / 2,510.8031 ms | 3 numeric Y labels; estimated Y draft at 381/81, max 10; X incomplete, overall incomplete |
| Ordinary | 3.5348895 s / 3,196.7616 ms | 6 measured pairs; complete |
| Tickless | 3.3999362 s / 3,055.4914 ms | 6 estimated pairs; complete review draft |

The dense result report is `bbb9b848b19f89da274dd24509282bb17a51c0eeb78535c361d44c0fdd73217c`;
dense-tickless `1368aaa2b942a426b9e5eb3531bd6ef68ebab460e91d9fda1d9b9067d977546b`;
ordinary `60d27e0f5991aff52b69429212c21c5ee77eef270ee73d9e8aa2c2cf3bffcd07`;
tickless `d935c7e274fc0a066cf257b8bf40da99b602b361aa1fe3366d1de29be195e80c`.
The unresolved merged X label remains visible and invalid rather than being
guessed into three labels. These timings are not a speedup comparison; the
fixed path performs more useful review work.

The tickless prepared-crop probe completed simulated explicit review, marker additions,
CSV export and project reopen in 4.7350975 s (4,041.6813 ms pipeline): three
CSV rows, three reopened points, and a saved user-confirmed manual three-anchor
calibration with null tick-evidence provenance. CSV SHA-256
`249b49a16b036e5be69191a4aea4a867e75d4cdbe0ac6fd31fa2568a23b3edee`; report
SHA-256 `cb06063c73649addb58929119f119ee45d2e42ce86a8fc6f8fb6f7f892f5bc2e`.

Build and test commands, from the active worktree:

```powershell
$active = 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2'
$primary = 'E:\Graph-auto-reader'
$out = Join-Path $active 'artifacts\hardware-final-build'
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false "-p:OutDir=$out\"
dotnet build tests/GraphReader.Ocr.Tests/GraphReader.Ocr.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false -p:BuildProjectReferences=false "-p:OutDir=$out\"
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false -p:BuildProjectReferences=false "-p:OutDir=$out\"
dotnet vstest (Join-Path $out 'GraphReader.App.Tests.dll') (Join-Path $out 'GraphReader.Ocr.Tests.dll') '/Logger:trx;LogFileName=partial-axis-and-grouping.trx' '/ResultsDirectory:artifacts/axis-evidence-check/test-results'
$probe = Join-Path $out 'GraphReader.CalibrationOcrProbe.exe'
$app = Join-Path $out 'GraphReader.App.exe'
$assets = Join-Path $primary 'artifacts/dev-portable/builds/0.4.45-20261114T074545114Z-4e721496/review-assistance/ocr'
$rust = Join-Path $primary 'artifacts/dev-portable/builds/0.4.45-20261114T074545114Z-4e721496/native/graphreader-engine.exe'
$evidence = Join-Path $active 'artifacts/axis-evidence-check'
foreach ($name in 'dense', 'dense-tickless', 'ordinary', 'tickless') {
    & $probe --tick-proposal-check (Join-Path $evidence "$name-request.json") $app $assets $rust
}
& $probe --marker-workflow-check (Join-Path $evidence 'tickless-request.json') $app $assets $rust (Join-Path $evidence 'source-full-workflow') prepared-crop
```

The first three build commands produced the App/tests, OCR tests and probe;
the VSTest command ran the two test assemblies above.

No private 0.4.46 run, native GUI check, accuracy claim or production approval
is included in this source outcome. Version 0.4.46 was prepared once for packaging.
After this record reached main at `42b0d9e2`, the unused owned reproduction output
`E:\Graph-auto-reader\artifacts\ocr-grouping-repro-20261114` was retired at
2026-11-14T08:35:18Z: 171 files, 102,081,435 path bytes. Exact containment,
reparse and inventory checks preceded deletion. The active
`artifacts/axis-evidence-check` inputs and source/package outputs were retired
after packaged verification and delivery record `05dd44bb` reached main, in the
2026-11-14T08:57:44Z cleanup. Original data, current assets, shared validation
output and user Data were preserved. No private case-level data was used.
