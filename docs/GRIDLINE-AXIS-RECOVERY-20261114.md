<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Gridline axis recovery, 2026-11-14

## Failure and bounded repair

An owned graph with full horizontal and vertical gridlines returned
`AMBIGUOUS_AXIS`, although only its actual bottom and left axes carried short
outward ticks. The Rust projection selector treated the gridlines as competing
axes. The delivered 0.4.46 baseline reproduced this failure.

The new Rust path runs only after `AMBIGUOUS_AXIS`. It reuses the existing
40% line minimum, 90% comparable-strength rule, thickness, continuity, run-length
and tick-spacing checks. Exactly one connected pair must have at least three
strict outward measured ticks on each axis. Inward strokes and legacy
endpoint-only evidence cannot establish this recovery. Multiple qualifying
pairs or unexplained strong bands outside the selected plot remain ambiguous.
Existing successful paths retain their behavior and original pixel coordinates.

Search is capped at 128 candidate pairs as a resource bound. Exceeding it rejects
the entire search; no truncated subset is selected. The implementation reuses
the projection and pixel buffers, preserves `MAX_PIXELS`, and leaves the
half-core worker budget and parent-controlled cancellation unchanged.

## Validation and identities

The initial owned regression recorded the old `AMBIGUOUS_AXIS` behavior in a
32/32 passing suite. After recovery, 37/37 Rust tests passed: compile 2.17 s,
command 3.553 s, tests 0.05 s. Controls cover the exact grid recovery, sparse
ticks on either axis, inward-only evidence, two tick-supported panels,
outside strong bands and the 144-pair resource rejection. The earlier axis,
continuation, white-margin and numerical guards remain covered.

Source CI [43578459880](https://github.com/SKang393/Graph-auto-reader/actions/runs/43578459880)
passed at `e696802e`: 2,693 .NET tests, 30 skips, 37 Rust tests, 68 packaging
checks and 62 version-policy checks. App compilation had zero warnings/errors;
release jobs were skipped.

```powershell
$env:CARGO_TARGET_DIR = "$PWD\artifacts\native-engine"
cargo test --manifest-path native/graphreader-engine/Cargo.toml --locked --offline --jobs 1
$env:CARGO_BUILD_JOBS = '1'
& .\packaging\native-engine\Build-NativeEngine.ps1 -RepositoryRoot (Get-Location).Path
```

The incremental release build exited 0: compile 2.47 s, command 4.536 s.
The 290,304-byte worker is
`artifacts/native-engine/release/graphreader-engine.exe`, SHA-256
`cf08957415611ec7a89c0dfaa84e75b026b67c7c3fbebd8163d1a1a4646c2b95`.
The existing MSVC static-CRT and `/Brepro` settings remain. The build helper
updated its target-local toolchain record and retained the standard-library
notice SHA-256 `5647be074c8edf7339fd863055923a8fc80bc5610a8d4661ec3b767b9d392c27`.

The generator's optional `-GridLines` draws full lines within the existing owned
axis bounds. Default drawing order, labels and tick positions are unchanged.

```powershell
& .\tools\GraphReader.CalibrationOcrProbe\New-TickCalibrationFixture.ps1 -OutputPath "$PWD\artifacts\grid-recovery-check\gridlines.png" -GridLines
```

The owned PNG is 640 x 480, 6,122 bytes, SHA-256
`1d258bf5c7be0b7788286f01e64ac8d1ca1eaac8e0b574b927ca1e52011c6894`.
The request SHA-256 is
`08168e4f9d0d880f7cfdf97a5106b79bfeb20aa9e589d579eec982dee3788641`.

## Actual worker and OCR check

Both runs used the same owned request, probe and unmodified delivered 0.4.46
App/OCR/assets. Only the Rust executable argument changed. Root executed the
following argument vectors through hidden `Start-Process`, separate stdout and
stderr files, and a 60-second owned-process watchdog:

```powershell
$active = 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2'
$package = 'E:\Graph-auto-reader\artifacts\dev-portable\builds\0.4.46-20261114T083718338Z-42b0d9e2'
$probe = Join-Path $active 'artifacts/hardware-final-build/GraphReader.CalibrationOcrProbe.exe'
$request = Join-Path $active 'artifacts/grid-recovery-check/request.json'
$app = Join-Path $package 'GraphReader.App.exe'
$assets = Join-Path $package 'review-assistance/ocr'
& $probe --tick-proposal-check $request $app $assets (Join-Path $package 'native/graphreader-engine.exe')
& $probe --tick-proposal-check $request $app $assets (Join-Path $active 'artifacts/native-engine/release/graphreader-engine.exe')
```

| Runtime | Result | Recorded timing | Report SHA-256 |
| --- | --- | --- | --- |
| Delivered 0.4.46 Rust | `AMBIGUOUS_AXIS` | 65.6888 ms geometry; process wall time not recorded | `03b55f27859f9fbcac53ea375be9a37eacf2173fd6da655fcccefc7cd96bcd2f` |
| Local Rust + delivered 0.4.46 App/OCR | Complete review-only proposal, 6 numeric labels, 6 measured pairs, 0 estimated | 3.19327 s process; 2,818.5174 ms pipeline; 128.7154 ms geometry | `083a8b4503a32883aaa565ee0f02031523b5a475da661f519eadf601cd531237` |

The first wrapper expected an incorrect prefixed error code and stopped after
the valid baseline response. The baseline was not rerun, and no baseline wall
time is claimed. The source check exited 0 with empty stderr and `applied=false`.
Detected bounds were left 100, bottom 380, right 580, top 80; measured X ticks
140/340/540 and Y ticks 80/230/380. The actual CPU OCR worker reported a verified
0.4992-logical-core budget, one session/thread and no persistent cache.

Runtime identities:

- Delivered Rust: `71455f26d241aec90bf2b88b4267e272028a286dd2b8e7fbc5f86ab8d27fdfa0`.
- Delivered App EXE: `3085c125bdaf9b6271363e3593341dc8d7cd2b9761e1be22282f04389c30c5f7`.
- Delivered App DLL: `634d5121dc73d1c18282581b2623def039d86089712d124f9fb260a85c194d03`.
- Probe DLL: `72129810638483b2231d2659af6e81443f2728ea985b36c39782055381bf410a`.
- Reader: `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`.
- Manifest: `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a`.

## Limits and retention

This source change is included in verified preview 0.4.47. The historical
0.4.46 baseline build has been retired after replacement. This is one owned control, not native GUI acceptance, private-corpus
coverage, an accuracy estimate or production approval. Dense searches over
128 pairs, inward-only ambiguous grids, interrupted axis strokes and other
unsupported layouts can still require manual review/cropping. No universal
multi-panel or padding-invariance guarantee is made. No model, license,
dependency, threshold constant or scientific-value default changed.

`artifacts/grid-recovery-check` (eight owned files, 13,649 bytes) was retired
at 2026-11-14T17:16:30Z after its 0.4.47 packaged consumer passed and record
`3cb620fa` reached main, under [ARTIFACT-RETENTION.md](ARTIFACT-RETENTION.md).
The PNG, requests and raw reports were not committed. Current runtime/model inputs and the active Rust target
cache remain available to their consumers. No original or private data was read
or modified for this batch.
