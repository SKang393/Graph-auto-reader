<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Owned bottom-corner worker diagnostic, 2026-11-14

This owned control extends the source/test evidence in
[BOTTOM-CORNER-TICK-LANES-20261114.md](BOTTOM-CORNER-TICK-LANES-20261114.md)
to the actual packaged 0.4.51 CPU OCR worker from source
`e54917917d1acdc3aed2c556f3d2faf0bcbff253`. The integrator verified all 477
archive/extracted files before this run.

## Scope and command

The serialized probe-only command was:

```text
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false -p:BuildProjectReferences=false -p:OutDir=E:/Graph-auto-reader/artifacts/goal22-worktrees/resume-20261111-phase2/artifacts/corner-tick-worker-451-check/tool/
```

After restaging 21 verified runtime libraries beside the probe, its EXE ran
`--owned-corner-tick-451-check` with these exact arguments:

```text
E:/Graph-auto-reader/artifacts/goal22-worktrees/resume-20261111-phase2/artifacts/hosted-preview-451/build/GraphReader.App.exe
E:/Graph-auto-reader/artifacts/goal22-worktrees/resume-20261111-phase2/artifacts/hosted-preview-451/build/review-assistance/ocr
```

Execution used hidden `Start-Process`, redirected stdout/stderr, and a 180-second
parent watchdog. Existing E-local CLI home/package cache were reused.

`CornerTickWorkerCheck.cs` creates two owned panels entirely in memory, containing
the same six glyph pixels/positions, while changing plot width from 480 to 1000.
The bottom Y zero is wholly left of the Y axis and straddles the baseline with
its center below it. The worker's returned polygon must confirm that exact
geometry; otherwise the control is incomplete. No rendering/model search is
authorized to make it pass.

One persistent CPU worker reads each panel once. Its returned `SuggestedAxis`
records the existing pre-recognition NumericExpected classification; `Role`
records final recognized classification. Both must remain Y for the zero. Each
control must also return six literal numeric labels and a complete six-pair
measured calibration using the explicit owned plot/ticks. This is not Rust axis
detection, scientific Apply, export, independent accuracy or private acceptance.

The mode verifies 0.4.51 version, exact local/packaged App and OCR assembly
equality, existing fixed model/manifest pins and reported runtime file hashes.
The report records actual selected EXE/DLL hashes and cold/reused timings.
No identity was added to historical 449/450 pinned modes.

## Results and identities

The first build passed in 12.0642209 seconds with zero warnings/errors. An initial
1.0404883-second invocation was void before worker/inference startup: the new
request used a descriptive binding, while the existing client requires SHA-256.
Only that binding and error reporting were repaired. The unchanged images and
model were then exercised once after a 14.1801713-second zero-warning/error
probe-only rebuild.

Final process exit was 0 in 48.8325555 seconds, with empty stderr. Both controls
returned exactly six literal numeric rows and six measured pairs, complete
first/final/Ymaximum values 1/3/10, and anchors `(140,380)`, `(140,80)`,
`(540,380)`. Both detected zero polygons were `(70,376)` through `(78,387)`,
center `(74,381.5)`, confirming the corner reproduction. Actual worker
`SuggestedAxis=Y` and `Role=YTick` remained correct at both widths.

| Request | Pipeline ms | Init ms | Recognition ms | Session reused |
| --- | ---: | ---: | ---: | --- |
| Narrow | 8699.2094 | 2576.0854 | 4763.0986 | false |
| Wide | 2140.0980 | 0 | 1996.3078 | true |

This is one cold plus one reused call, not steady-state performance. Both report
CPU, one session/thread/worker, verified 0.4992 logical-core cap and BelowNormal.

SHA-256 identities:

- App EXE: `144d4cfab07d81429de6366a76a211fe515fa8227578b9c857ed76e5cca6efed`
- App DLL: `5be01923609dfc144f407db353bc7097f47c773bc44140b87af04d387ce041ff`
- OCR DLL: `1abf49cbb93f068307ec1e3f6bd1e90be5d5ee9949d72ca3bdf501d25dc8a43d`
- Final probe DLL: `540b5b550488a91dc1020c735584521ac7e9c0c1dd7e24cfa39567e766cb350c`
- Narrow in-memory PNG, 1821 bytes: `94a1a79b63f8d592cd74d0c8b08f443cd820457dbc9b6faaa4e8bd1b9e48d773`
- Wide in-memory PNG, 2105 bytes: `6dde78894155399c7df61ca2e63f6e668d44b6b351f39f434cf77022a544ef97`
- Final `stdout.json`: `afc7e77a9f6bf8bed0726cbf8582769791cb9f339cc582976de1935231d00107`
- 21-file staging identity record: `cd97bf889301ed901848d62721e9c2afee21597d03e12b3bb329c80e172ca5a5`

## Resources and retention

The image/input/report allowance is below 10 MB; both encoded panels and their
pixel buffers remain in memory. This is not the probe build size: required
compiler output and runtime dependency copies total 42,686,714 bytes here.
Neither the model nor the whole package is copied into the probe folder.
Existing independent half-core parent/child budgets and the 170/175
second probe deadlines apply. No source image or dataset is read or written.
Only bounded JSON/stdout/stderr belong under
`artifacts/corner-tick-worker-451-check`. Its active consumer is this one owned
packaged-worker control and the integrator's outcome record. Retire its disposable
outputs after the record is pushed and package consumers finish. Final inventory
is 53 files / 42,704,506 bytes, including 17,792 bytes of top-level reports/logs.
No named App/probe processes remained after exit; this package consumer is done.
No new dependency
or license is introduced; source remains Apache-2.0 and uses existing Segoe UI
without copying font files.

## Status

Owned packaged-worker control passed. This does not measure Rust geometry,
private coverage, exported calibration or independent OCR accuracy. No private
read, model change, threshold change, scientific Apply, package activation or
Git action occurred in this diagnostic.
