<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# GPU training launcher

`Run-GpuPython.ps1` launches the project-local Python environment under `venv`.
The recorded runtime uses Python 3.13.7, PyTorch `2.13.0+rocm10.1.0`, HIP
`7.16.26385`, and an AMD Radeon RX 6600 XT / gfx1032. Exact dependencies are
listed in `requirements.lock.txt`; package sources and hashes are recorded in
`gpu-package-provenance.json`.

From the repository root:

```powershell
& .\tools\gpu-training\Run-GpuPython.ps1
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('-m', 'pip', 'check')
```

Pass script or module arguments through `-PythonArguments`. The launcher
preserves the caller's working directory and restores its process environment
on exit. Temporary files and caches use `.state` under this folder.

The default probe checks GPU forward/backward execution and fails when its
required ROCm device is unavailable. It performs no optimizer steps. Selecting
an interpreter does not automatically move a trainer's model or tensors to GPU;
trainers are responsible for explicit device selection and residency checks.

Runtime compatibility checks do not establish convergence, digitization
accuracy, redistribution rights, or application release readiness.
