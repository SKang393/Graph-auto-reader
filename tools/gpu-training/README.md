<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# RX 6600 XT training environment

The project's existing native Windows ROCm environment belongs here, at
`E:\Graph-auto-reader\tools\gpu-training\venv`. The maintainer authorized its
consolidation from `E:\GraphReader-GpuProbe-20261029` on October 29, 2026.
`E:\Tools` has no duplicate ROCm installation. The project's other Python
toolchain is the Python 3.11 Paddle/ONNX conversion environment, not a substitute
for this Python 3.13 GPU training environment.

## Use

From the repository root:

```powershell
# GPU forward/backward smoke check. Prints its result; creates no result file.
& .\tools\gpu-training\Run-GpuPython.ps1

# Dependency consistency check using the same interpreter.
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('-m', 'pip', 'check')
```

Pass a trainer's actual script/module arguments through `-PythonArguments`.
The launcher preserves the caller's working directory and restores its process
environment on exit. Temporary files and caches stay in the ignored `.state`
directory under this folder; pip's download cache is disabled.

All optimizer/model training must explicitly use the RX 6600 XT. Each trainer
must check its device and tensor/model residency and reject CPU fallback.
Selecting this interpreter alone does not change a trainer's device selection.
CPU preprocessing and non-training checks are allowed. The default probe fails
if ROCm or the RX 6600 XT is unavailable; it performs no optimizer steps and
does not establish full trainer or product acceptance.

## Installed runtime and provenance

Python 3.13.7; PyTorch `2.13.0+rocm10.1.0`; internal HIP `7.16.26385`;
AMD Radeon RX 6600 XT / gfx1032. `requirements.lock.txt` records the installed
package versions. The environment still uses its existing base Python 3.13
installation; no base interpreter or display driver is moved or reinstalled.

The existing 33,935-file environment (3,908,036,390 logical bytes before
relocation) is retained by a same-volume directory move. Installed package
licenses/notices stay with it. Environment binaries and `.state` are ignored
and must not be committed or bundled into the application.

`gpu-package-provenance.json` retains source URLs and archive hashes from the
original installer reports for the seven ROCm/PyTorch distributions. These are
installation provenance, not new downloads or a redistribution approval. The
remaining package versions are pinned in the lock file. Local toolchain
metadata and this aggregate report contain no private research data and may be
committed. The prior test's ONNX exports were disposable probe outputs, not
production-approved models.

## Completed probe record, October 29, 2026

The original bounded CompactCenterNet test used only the project's procedural
train split: batch 8 x 3 x 128 x 128, FP32, three warmup steps and 20 measured
optimizer steps. The two GPU medians were 56.813 ms and 55.894 ms. Historical
CPU comparisons were 424.238 ms with one thread and 245.898 ms with six; no new
CPU training is authorized or performed by this consolidation.

All 55 parameter tensors changed; loss fell from 4.327467 to 0.573359.
CPU/GPU forward maximum absolute difference was 4.76837158e-7, gradient relative
L2 difference 1.37201559e-5, and ONNX Runtime parity error 2.38418579e-7.
GPU repeat losses and ONNX hashes matched. Peak allocated/reserved GPU memory
was 169.6/218.0 MiB. The original `pip check` passed.

These measurements exclude startup, tuning, data generation, evaluation and
export time. MIOpen emitted missing gfx1032 tuning/grouped-convolution warnings;
the first run also reported an initial tuning error before successful training.
This proves bounded backend compatibility, not full convergence, every model's
determinism, or end-to-end product readiness. No sealed/private corpus was used.

## Retention and relocation verification

Keep the current environment, this concise record, package provenance/versions,
and the reusable launcher/probe. Do not retain the old standalone directory,
completed benchmark scripts/results/logs, installation logs, test ONNX exports,
or old caches. Clear `.state` after its active consumers finish, following
[the retention policy](../../docs/ARTIFACT-RETENTION.md).

Relocation checks passed on October 29, 2026:

- `Run-GpuPython.ps1`: RX 6600 XT / gfx1032 detected; matrix and convolution
  forward/backward succeeded with finite gradients on `cuda:0`, strict
  deterministic algorithms enabled, and `cpu_fallback: false`. The existing
  MIOpen missing grouped-convolution/tuning-database warnings remain; both
  operations completed. No optimizer, training corpus, or model promotion ran.
- `Run-GpuPython.ps1 -PythonArguments @('-m','pip','check')`: no broken
  requirements. Direct `venv/Scripts/pip.exe --version` and
  `venv/Scripts/rocm-sdk.exe --help` also succeeded from the new location.
- Rebound 28 console launchers and four activation/configuration files; updated
  nine installed package RECORD files. No old probe paths remain in those
  launchers/configuration. Current project training instructions point here.
- Python syntax, PowerShell syntax, package/provenance metadata, Git ignore
  rules, and argument forwarding passed. An initial environment-restoration
  check failed for absent `PIP_CACHE_DIR`; explicit removal of previously absent
  variables fixed it. The repeated check passed for all 14 affected variables,
  caller working directory, E-drive temporary paths, and failure exit code 7.
- The interpreter and core GPU payloads retained these SHA-256 hashes:

| Environment-relative file | SHA-256 before and after |
| --- | --- |
| `Scripts/python.exe` | `200d001707ed494066ef00e31e38330e32490c2a74a450ca5d66d2179396fd0e` |
| `Lib/site-packages/torch/_C.cp313-win_amd64.pyd` | `a5ad9a5477f0d85dd272211dc2fff4bc63e38c4abf758c76ecf6ccd453d2c444` |
| `Lib/site-packages/torch/lib/torch_hip.dll` | `6e240c46a71e679e94d72be908e21146ddd29e3b498f5b4e500f1a15509c53cf` |

Cleanup completed after source and this record were confirmed on `origin/main`
at `9c84016f8616a70f87c2d6f5ba0001c080d35e3c`. Removed the 44 completed
probe/cache files (2,310,750 logical bytes) in the old standalone folder and
six disposable smoke-cache files (127,571 logical bytes) from `.state`.
Total: 50 files, 2,438,321 logical bytes, followed by 42 empty directories.
Deletion used exact reviewed file paths and empty-directory removal; no active
consumers or reparse points were present. The old standalone folder and smoke
cache were confirmed absent, and the relocated GPU interpreter remained present.
The 3.64 GiB installed runtime was relocated, not deleted or duplicated.
The launcher recreates its temporary `.state` directories only when needed.

## Additional duplicate-folder cleanup, October 29, 2026

The maintainer also requested removal of the separate GPU restore folder and
the obsolete Desktop checkout. These were different paths from the original
probe folder removed above.

| Reviewed folder | Files | Logical bytes | Disposition |
| --- | ---: | ---: | --- |
| `E:\GraphReader-GPU-Restore-20261029-v1` | 34,048 | 5,078,651,959 | Redundant ROCm installation, download cache and completed install/probe scratch |
| `C:\Users\Sungwoo\Desktop\Graph-auto-reader` | 5,281 | 766,508,010 | Old pytest/runtime scratch and recovered upstream clone |

The restore environment has the same 25 package versions as the retained
environment. All seven GPU package archive SHA-256 values and the three core
runtime file hashes listed above match. The retained interpreter independently
confirmed ROCm/HIP availability and the RX 6600 XT. No current launcher points
to the restore folder; its remaining project reference is a completed probe's
historical command record. That record remains in the main project.

The recovered C: clone is based on
`a92f017c37567b63c03de127ea183f5208e94a40`; all 906 reachable/reflog commits
already exist in the retained repository, and its HEAD is an ancestor of main.
There are no untracked/ignored files or external linked worktrees. Two dirty
source/test files are preserved as `docs/retired-c-recovery-20261029.patch`
before deleting the clone: 7,158 bytes, SHA-256
`701b779a011167d5bec33ac15ec11182245c435d0c86d6c71bb717e12554b862`.
`git apply --reverse --check` verified the patch against those recovered edits.
The patch is historical source, not a change to the current application; remove
its working-tree copy after pushing it and retain its Git-history reference.

No active process or reparse point was found under either cleanup root. Push
this review and the recovered patch before removing the two exact folders.
Restore/update the GPU environment in this project's `tools/gpu-training/venv`;
do not create another standalone E: restore environment or recreate the old
Desktop checkout. Completed recovery downloads and scratch follow the same
retention policy as other completed runs.
