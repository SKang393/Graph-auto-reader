<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Windows distribution validation resume

The existing production builder already derives installer and portable payloads
from one common self-contained publish and checks parity, except portable.mode.
`-CandidateOnly` defers only the installer-clean-windows-vm,
portable-clean-windows-vm and release-artifact-pair-parity gates. It does not
permit unapproved model, native-runtime or product prerequisites.

The current installer expects `build-metadata.json` and uses the normal
product's install, registry and Start Menu identity. Delivered ManualPreview
449 instead has `build-info.json` and portable.mode. Wrapping that ZIP in the
production installer is not a supported packaging path. A separate internal
installer would need deliberate metadata, installation/data isolation and
same-publish parity; none was created or presented as a release candidate.
Do not create that parallel installer merely to bypass existing gates.

Read-only inspection found existing QEMU under the historical task's
`artifacts/goal22-clean-machine/tools/qemu` directory. Its executable SHA-256
is `47d57a6072e0bb3bd98f87926eb129eb1736dfe818c67b3b81ef7ce4edd0b3cd`.
The directory also contains `iso/Windows11-Enterprise-Eval-25H2-en-us.iso`,
7,092,807,680 bytes. Its existing verification metadata records SHA-256
`a61adeab895ef5a4db436e0a7011c92a2ff17bb0357f58b13bbc4062e535e7b9`
matching the recorded official value; the large ISO was not rehashed here.
No VM disk/configuration was found in this scoped local tree. An initial
PATH-only QEMU check was insufficient and was corrected by this discovery.
No duplicate tool installation, ISO download, guest creation or host-feature
change was made.

The [earlier host record](CLEAN-WINDOWS-HOST-PREPARATION-20261030.md) documents
a separate Windows laptop with authenticated SSH, verified ISO inputs and
an offline Windows 10 QEMU guest as of October 31. That is historical evidence,
not confirmation of current availability. Scoped inspection of 122 small
metadata files and the SSH aliases did not establish a laptop target or
authentication setup. Keys and credentials were not inspected; no network
scan or connection was attempted. The current host hypervisor query was denied
in the restricted environment, so virtualization capability is unverified.

The maintainer identified `flex5laptop`. Its existing SSH service is reachable.
The standard current-user SSH config has an owner/ACL error, and default
known-host files have no laptop entry. Scoped inspection recovered the existing
project-local `artifacts/remote-testing/ssh/known_hosts` and validation identity.
Using those existing files with `-F none`, `BatchMode=yes`,
`IdentitiesOnly=yes`, `StrictHostKeyChecking=yes` and `UpdateHostKeys=no`
verified the hostname `FLEX5LAPTOP` and OS version
`Microsoft Windows NT 10.0.26200.0`. The query exited 0 in 0.981 seconds.
No private-key contents were inspected and no authentication, trust, ACL or
system setting changed. REV-012 is resolved; no new laptop credentials are needed.

An exact-byte 449 portable smoke on its existing host, with isolated data and App/OpenCV/OCR
identity checks, can provide a useful host diagnostic. It must not claim fresh
guest, installer, public-release, visual or automatic-accuracy acceptance. The
old model-free OpenCV validator intentionally rejects bundled ONNX payloads,
so it cannot certify the 449 OCR preview. Fresh-guest production pair evidence
and other release gates remain separate. No dependency or license changed.

## Exact 449 existing-host startup check

The retained 449 ZIP was uploaded once in 3.904 seconds to a newly created,
isolated validation directory on Flex5Laptop. Remote SHA-256 matched
`422eb96e35a7c4d73ae87c13a1a5079450f4d6d42f5ccd161a26c8b2b514d1c6`
at 87,554,492 bytes. The 477 extracted files include the self-contained .NET
and Windows Desktop 10.0.11 runtimes. Nine critical identities matched the
retained package: App EXE/DLL, managed/native OpenCV, Rust worker, OCR model,
OCR manifest and both OCR license/notice files.

- `GraphReader.App.exe --portable-smoke`: exit **0**, **630.378 ms**.
- `GraphReader.App.exe --production-runtime-smoke`: expected and actual exit
  **2**, **227.750 ms**, preserving the unapproved-production gate.
- Both stdout/stderr streams were empty. The isolated Data contains only two
  three-byte layout settings; existing user data and binaries were unchanged.

The copied remote report SHA-256 is
`a279100498925f66d02e3d78d04ddeb999806e0c29a22c5ba3c9d03d00ddee57`.
Two earlier measurement attempts were void: an encoded command exceeded the
Windows command-line length before execution; a PowerShell 5 process object
returned a null exit code after the first portable invocation. A task-local
script invoked with `-File` and an owned `System.Diagnostics.Process` handle
reverified the identities and recorded the final exits with bounded waits.
The archive was not uploaded or extracted again to repair those scripts.

This checks existing-host startup, portable path policy and fail-closed
production behavior. It did not launch a GUI, infer an image, test accuracy,
exercise an installer or establish clean-machine acceptance. The remote task
directory and local `artifacts/flex5-449-check` remain only for the next owned
Rust/OCR worker check. Retire those copies after that outcome is pushed and
the active consumer finishes; retain the primary delivered 449 package.

## Owned Rust and CPU OCR check

The same remote package then processed one owned 640 by 480 graph generated by
the existing `New-TickCalibrationFixture.ps1`. Image SHA-256:
`c1750cab62d79c96b75310cdb5419edcb91a1e99218b248894a26b731aa43dda`.
The small task-local client used the existing `GRA1` Rust frame and length-
prefixed OCR protocol; no SDK, second App copy or new runtime was installed.
Rust returned plot bounds and three actual ticks on each axis in 158.201 ms.
That is a standalone process-call timing; the short-lived Rust job/priority
was not independently inspected, so it is not separate scheduling evidence.
Only its observed geometry fed OCR, without known numeric labels as metadata.

Exactly two identical requests ran in one packaged CPU worker and one inference
session. Cold client wall was 1,744.424 ms; worker total was 1,440.597 ms,
including 595.056 ms initialization and 692.898 ms recognition. The reused
read took 961.781 ms client wall and 714.201 ms worker total, with zero
initialization and 669.961 ms recognition. Both returned the same six numeric
labels, all marked for review. No calibration was applied. These two reads
are not a steady-state benchmark or independent accuracy estimate.

The worker verified CPU selection, one intra/inter-operation thread, one worker,
BelowNormal priority and a 0.4992-logical-core Job Object cap on this 16-logical-
processor host. CPU pacing was zero; DirectML alone receives the separate
pacing delay in current source. App/OCR/Inference/ORT and model/manifest hashes
matched the retained package. Closing stdin ended the worker with exit 0;
there was no restart or persistent inference cache. The 3,087-byte OCR stderr
contained 13 ONNX Runtime unused-initializer warnings, without a failure.

The copied inference report SHA-256 is
`ef65e160a148d64d82929e6ad0799e5e83e5fdb2d4c83fa43edbe75ad41e673d`.
No private/user input, GPU setting change or GUI action occurred. The CPU
worker check is complete; its isolated remote package copy, generated owned
image and local result subtree can be retired after this record reaches main
and live-consumer/path guards pass. Original projects, SSH trust/identity and
the primary retained package are outside that cleanup scope.

## Remote task-copy retirement

After the owned outcome reached `origin/main` in `841db52e`, guarded cleanup
removed the exact remote calibration-review task directory on FLEX5LAPTOP.
The receipt was received at `2026-11-14T22:37:24.9529541Z`: 503 files and
284,071,480 path bytes retired, target absent, no active consumers or reparse
points, and the primary retained 449 executable hash unchanged. The existing
SSH identity/trust and all unrelated remote data remain intact. One small
task-created retirement helper remains outside the removed directory pending
its final cleanup. Local owned inputs remain temporarily active for the
[native workflow and fresh-process reopening check](NATIVE-PREVIEW-449-CHECK-20261114.md).
