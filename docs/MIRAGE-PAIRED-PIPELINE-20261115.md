<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Paired Mirage diagnostic preparation

The experimental probe pairs the unchanged 0.4.51 ManualPreview baseline with
the isolated Mirage worker on the same in-memory prepared panel. Both readers
use the same automatic Rust-first axis geometry and measured ticks. This is a
text and association comparison, not a comparison of axis detectors. The
separate owned supplied-geometry control checks only OCR and association.
Neither path applies calibration or changes production approval.

The private mode is designed to verify the fixed 0.4.51 runtime before reading
the frozen 120-project development selection, exclude all 51 sealed projects,
and account for prepared panels, genuine no-panel fallback, timeouts and
unattempted work. Returned panel coordinates map to source coordinates once.
Private output is aggregate only; no case rows, image files, manual answers or
truth-selected panels are emitted. Strict Mirage completion requires valid JSON,
no parse error or repairs, known finite scores and a valid review draft.
Recovered text and unknown scores remain separately counted diagnostic evidence.

The probe compiled in Release with zero warnings or errors. The 21 runtime DLLs
in its reused tool directory were individually compared with the verified 451
build after compilation. The probe DLL SHA-256 was
`d1be4fbf916e44468ab893bb56043a077ac4964240e5a2d3998924aa9405c3e6` and the
restored App DLL SHA-256 was
`5be01923609dfc144f407db353bc7097f47c773bc44140b87af04d387ce041ff`.
The package identities are pinned in `RealDevPrepared451CalibrationCheck.cs`;
the Mirage runtime, model, manifest, license and worker identities are pinned in
`MiragePairedWorker.cs` and checked before payload selection.

Owned-only `--mirage-mapping-self-test` and `--mirage-paired-fake-self-test`
both exited 0. They cover normalized box mapping, unknown/nonfinite score
rejection, strict-response guards, shared panel bytes, incomplete results,
unsupported panels and interrupted-project accounting. Their result hashes are
`9a3046f0572c61032f078997b44f85ba76446a8be0e8ffd523fcbd5e8f6e1646` and
`289649af4f52da31028dee0b543c58acfb8205b68dfadd128ab17fa9eb7592c4`.
The actual owned worker control exited 0 in 25.972 seconds. Both its automatic
prepared-panel path and separately supplied-geometry path reported baseline
6 numeric rows, 6 associated pairs and one complete review draft, and Mirage
7 numeric rows, 6 pairs and one complete review draft. Mirage responses were
strict-valid; no failures were reported. The extra numeric row was retained.
These are owned functional observations, not an accuracy claim. The result
SHA-256 was `10b992ebf9919b4ed0442f56d8df0eab9971b7e843237feec9380d06baff2be7`.

In the automatic control, baseline wall time was 5,220.732 ms, including
1,911.783 ms initialization; Mirage wall time was 9,833.640 ms, including
9,711.107 ms native processing. In the supplied-geometry control, baseline
wall time was 624.943 ms and Mirage wall time was 7,869.092 ms, including
7,845.384 ms native processing. Mirage startup was 701.118 ms. These individual
owned timings are not a general benchmark. The fixed development pass is
reported below.

After the owned preflight and review, the fixed development diagnostic used:

```powershell
$tool = 'E:/Graph-auto-reader/artifacts/goal22-worktrees/resume-20261111-phase2/artifacts/corner-tick-worker-451-check/tool/GraphReader.CalibrationOcrProbe.exe'
$build = 'E:/Graph-auto-reader/artifacts/dev-portable/builds/0.4.51-20261114T235320548Z-e5491791'
& $tool --real-dev-mirage-paired-451-check 'E:/Graph-auto-reader/data/manual data' "$build/GraphReader.App.exe" "$build/review-assistance/ocr" "$build/native/graphreader-engine.exe" --explicit-opt-in
```

The per-project deadline is 45 seconds; the overall deadline and hard watchdog
are 900 and 905 seconds. Parent and children independently enforce their CPU
budgets. These controls and tests are diagnostic evidence, not model selection,
accuracy acceptance, or release approval.

The probe build, 451 runtime restoration and owned-only self-tests used:

```powershell
$env:DOTNET_CLI_HOME = 'E:\Graph-auto-reader\artifacts\dotnet-e-drive'
$env:NUGET_PACKAGES = 'E:\Graph-auto-reader\tools\nuget-packages'
$env:TEMP = 'E:\Graph-auto-reader\artifacts\test-temp'
$env:TMP = $env:TEMP
$out = (Resolve-Path artifacts/corner-tick-worker-451-check/tool).Path
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false -p:BuildProjectReferences=false "-p:OutDir=$out/" --verbosity minimal
$build = 'E:\Graph-auto-reader\artifacts\dev-portable\builds\0.4.51-20261114T235320548Z-e5491791'
$files = @(Get-ChildItem $build -File | Where-Object { $_.Name -match '^(GraphReader\..*\.dll|OpenCvSharp.*\.dll|Microsoft.ML.OnnxRuntime.dll|onnxruntime.*\.dll|libwebp.*\.dll|vcruntime140_cor3.dll)$' })
if ($files.Count -ne 21) { throw 'Runtime count mismatch' }
foreach ($file in $files) {
    $dest = Join-Path $out $file.Name
    Copy-Item -LiteralPath $file.FullName -Destination $dest -Force
    if ((Get-FileHash $file.FullName -Algorithm SHA256).Hash -cne (Get-FileHash $dest -Algorithm SHA256).Hash) { throw 'Runtime hash mismatch' }
}
& "$out/GraphReader.CalibrationOcrProbe.exe" --mirage-mapping-self-test
& "$out/GraphReader.CalibrationOcrProbe.exe" --mirage-paired-fake-self-test
```

## Fixed development result

The single paired pass completed with exit 0 in 706.679 seconds wrapper wall
time; the report records 705.496 seconds pipeline wall time. The aggregate
SHA-256 was `c4efe0547756a3725f0f03d86b208329784603a21c93bb1d18b873116895d50b`.
All 120 selected development projects were processed, containing 118 distinct
embedded images. Preparation returned 112 panels and used 15 genuine no-panel
fallbacks, for 127 effective panels. All 127 were attempted and finished;
there were no unsupported panels, interrupted panels, preparation errors,
timeouts or unattempted projects. The frozen assignment SHA-256 was
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`.

The shared 451 Rust-first geometry returned on 50 panels. The other 77 ended
before either reader: 73 `AXIS_AMBIGUOUS` and 4 `AXIS_NATIVE_UNAVAILABLE`.
Both readers returned on all 50 geometry-bearing panels. Baseline produced
367 literal numeric rows and 337 associated pairs, with zero complete review
drafts. Mirage produced 574 literal numeric rows and 499 pairs, with two
complete drafts, one using estimated positions. All 50 Mirage responses were
strict-valid, with zero repaired responses, unknown-score rows or reader
failures. At the project level, 2 had a Mirage-only complete draft and 118 had
neither reader complete; none had both complete or baseline-only complete.
These are proposal availability counts, not correctness scores.

Incomplete-draft reasons can overlap. Baseline counts were insufficient pairs
42, invalid numeric label 17, inconsistent spacing 16, unpaired label 11,
nonmonotonic 8, invalid range 3, duplicate association 2, ambiguous association
2 and zero not observed 1. Mirage counts were insufficient pairs 30,
inconsistent spacing 16, unpaired label 10, ambiguous association 7,
nonmonotonic 7, invalid numeric label 7, invalid range 2, zero not observed 1
and duplicate association 1.

Median baseline OCR wall time was 2,501.410 ms and p95 was 6,337.337 ms;
Mirage wall median was 10,708.071 ms and p95 was 12,430.054 ms. Mirage native
median was 10,544.163 ms, and its initialization was 485.552 ms. Baseline
initialization had a 1,596.379 ms maximum and zero median because the reader
session was reused. Both worker paths were verified under the approximately
0.4992-logical-core cap and BelowNormal priority; this is an aggregate CPU
budget, not an instantaneous per-core guarantee.

The aggregate's baseline App, model and manifest hashes match the frozen
0.4.51 pins above. Both readers used the same prepared panel and automatic
geometry, with no manual truth selection, sealed payloads, optimizer steps or
automatic Apply. The development decision retains the existing baseline and
Rust/WPF default: the observed two-of-120 Mirage draft availability and roughly
4.3-times median OCR wall time do not justify adoption. This is a
development-only availability and timing result, not an accuracy comparison.
Production approval remains false and the full production workflow remains
unapproved. Aggregate scratch is disposable after this outcome is pushed and
its active consumers finish.
