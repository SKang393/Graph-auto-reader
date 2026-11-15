<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Flex 5 internal preview 0.4.52 verification

The existing Flex 5 Windows host ran an isolated check of the verified internal
preview, not a clean-machine or release acceptance test. The check used the existing pinned SSH
identity and host key, with a new `preview452/calibration-review-20261115`
directory and separate Data directory. The destination was absent and free
space was 224,322,859,008 bytes before transfer.

The one transferred ZIP was 87,558,560 bytes, SHA-256
`47e7643e3f2ea2adb20d6899914228b0e5de5cd8e0c72e398638cfb593ebba8f`,
from source `68781bcce22cf925bff2f99b21c139b622117c06`. Its remote
extraction contained 477 files; nine specified App, Rust, OpenCV, OCR and notice
identities matched the locally verified package. The App executable hash was
`c7979e7d88482fb239e6655d4939aea15755d605a472730c116776cda1da8e27`.
The host reported Windows NT 10.0.26200.0. The portable smoke exited 0 in
1,825.9653 ms; the Production runtime guard exited 2 in 272.4452 ms, as
expected for ManualPreview. Both had empty stdout and stderr.

One previously generated project-owned 640 x 480 image, SHA-256
`c1750cab62d79c96b75310cdb5419edcb91a1e99218b248894a26b731aa43dda`,
was supplied to the packaged Rust worker and then to one persistent CPU OCR
worker. Rust returned the original-pixel geometry and three X and three Y ticks
in 167.4458 ms. Two identical requests returned six numeric review-only rows
each. Client wall times were 2,040.6221 ms and 1,480.7784 ms; reported worker
totals were 1,673.3473 ms and 1,477.524 ms. The same process reported one
session, reuse false then true, one intra/inter/worker thread, and a verified
0.4992 logical-core Job budget with BelowNormal priority. CPU pacing was zero;
the worker exited 0 on EOF. Sampled peak private and working-set values in the
two response snapshots were 137,900,032 and 172,376,064 bytes; these are not
memory ceilings. No calibration was applied.

There was one successful package transfer, one extraction, one invocation of
each smoke, one Rust geometry invocation and exactly two OCR reads on the same
worker. Each finished without a reported product failure or timeout. The first
SSH preflight attempt was denied by the local sandbox before connecting; the
authorized normal-host retry succeeded. One local hash-list command had a
PowerShell syntax error and was corrected without rerunning the product checks.

The scripts in `artifacts/flex5-452-check` were parsed with
`[Management.Automation.Language.Parser]::ParseFile`, then the preflight,
extraction, runtime smoke and owned inference scripts were run once each over
the pinned SSH route described in `artifacts/flex5-449-check/README.md`.
The local captured reports have SHA-256 values:

- `extract-result.json`: `f05cf255e6077c512c8332bdf46827a0bdea8eb4e310c6b376b06bb9c9b78918`
- `runtime-result.json`: `24559c5cd3b7e23de98782883bcade46c34716b8a98a2c8821cae5233ef19acd`
- `owned-result.json`: `77319d8f8631cc2a427fed49f8a427799abeffaf995974e39d5513e1e3fd8175`

This exercises the orientation-enabled 0.4.52 worker, but this fixture does not
isolate the orientation change. No GUI interaction, GPU, installer, accuracy,
private-data, or clean-machine claim follows from these checks. Production and
model approval remain separate gates.

For later retention review, the remote 0.4.49 validation directory was absent
when checked after this run; this task did not remove it. A read-only inventory
of the current 0.4.52 directory found 501 files, 284,081,896 bytes, no reparse
points and no matching live consumers at that instant. It includes the uploaded
87,558,560-byte ZIP as well as the 477-file extracted app and owned input and
reports. The full inventory is `artifacts/flex5-452-check/remote-452-inventory.json`.
Nothing was deleted; the extracted package and owned input remain on the host.
The exact reviewed remote scopes are
`C:\Users\sungw\AppData\Local\GraphAutoReaderValidation\preview449\calibration-review-20261114`
(absent at inventory) and
`C:\Users\sungw\AppData\Local\GraphAutoReaderValidation\preview452\calibration-review-20261115`
(the 501-file inventory). The completed checks have released the latter;
any subsequent cleanup requires fresh consumer and path checks. The verified
primary 0.4.52 package is outside these remote test scopes.

## Prepared remote retirement

The guarded helper `artifacts/flex5-452-check/remote-cleanup.ps1` has
SHA-256 `42f5d2ecc1983afe0774a81de9f49d4490c5e35d0ebf6494bbf6e72909188fb2`;
its local wrapper `cleanup.ps1` has SHA-256
`226299e333d578a163ab5b3922ea1c7b8389b82c8352971e4ec234e30411179b`.
Both parsed successfully. The wrapper verifies the retained primary 0.4.52 ZIP
and App executable, pins the inventory and extra-file hashes, and requires this
exact outcome blob on the live `origin/main` before Execute. It rejects a stale
tracking ref and a missing or invalid remote receipt. The remote helper checks the exact
501 paths and byte counts, all 24 extra-file hashes, ZIP-to-app hashes, host,
containment, reparse points and all visible live processes. It re-resolves the
literal target before removal and constrains each extra-file path. It targets only the 0.4.52
test directory; the absent 0.4.49 directory is not targeted. Lead-run
`-ValidateOnly` passed with exit 0 in 7.0363 seconds: all 501 files /
284,081,896 bytes and 477 archive entries matched, and nothing was removed.
Execute remains pending. Once this outcome is pushed and remote cleanup completes,
the local test logs and helper files are disposable.

From the worktree, the review and execution commands are respectively:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/flex5-452-check/cleanup.ps1 -ValidateOnly
powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/flex5-452-check/cleanup.ps1 -Execute
```
