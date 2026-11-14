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

REV-012 asks for the existing laptop target. Once available, an exact-byte 449
portable smoke on its existing host, with isolated data and App/OpenCV/OCR
identity checks, can provide a useful host diagnostic. It must not claim fresh
guest, installer, public-release, visual or automatic-accuracy acceptance. The
old model-free OpenCV validator intentionally rejects bundled ONNX payloads,
so it cannot certify the 449 OCR preview. Fresh-guest production pair evidence
and other release gates remain separate. No dependency or license changed.
