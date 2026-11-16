<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Reusable verified-preview activation

The 0.4.56 delivery needed two narrow repairs to a disposable activation script:
historical ledger entries can lack a directory, and `File.Replace` requires an
explicit backup path under Windows PowerShell. The replacement is a reusable
[activation helper](../packaging/Activate-VerifiedInternalPreview.ps1), with
`Inspect` as its default and `Activate` as its only mutation. It neither builds,
downloads nor retires packages.

Before a move, it binds the actual remote main, pushed scope and source ancestry,
both working ledger blobs, ledger schema 1, pointer schema 2, the sole retained
entry, previous keeper, package and complete payload hashes. It also checks
contained/disjoint paths, reparse points, safe archive entries, `portable.mode`,
ManualPreview metadata, executable/runtime identity, process consumers and shared
Data. Activation moves the one payload and ZIP, verifies them again, then uses the
existing atomic JSON writer. The previous keeper remains available for separate
reviewed retirement. An interrupted move can resume only when exactly one copy
of each payload and ZIP exists and the old pointer remains intact.

The [PowerShell controls](../packaging/tests/Test-ActivateVerifiedInternalPreview.Tests.ps1)
use a real temporary bare Git origin and a thirteen-file owned payload. They
cover normal activation, recovery after the payload move, old keeper and Data
preservation, empty Data, incomplete historical entries, unpushed scope, local
ledger drift, unsafe ZIP entries, changed bytes, package Data, unsupported/missing
schemas, and a checksum-bound ZIP and tree both missing `portable.mode`. Refusal
controls check that neither the pointer nor staged/target state changes.

```text
powershell.exe -NoProfile -ExecutionPolicy Bypass -File packaging/tests/Test-ActivateVerifiedInternalPreview.Tests.ps1
```

Final local run: **exit 0 in 19.5579423 s** on Windows PowerShell 5.1. An
independent source review found no remaining scoped blocker. The same command
is now included in full Windows CI. This fixture result does not mean an actual
package has been activated; that outcome belongs in its delivery record.

All earlier failed attempts are retained in this concise account: sandbox
OS-temp move access failed in 1.7357441 s; sandbox Git IPC failed in 5.6517515 s;
the first normal-host fixture correctly rejected its backslash ZIP paths in
6.2925766 s; then a real helper variable collision with PowerShell's automatic
`$Matches` failed in 6.5476054 s. Fixture paths/transport and the variable name
were repaired. The initial successful controls took 9.8097143 s. Review added
explicit schema/sentinel guards, passing in 16.9102946 s; the final 19.5579423 s
run isolated the missing-sentinel guard with matching archive and tree. No
actual preview was moved by these tests.

Final helper SHA-256:
`560efedb36cb263a5afe559630da31e3528b857f68306f6190d0201b2c0c115e`.
Final controls SHA-256:
`ea67f23e2c2e06ec5da9a1785f019fc38be57a655f76dd52a3e2ff2a8ca9e7a4`.
No dependency, license, model, application contract or package version changes
are required. Both source files carry Apache-2.0 notices. Temporary fixture
repositories are removed after the controls complete; originals and active
packages are outside their scope.

Commits `3970a0e6` (helper) and `4126addb1069c1f94142c4901cb8f0b3d1ea6629`
(delivery record) are pushed to main. The latter passed
[full Windows CI 43810577712](https://github.com/SKang393/Graph-auto-reader/actions/runs/43810577712),
job `133111123518`, including the new activation transaction controls, all
**3,220 .NET tests**, **80 Rust tests** and **68 packaging verification controls**,
with **30 expected .NET skips** and zero failures. The primary build had zero
warnings/errors; separate packaging fixture builds emitted 68 nullable-context
warning annotations. The invalid-scope control's Git refusal was expected.
Actual 0.4.57 inspection, activation and launcher verification subsequently
succeeded as recorded in its [delivery outcome](INTERNAL-PREVIEW-457-VERIFICATION-20261116.md).
