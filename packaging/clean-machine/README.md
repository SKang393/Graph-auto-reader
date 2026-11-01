<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Clean Windows validation

`Invoke-GraphReaderCleanMachineValidation.ps1` runs inside a newly installed,
network-disabled Windows x64 VM. It binds one clean development-portable payload
to exact application and source-built OpenCV checksums, loads the native DLL with
Windows loader search restrictions, runs the real WPF `--portable-smoke` path,
and writes `opencv-clean-machine.json` even when validation fails.

The report passes only when all of these conditions are direct observations:

- the VM provenance binds an official checksum-verified Windows evaluation ISO;
- the Windows installation is less than 24 hours old;
- no developer toolchain command is present on `PATH`;
- no network adapter is up;
- the payload is a clean exact-version build with `portable.mode`;
- the exact reviewed `OpenCvSharpExtern.dll` loads successfully;
- the self-contained application portable smoke exits zero;
- the manual portable contains no `.onnx`, `.param`, or `.bin` model payload.

This script does not edit `packaging/common/release-audit.json` and cannot approve
a public release by itself. The report must be reviewed, copied to the tracked
release-evidence location, checksum-bound in the mandatory gate, and revalidated
by `Install-ReleaseRuntime.ps1` before production packaging may consume it.

`Invoke-GraphReaderPdfiumCleanMachineValidation.ps1` applies the same VM, ISO,
freshness, toolchain, and offline checks to a production common-publish payload.
It validates all five packaged PDFium files, executes the exact reviewed runner
against the fixed public synthetic 72 by 72 PDF fixture, checks the exact raw
output hash and dimensions, binds both the application host EXE and the
`GraphReader.App.dll` code bytes, and launches the exact production application with
`--production-runtime-smoke --require-packaged-pdfium`. The environment override
is cleared for that launch, so success directly exercises the packaged
`pdfium/reviewed-approval.json` fallback.

The PDFium report is `pdfium-clean-machine.json`. Like the OpenCV report, it
cannot approve release packaging. `Install-ReleaseRenderer.ps1` must independently
validate the current harness hash, report schema, typed results, VM identity,
application bytes, renderer bytes, resources, fixed render, and packaged fallback.

## Preparing a separate Windows test PC

The separate PC is the VM host. A normal physical Windows installation on that
PC does not itself satisfy these clean-VM validators: they require a fresh
QEMU guest, a matching UUID and Windows build, and an offline adapter check.
No current installer or portable release candidate has passed those gates.

Before creating the guest, copy the necessary project tools and later the
verified candidate into a designated test folder on the test PC. The test PC
and guest may use their C: drive; the development computer's project-local
storage rule does not require an E: drive on another machine. Keep the Windows
evaluation ISO, QEMU installer, VM disk, provenance, test outputs, and caches
inside the test folder. An already installed QEMU program under `C:\Program Files`
may be used. Obtain the Windows evaluation image and its published SHA-256
from the official distributor, and retain the exact QEMU installer used for
the VM provenance. Do not enter a guessed or self-generated checksum as the
official checksum.

Run the host preflight from the project folder, replacing the four
variables with the actual absolute paths and published checksum:

```powershell
$qemuExe = '<absolute path to qemu-system-x86_64.exe>'
$qemuInstaller = '<absolute project-local QEMU installer path>'
$windowsIso = '<absolute project-local Windows evaluation ISO path>'
$officialIsoSha256 = '<official 64-digit ISO SHA-256>'
& .\packaging\clean-machine\Test-ValidationHostReadiness.ps1 `
    -QemuExecutablePath $qemuExe -QemuInstallerPath $qemuInstaller `
    -WindowsIsoPath $windowsIso -ExpectedOfficialIsoSha256 $officialIsoSha256
```

The preflight hashes the three files, checks the QEMU version and the ISO
against the supplied checksum, and reports blockers. Its helper compiles with
temporary files inside the project and gives the QEMU probe project-local
`TEMP` and `TMP` values. The QEMU executable is held open against ordinary
write or delete attempts and rehashed across the version check. Captured
stdout and stderr are each limited to 16,384 characters; the probe and stream
drain share a 10-second limit, and a Windows Job Object terminates descendants.
The check writes no persistent report.
`inputs_checked_manual_verification_needed` is not a VM or release pass. The
preflight cannot prove the checksum came from an official source, the ISO will
boot, the machine supports virtualization or has enough disk, or that the
installer produced the selected QEMU executable. It creates no VM and neither
runs the product installer nor approves a release. Inspect the returned
`status` and `blockers`; the process exit code alone is not a readiness decision.

Provision a fresh Windows x64 evaluation guest with QEMU networking disabled.
The host PC may remain connected; the guest must be offline during validation.
Record the actual QEMU version and installer hash, ISO and independently
verified official ISO hash, VM configuration hash and UUID, guest OS build
and update revision, and the exact candidate commit, version, executable and
harness hashes. The guest must report the matching QEMU manufacturer and UUID,
have no developer tools on PATH, show no active network adapters, and be less
than 24 hours old when the validators run. The existing guest validators read
these values from a `graphreader.clean-windows-vm-provenance.v1` JSON file;
use observed values, not a copied historical receipt.

For the guest provenance, `isoSha256` and `officialIsoSha256` must be the two
independently checked values; `isoSha256Verified` and `freshInstall` are JSON
booleans. Record `environmentKind` as `fresh-windows-evaluation-vm` and
`networkMode` as `none` only after those conditions have been established.
The remaining required common fields are `vmId`, `vmConfigurationSha256`,
`qemuInstallerSha256`, `qemuVersion`, `expectedOsBuild`, `expectedOsUbr`,
`expectedCommit`, `expectedVersion`, and `expectedExecutableSha256`. The
OpenCV validator additionally requires `expectedOpenCvSha256` and
`expectedHarnessSha256`. The PDFium validator additionally requires
`expectedApplicationDllSha256`, `expectedPdfiumRendererSha256`,
`expectedPdfiumHarnessSha256`, and `expectedRendererSmokeScriptSha256`.
The actual UUID and build can be read inside the guest using
`Get-CimInstance Win32_ComputerSystemProduct` and
`Get-CimInstance Win32_OperatingSystem`; UBR comes from the Windows current
version registry. Use `Get-FileHash -Algorithm SHA256` for the actual files.

After the release candidate exists, make the minimum required candidate and
validation scripts available inside the guest and run the existing OpenCV and
PDFium validators with their required paths and exact candidate hashes. The
OpenCV validator currently checks a model-free manual-preview payload; its
result does not validate the production application workflow. The PDFium
validator exercises the reviewed renderer and production smoke path. These
component checks do not replace the separate installer and portable tests:
install, launch, repair or reinstall, uninstall, data preservation, portable
path isolation, offline import/detection/review/export, and artifact parity
must be observed for the same release candidate before approval.

The OpenCV validator requires `-PayloadRoot`, `-OutputRoot`,
`-VmProvenancePath`, `-ExpectedCommit`, `-ExpectedVersion`,
`-ExpectedExecutableSha256`, and `-ExpectedOpenCvSha256`. The PDFium validator
requires the same common parameters except OpenCV, plus
`-ExpectedApplicationDllSha256` and `-ExpectedRendererSha256`. Run each in a
separate `powershell.exe -NoProfile -File` process because the validators exit
with their own status code. Place their evidence outputs under the project
folder. Do not reuse the historical 0.0.21 hashes as current candidate values.
