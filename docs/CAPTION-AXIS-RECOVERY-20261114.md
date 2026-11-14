<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Caption-side axis recovery control

## Change and bounded result

After the existing content-extent selection returns `NO_AXIS`, the Rust worker
scans the image twice to measure longest connected runs. It allocates only
width- and height-sized score vectors, not another image. Recovery requires
exactly one connected pair with at least three strict outward ticks on each
axis, using the existing score ratios and 128-pair cap. Multiple pairs and
unexplained strong bands outside the selected plot remain rejected. Existing
successful paths are unchanged.

The owned synthetic 512 x 512 control has a small plot, dotted caption, and
outward X and Y ticks. Image SHA-256:
`af97c4d4ce5fd057eac1845cf24c881e5037d3e1b3d969b7f68d12686f55f959`.
Using the same request, 0.4.46 App/OCR, and Rust source versus the packaged Rust
baseline:

| Rust worker | Result | Process / pipeline |
| --- | --- | ---: |
| 0.4.46 baseline `71455f26d241aec90bf2b88b4267e272028a286dd2b8e7fbc5f86ab8d27fdfa0` | `NO_AXIS`, 0 paired ticks | 0.5566541 s |
| Source `36a8d527e922be7d738f7249f833f7b5feaa3a2c7aadef3207920cd831f72e20` | Complete review proposal, 6 measured pairs, 6 numeric candidates | 3.3378306 s / 2935.5882 ms |

The source proposal used original panel pixels, with X sessions 1 through 3
and Y range 0 through 10. It remained review-only, production approval false,
and unapplied. This is a functional owned-control recovery, not a speed
comparison, private accuracy result, or general caption-invariance claim.

## Validation and reproduction

Rust validation used:

```powershell
cargo test --offline --manifest-path native/graphreader-engine/Cargo.toml --target-dir E:\Graph-auto-reader\artifacts\caption-geometry-test-20261114
powershell.exe -NoProfile -ExecutionPolicy Bypass -File packaging/native-engine/Build-NativeEngine.ps1 -RepositoryRoot E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2
```

Final Rust validation passed 41 tests (0.05 s test time; 1.54 s compile); the
release compile took 1.80 s. The initial wrapper invocation was blocked by
execution policy; rerunning with process-scoped PowerShell `-ExecutionPolicy
Bypass` succeeded.

The two worker runs used the same owned request, App and OCR assets:

```powershell
$active = 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2'
$build = 'E:\Graph-auto-reader\artifacts\dev-portable\builds\0.4.46-20261114T083718338Z-42b0d9e2'
$probe = Join-Path $active 'artifacts\hardware-final-build\GraphReader.CalibrationOcrProbe.exe'
$request = Join-Path $active 'artifacts\caption-recovery-check\request.json'
$app = Join-Path $build 'GraphReader.App.exe'
$ocr = Join-Path $build 'review-assistance\ocr'
& $probe --tick-proposal-check $request $app $ocr (Join-Path $active 'artifacts\native-engine\release\graphreader-engine.exe')
& $probe --tick-proposal-check $request $app $ocr (Join-Path $build 'native\graphreader-engine.exe')
```

The first command used the source Rust worker; the second used the packaged
0.4.46 baseline. Both exited 0 with empty stderr. Source report SHA-256 is
`b15f667f83b18e10e884cf80e2eb6a1caccbb19fde3ef8fe21778ba2b460d3fc`; baseline
report SHA-256 is
`9008c2eb452268e6cb29e5f5032d6d21548a6e4de6683c4062540b99dfe45bea`.
The source report records panel SHA-256
`4ee78338dea5052a57f9247f0708405effe6c56d4f20a2a0ca0b31f5424d4047`.

## Retention and limits

`E:\Graph-auto-reader\artifacts\caption-geometry-test-20261114` contains 19 files totaling
13,550,953 bytes; retire it after this record is pushed. Keep the under-100-KB
`artifacts/caption-recovery-check` control for the assigned 0.4.47 package
consumer. Preserve original data, current models, and shared QA data. No
package was created by this check. The broader packaged workflow and real-dev
confirmation remain separate evidence. No dependency, license, model or
scientific-value default changed.
