<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Hosted internal preview delivery

## Problem and implemented path

The 0.4.37 internal preview was uploaded through a temporary one-job runner.
That runner is unregistered. The new manual `Build internal preview` workflow
uses a GitHub-hosted Windows runner and the prepared main source SHA/version.
It does not create a runner on the maintainer's PC or publish a public release.

The source-built OpenCV runtime already delivered in 0.4.37 can be reused for
ManualPreview without transferring private provenance records. The new importer
requires an exact ZIP filename/SHA match to one complete internal build ledger
entry. It checks the embedded clean ManualPreview version/commit, executable
identity, reviewed runtime metadata, current source-revision policy and DLL
hash before copying only the DLL, runtime metadata and required notices.
Duplicate/traversal entries are rejected. A small lineage record preserves the
source archive identity and `cleanMachineEvidence=false`, `releaseApproved=false`.
The source-evidence install path is unchanged; the two inputs are mutually
exclusive. Production packaging gates are unchanged.

The hosted wrapper requires GitHub Actions, main, a clean exact checkout and
the expected central version. Existing Build-DevPortable performs its fast test
set, self-contained publish and smoke checks. The wrapper verifies the Rust
worker and standard-library notice hashes, creates one internal ZIP, checks
each archived file against the published bytes and reuses the existing upload
validator. User Data is excluded. The validator permits only the exact Rust
standard-library HTML notice path with its worker/metadata hash checks; older
pre-Rust previews remain valid. General HTML and source/private files remain
rejected.

The workflow downloads the existing immutable artifact using the official
[download-artifact action](https://github.com/actions/download-artifact/blob/9000827ccba6bdab643e8b6fd33ac0654aef8333/action.yml),
pinned at v8.0.2 commit `9000827ccba6bdab643e8b6fd33ac0654aef8333`.
This is an MIT-licensed build-time action, not an application dependency.
Checkout, .NET setup and upload retain the repository's existing pinned actions.

## Source archive identity

- Artifact: `13892703110`, run `43102839837`.
- Package: `GraphAutoReader-0.4.37-win-x64-internal-preview.zip`, 79,867,980 bytes.
- ZIP SHA-256: `bc9c3548e13f3faf2098f35ec9c7870647859d03493795b66fcf5678b81f2236`.
- Source commit: `e4067fd912496b1eedc1afafefbfe6de5b00b19b`.
- OpenCV DLL SHA-256: `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`.
- Imported lineage SHA-256: `45dbeff4fae19e77a6bd60c0d31269620b860b50195dc90988bdcd6d9363d8d4`.

## Validation

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File packaging/tests/Test-ReviewedPreviewRuntime.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File packaging/tests/Test-HostedInternalPreview.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File packaging/tests/Test-InternalPreviewUpload.Tests.ps1
```

Importer fixtures passed 7/7: valid archive, wrong ledger hash, missing notice,
Production metadata, changed DLL, traversal and case-duplicate entries. The
initial fixture run exposed a missing Windows PowerShell compression assembly;
loading that assembly explicitly fixed it before the passing run. Actual
0.4.37 import also passed with the DLL and lineage hashes above. The final
importer includes all four required OpenCV/MSVC license and notice files.

Hosted helper fixtures passed 5/5: valid identity, wrong version, wrong source,
changed Rust worker and changed Rust notice. Upload-validator regression checks
passed, including valid Rust payload, arbitrary HTML rejection, worker/notice
hash mismatch and incomplete native payload. PowerShell syntax checks and
`git diff --check` passed. Independent importer review found no concrete blocker.

These initial source/fixture/import checks were followed by the successful
hosted 0.4.38 delivery below. Installed/portable clean-machine acceptance
remains open.

## Retention and maintenance

The 0.4.37 ZIP and latest pointer are replaced only after the verified 0.4.38
ledger is pushed. Completed importer checks under
`artifacts/preview-runtime-0437-import-check`,
`artifacts/preview-runtime-0437-import-final` and the initial failed
`artifacts/preview-runtime-test-ac0a74fbb11d4c87b4affcb792422269` were retired
after this record reached main at `286382c0`: 14 files, 16,008,146 bytes.
No private attestation or complete source-built
OpenCV environment was duplicated.

Hosted [Windows CI run 43522216337](https://github.com/SKang393/Graph-auto-reader/actions/runs/43522216337)
completed successfully on that commit, including the new importer/helper
checks, bounded OCR host self-tests, Rust/application tests and all packaging
validation. Release build/publish jobs were correctly skipped. This CI run did
not dispatch the separate hosted internal-preview packaging workflow.

GitHub artifacts expire. The current workflow pins the verified 0.4.39 input;
future deliveries must update that pin before the artifact expires. Build numbers
still advance once per actual packaged build, and public release cadence and
final product approval remain governed by VERSIONING.md.

## Hosted delivery 0.4.38

[Build 43528703957](https://github.com/SKang393/Graph-auto-reader/actions/runs/43528703957)
completed successfully from `2992419ccf0b6d483adb023dae0ac56c86d8880a`.
The hosted materializer reproduced the pinned OCR reader. App, Domain and
integration checks passed 1,001 tests; seven opt-in integration tests were
skipped. ManualPreview smoke exited 0 and the deliberately unavailable
Production smoke exited 2. Source CI `43528642593` passed separately.

- Artifact `14086606947`: 87,184,827 bytes, SHA-256
  `1ae6100a6d94cc159f87a89c0fe2bab7bdb196bd4421844180a91026494e21cc`.
- Inner ZIP `GraphAutoReader-0.4.38-win-x64-internal-preview.zip`:
  87,440,684 bytes, SHA-256
  `6cbe09d2453147a39d31a781a6453b69c1044011a04ad76a0b175879be64d65e`.
- Downloaded extraction: 477 files, 195,801,513 bytes, each checked against its
  ZIP entry; source, executable, Rust worker, notices and OCR assets matched.
- Native delivered-app check: bundled Rust anchors and nine OCR suggestions,
  blank values and disabled Apply; no App process remained after close.
- Artifact expiry: 2027-02-11. No installer, public release or production
  approval was created.

After the ledger reached main in `d93d8f1f`, `latest.json` was switched to
0.4.38. Exactly one extracted build and one package remain. Cleanup retired
474 files, 353,027,806 bytes: the prior build and ZIP, incoming outer archive,
receipt, QA scratch and temporary pointer backup. Shared user `Data` was
preserved. The first atomic pointer replacement rejected an empty backup path;
retrying with an explicit temporary backup succeeded before any old build was
deleted. This delivery record is not a clean-machine acceptance claim.

## Hosted delivery 0.4.39

[Build 43541187921](https://github.com/SKang393/Graph-auto-reader/actions/runs/43541187921)
completed from `62a1862ece524bcd07cb8ee7028848936e03b53e`. This integrated preview
adds saved CPU/GPU choice, a cancellable measured automatic profile, and one
Suggest calibration action pairing Rust tick positions with OCR labels. Values
and anchors require review before Apply. App, Domain and integration checks
passed 1,065 tests with seven opt-in skips. The separate
[source CI 43541187420](https://github.com/SKang393/Graph-auto-reader/actions/runs/43541187420)
passed 2,581 .NET tests with 30 skips across 13 projects, ten Rust tests and
packaging/localization checks; builds reported zero warnings and errors.

- [Artifact 14091868242](https://github.com/SKang393/Graph-auto-reader/actions/runs/43541187921/artifacts/14091868242):
  87,226,981 bytes, SHA-256
  `6aab594b5dd6974d8f7f413ff1de1edf788ec1f2a30bf9eb62c20970eeea6a99`.
- Inner ZIP `GraphAutoReader-0.4.39-win-x64-internal-preview.zip`:
  87,483,858 bytes, SHA-256
  `8c40467d720c418b7656eb0b7d0d977e97571e04809440c3dad439f470469175`.
- Extracted build `0.4.39-20261114T013632352Z-62a1862e`: 477 files,
  195,923,591 bytes. Safe entry checks and every extracted-file hash matched.
- Receipt SHA-256: `ffdebcb46a6e9588803acb02acb0e81136440cd3bf4f6dd044849761d8ea837c`.
  Build-info SHA-256: `8be5fae8c97355425b90605ef090c96e48b3afd374050d6af39791548d255553`.
  Executable SHA-256: `a73614cc97d797a8024f96f4faa64d8cbe4907a656f2aa474d7dfdf3fa1b931d`.
- Rust SHA-256: `2f72bdaeb1454e1b2b6558e36fc2793e21f2be104a2fcd51c031e1d12754043f`.
  The Rust notice and six required OpenCV/Microsoft/Paddle notices/licenses
  matched. OpenCV and OCR model bytes remain the identities above; current
  review manifest is `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a`.
- Isolated local ManualPreview smoke exited 0; unavailable Production smoke
  exited 2. No normal window was opened. Artifact expiry is 2027-02-12.

The existing `--tick-proposal-check` probe was run against the downloaded
package's own App, Rust executable and `review-assistance/ocr` directory on the
two explicitly owned inputs in [the calibration record](CALIBRATION-TICK-PROPOSALS-20261113.md).
The complete control returned all six pairs and the expected three anchors in
3.833 s wall time (149.850 ms geometry, 2,598.096 ms OCR, 41.704 ms pairing).
The incomplete panel returned seven pairs, `CALIBRATION_TICKS_INSUFFICIENT_PAIRS`
and null calibration values/anchors in 4.103 s (60.340 ms geometry,
2,988.362 ms OCR, 46.260 ms pairing). Neither run applied or exported values.
Both reported CPU, one inference thread and worker, BelowNormal priority and
the verified 416/10000 job budget on 12 logical processors (0.4992 core).
These are two execution controls, not representative accuracy measurements.
Packaged App DLL SHA-256 is
`00d6c628e0df75c49033aaf9351cbbe64f7460c3690932f841eebb7852f6cb0f`.
Control and incomplete JSON hashes were respectively
`0ba4ec63a35f845dcf7c2d279a43bcc46551d7db948f140707555072984ad05d`
and `5cd43b6066d95b23155285dce1eecaa469568eadef819f6b5e8ba4e2961c5b5f`.

After this ledger/outcome reaches main, switch the latest pointer and retire
the superseded 0.4.38 extracted build/ZIP, the outer download, receipt, smoke
scratch and tick-check request/control/results. Shared user Data, the original
owned source image and the still-open earlier QA App and its dependencies are
preserved. The current hardware-assets model is a hard link, so removing the old
build path must not remove that active input. Future hosted builds now use the
verified 0.4.39 artifact as their reviewed runtime input. This is an internal
ZIP only. Native saved-choice and Suggest calibration interaction remain waiting
after the user stopped Computer Use (REV-011); no installer, public release,
clean-machine pass or production approval is claimed.
