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

GitHub artifacts expire. The current workflow pins the verified 0.4.41 input;
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

After this ledger/outcome reached main at `a039f669a3360392bdae1d238280bebe6bcc67c2`,
the latest pointer was atomically switched to 0.4.39. Contained, reparse-free
cleanup retired 490 files totaling 370,485,937 path bytes: the superseded
0.4.38 extracted build/ZIP, outer download, receipt, smoke scratch, pointer
backup and tick-check request/control/results. One extracted build and one ZIP
remained; no process was running from either package before replacement.
Shared user Data, the original
owned source image and the still-open earlier QA App and its dependencies are
preserved. The current hardware-assets model is a hard link, so removing the old
build path did not remove that active input, whose SHA-256 was rechecked.
Future hosted builds now use the
verified 0.4.39 artifact as their reviewed runtime input. This is an internal
ZIP only. Native saved-choice and Suggest calibration interaction remain waiting
after the user stopped Computer Use (REV-011); no installer, public release,
clean-machine pass or production approval is claimed.

## Hosted delivery 0.4.40

[Build 43545166216](https://github.com/SKang393/Graph-auto-reader/actions/runs/43545166216)
completed from `19edf74d927ec6fad839c31031292c885facf02f`. This preview adds
bounded marker matching from a user-selected original-pixel example, visible
proposals and individual acceptance through existing point editing. Hosted
App, Domain and integration checks passed 1,077 tests with seven opt-in skips.
The separate [source CI](https://github.com/SKang393/Graph-auto-reader/actions/runs/43545111607)
passed 2,593 .NET tests with 30 skips across 13 projects, ten Rust tests and
packaging/localization checks. Compiler warnings and errors were zero.

- [Artifact 14094395240](https://github.com/SKang393/Graph-auto-reader/actions/runs/43545166216/artifacts/14094395240):
  87,252,726 bytes, SHA-256
  `f5872fee4bb6ff040f25afb43ec9926419e9e0128683d29846d66279cde3364a`.
- Inner ZIP `GraphAutoReader-0.4.40-win-x64-internal-preview.zip`:
  87,509,475 bytes, SHA-256
  `4321c500efd107fc776b24717a7b434e177a17945af1d1e5ea2e57c3ca26331e`.
- Extracted build `0.4.40-20261114T021827354Z-19edf74d`: 477 files,
  196,029,063 bytes. Safe entry checks and every extracted-file hash matched.
- Receipt SHA-256: `2661c19d761e97f55d8131b070e5aff22a8add70a30539758821f7322c4eaf1e`.
  Build-info SHA-256: `3fd34ea8b5db19fc7e5e062e247822e96657a7d09b8bfda1df091e44642c4ea7`.
  Executable SHA-256: `662e859fc16b1a50d53da4477836e1e59207335f1851a5775e139b1727a8b5fd`.
  App DLL SHA-256: `59cc202cf8dc9c9a6cc386e74fbdce216314300b992f4206f7a761b2a89b00c3`.
- Rust, reviewed OpenCV, OCR model/manifest and their required notices retained
  the verified 0.4.39 identities above. Hosted materialization reproduced the
  same OCR reader. Artifact expiry is 2027-02-12.
- Isolated local ManualPreview smoke exited 0; unavailable Production smoke
  exited 2. No normal window was opened.

The existing `--marker-exemplar-check` probe ran against the downloaded App
and its own native DLL on the in-memory 1,280 x 800 owned control in
[the marker assistance record](MARKER-EXEMPLAR-ASSISTANCE-20261113.md).
Two matches were returned at (640, 400) and (960, 400), both with zero pixel
error. First request wall time was 571.506 ms, including 26.723 ms decoding and
80.459 ms matching. Cancellation of the observed child completed in 96.896 ms;
the exact child exited. Restart took 556.601 ms and returned the same matches.
Whole probe wall time was 1.539 s with exit 0 and no owned process left.
The runtime reported one OpenCV thread, BelowNormal priority and a verified
416/10000 Windows job CPU budget on 12 logical processors (0.4992 core).
Result JSON SHA-256 is
`b1c31615931bcda1c7655ac65a735a20cfe6a5fe4e01acc3c4f59249b406f40f`.
These are controlled execution/lifecycle observations, not accuracy evidence.

After this record reached main at `b2b378d9cf51ecdbec2c910ce95d26555f2d692b`,
the latest pointer was atomically switched to 0.4.40. Contained, reparse-free
cleanup retired 485 files totaling 370,662,879 path bytes: the superseded
0.4.39 extraction/ZIP and the 0.4.40 outer archive, receipt, smoke/probe scratch
and pointer backup. One extracted build and one ZIP remain. Shared user Data,
the current hard-linked OCR input (hash rechecked), and the earlier open QA App
and its dependencies were preserved. Native device, calibration and marker interaction
remain waiting under REV-011 after the user stopped Computer Use. This is an
internal ZIP, with no installer, public release, clean-machine pass or
production approval.

## Hosted delivery 0.4.41

[Build 43550712713](https://github.com/SKang393/Graph-auto-reader/actions/runs/43550712713)
completed from `a7f7394cd72c6777ae71c27a12d185f401548dca`. The integrated
preview adds reviewed marker batches with one Undo/autosave, plot-boundary
examples, and weighted matching that reduces connection-context interference.
Hosted App/Domain/integration checks passed 1,094 tests with seven opt-in skips.
[Source CI 43550580261](https://github.com/SKang393/Graph-auto-reader/actions/runs/43550580261)
passed 2,610 .NET tests with 30 skips across 13 projects, ten Rust tests, and
packaging/evidence/localization validation. Public release jobs were skipped.

- [Artifact 14096875686](https://github.com/SKang393/Graph-auto-reader/actions/runs/43550712713/artifacts/14096875686):
  87,254,531 bytes, SHA-256
  `4694900154956250e9fb5a851bd2827d770b9b9b8f7b30b73c85707e24289dcb`.
- Inner ZIP `GraphAutoReader-0.4.41-win-x64-internal-preview.zip`:
  87,511,587 bytes, SHA-256
  `ae26e86e86f26ce56b19aa8af335c52c74fdd8f14e95ab1d93266f97039d2bf0`.
- Extraction `0.4.41-20261114T032017592Z-a7f7394c`: 477 files,
  196,034,695 bytes. Every extracted entry matched its ZIP bytes; safe-entry
  validation passed. Artifact expiry is 2027-02-12.
- Receipt SHA-256: `7afefd508d4026f51ff71e009580ce6cb78274f681b6e67dab7d74ff9914ed30`.
  Build-info SHA-256: `0954d50120a9834a61f379cd7b3df4a3c49ed419d0eddd88e9a0d690f4733d97`.
  Executable SHA-256: `a05c8a50ee57a2ac56d4570ba3e09313e660c54848e960d8ed688a7f2473cef3`.
  App DLL SHA-256: `f7253dd9d529f7f8b8f192727c07d15be33d454ab9336f13658378f731c45b0e`.
- Rust, reviewed OpenCV, OCR reader/manifest and all required notices retained
  the verified identities above. Isolated ManualPreview smoke exited 0;
  unavailable Production smoke exited 2. No normal window was opened.

The existing diagnostic binary ran against the downloaded package's App and
native DLL. Horizontal, changing-slope and hollow controls each returned 4/4
expected matches, with no false proposals; the marker-free remainder control
returned none. Wall times were 750.783, 628.339, 829.131 and 724.837 ms.
The near-limit 2,560 x 1,600 control returned two centers with zero pixel error
in 1,042.365 ms; restart took 1,113.021 ms. The exact observed child cancelled
and exited in 17.140 ms. Both probes exited 0. Runtime reported algorithm
`original-panel-user-exemplar-gaussian-single-scale-v2`, one OpenCV thread,
BelowNormal priority and the verified 416/10000 CPU job budget on 12 logical
processors (0.4992 core). These are owned execution controls, not representative
accuracy or native UI acceptance. Shape and near-limit JSON hashes were
`d7d817635c0c4b55f4d23b47316b4f15cb6b30aad450931d192cabb3c21e71ba`
and `2440aafc0e886e6616cf9a0371bd344cec081fb7dc64a866ee70d6793a61c697`.

After verified ledger/outcome `000b6118f44059c2914c95028b53e073f4d4bb3f` reached
main, the latest pointer was atomically switched to 0.4.41. Contained,
reparse-free cleanup retired 487 files totaling 370,799,420 path bytes:
the superseded 0.4.40 build/ZIP, outer download, receipt, isolated smoke/probe
scratch and pointer backup. One extracted build and one ZIP remain. Shared user
Data, the current hard-linked OCR input (hash rechecked), and the earlier QA App
and its dependencies were preserved. The isolated compiler output is reused
for the active Prepare/crop implementation rather than copied to another build
tree. Although 441 is a release-eligible ordinal, this is an internal ZIP only:
no tag, installer, public release, clean-machine pass or production activation.
Native device, calibration and marker/batch interactions remain waiting under
REV-011 after the user stopped Computer Use.

Subsequent source CI `43554110711`, job `132322948894`, stopped in
`Test-VersionPolicy.Tests.ps1` before building: the 441 ledger incorrectly set
`releaseEligible: true`. The current policy distinguishes cadence eligibility
from public eligibility before 2.0.0. Corrected that metadata to
`releaseEligible: false, cadenceEligible: true`; application bytes and release
policy are unchanged. The existing version-policy suite then passed all 62
checks in 30.806 seconds using contained disposable test repositories. The
temporary log under `artifacts/version-policy-check` can be retired after this
record is pushed. No failed application tests are inferred from that early CI
failure.

## Hosted delivery 0.4.42

The manual Prepare/crop increment is packaged from
`0558b3fad288a623d49f6e898646083a365f8fd1`. It adds explicit crop preview/create,
retained original-source lineage, nested image/PDF reopen, a parent-close guard,
and an actionable size limit before marker assistance. See the
[source and workflow record](PANEL-CROP-WORKFLOW-20261114.md).

- Hosted run `43554111402`, job `132322954732`, succeeded. App 944, Domain 23,
  Integration 143: **1,110 passed, 7 skipped, zero failed**.
- [Source CI 43554461132](https://github.com/SKang393/Graph-auto-reader/actions/runs/43554461132),
  job `132324049567`, succeeded on `47debc19e50e9f282279cd8f2c042d0fdad8e725`:
  **2,626 .NET tests passed, 30 skipped, zero failed**, Rust 10/10, and 68
  packaging checks passed. This source differs from the package commit only in
  the two ledger/outcome documents; application files are identical. Public
  release jobs were skipped. Synthetic packaging fixture warnings are separate
  from the application build, which had zero warnings and errors.
- [Uploaded artifact 14097702300](https://github.com/SKang393/Graph-auto-reader/actions/runs/43554111402/artifacts/14097702300)
  is 87,267,519 bytes, SHA-256
  `0b95cd0e125ec1c0675101234c6f1abc438720ed22b9c847c304a2df9f4a312f`.
  Created 2026-11-14T04:01:29Z; expiry 2027-02-12T03:56:05Z.
- The downloaded inner ZIP is 87,524,277 bytes, SHA-256
  `ddd0c8dd0219c1a2d6b0b59f8b177e529905ffc22d9aa97b22cda6a1f9a3b4d3`.
  Its 477 files total 196,068,999 bytes. Every safe archive entry matched the
  staged extraction. Receipt SHA-256:
  `a7b322d8a00339212a2b3385e3a0aaa807fe3545a6f03ced80b9e5a35b18038a`.
- Build time 2026-11-14T04:01:00.3170764Z; build-info SHA-256
  `30694117909e5d157987b4bf8489fd339aa604ed33d3081a8113f36b9a3f86a8`.
  EXE `da6618656fc0f9f2c02f91c241f42e45081c49b277b9fb9a9d22572354f4e142`;
  App DLL `0878c2a5e9ee3fcd9261fd0757240374d55d50df643b001be91fa3c852aea26a`.
- Rust, reviewed OpenCV, OCR reader/manifest and required notices match the
  verified identities above. Isolated ManualPreview smoke exited 0; unavailable
  Production smoke exited 2. No normal application window was opened.

The diagnostic output was reused with all 12 managed GraphReader libraries
copied from this verified package and hash-checked. Its native workers and OCR
assets came directly from the staged package. The actual prepared-crop workflow
passed in **5.291919 seconds** process wall time (4.731774 seconds pipeline):
39.9916 ms crop, 2323.4831 ms OCR and 205.7483 ms marker work. Three accepted
points exported and reopened, parent pixels stayed unchanged, and saved points
and anchors retained exactly one original-source offset. CSV hash stayed
`a5cab504b91b88ae3c3f6ff976e298cc3d75e47139102400dc64c0e335635302`;
the packaged workflow JSON hash is
`3a2532b3c0a2e61590ee9883734a2827e8c0ea137b6d8c3d966b6b953fa3a1de`.
This is owned-input execution with simulated explicit review, not representative
accuracy, native interaction or production approval.

Native checks remain waiting after the window-list tool reported an Escape stop
immediately after the maintainer authorized resumption. No subsequent native
inputs were issued. No installer, public release, tag, clean-machine acceptance
or production activation is claimed. After the ledger and outcome reached
`origin/main` at `0cfecd315adf1793a80477d44f23ab6ddc7a17a9`, the latest pointer
was switched to 0.4.42. Guarded cleanup retired the superseded 0.4.41 directory
and ZIP, its pointer backup, and the completed `artifacts/hosted-442-check`
and `artifacts/panel-crop-check` directories. A subsequent filesystem check
confirmed all five targets absent, exactly one 0.4.42 build directory and one
87,524,277-byte ZIP present, and the pointer bound to the verified executable.
The cleanup command's output was lost during context rollover; no exact deleted
file/byte total is claimed. Shared user Data and active QA/model dependencies
were excluded. The 33-byte version-policy log
was retired after its tested correction record `47debc19` reached main.
The new 442 ledger entry passed the existing 62 version-policy checks in
52.128 seconds. Its public and cadence eligibility are both false, derived
directly from `VersionPolicy.ps1`. Future hosted builds now consume the verified
442 artifact identity.

## Hosted delivery 0.4.43

The calibration performance and geometry increment is packaged from
`bf0d0a56946abc01e889605fbaaecabb4db5d177`. It includes the source-bound export
receipt correction, preserved-sampling OCR padding reduction, measured-axis
continuation repair, varied Auto device workloads and crop recovery guidance.
See the [owned source checks](OCR-PADDING-AND-AXIS-CONTINUATION-20261114.md).

- [Hosted run 43563434224](https://github.com/SKang393/Graph-auto-reader/actions/runs/43563434224),
  job `132351960986`, succeeded: App 957, Domain 23, Integration 143,
  **1,123 passed, seven skipped, zero failed**.
- [Source CI 43563433567](https://github.com/SKang393/Graph-auto-reader/actions/runs/43563433567),
  job `132351955194`, succeeded on the exact package source: **2,644 .NET tests
  passed, 30 skipped**, Rust 12/12, 68 packaging checks and 62 version-policy
  checks. Application compilation had zero warnings/errors. Release jobs were
  skipped.
- [Artifact 14101760460](https://github.com/SKang393/Graph-auto-reader/actions/runs/43563434224/artifacts/14101760460)
  is 87,270,127 bytes, SHA-256
  `f96b67237afd1a54146e6b983de678890a06c250e0c266ebe7ebbff55fddc2b1`.
  Created 2026-11-14T05:54:38Z; expiry 2027-02-12T05:50:19Z.
- The downloaded inner ZIP is 87,527,242 bytes, SHA-256
  `77ca2f1207d6430b9fe600dbe1cfe5143fee8ee5e549865391362da52198c6a8`.
  All 477 safe archive entries matched extraction, totaling 196,076,167 bytes.
  Receipt SHA-256: `ed39976c29d70cd60dd75958022a078dcaad0249b4f1ecd5f86e3b5891d612d7`.
- Build time: 2026-11-14T05:54:22.3594443Z. Build-info SHA-256:
  `c24df00cf10ac968717637d14f2b58f853a8a66be48371120a86a6bc0f7b86af`.
  EXE: `8c012440a7a433aff98e5df3cab33a760544b024d920f92cc326ac07d45f7778`;
  App DLL: `86bb16ae0d89c0b12230d009a657120d5430a81992f710aa881ac3d18dc5bd6c`.
- Rust: `4dfab71143dc8d448f8daf4cd6c537c4d98de7573b1faa3139bd8abb85c6cbe4`.
  Its toolchain metadata/notice, reviewed OpenCV and unchanged OCR reader,
  manifest, license and notice identities were verified. Isolated ManualPreview
  smoke exited 0 and unavailable Production smoke exited 2.

After all 21 package libraries were restaged and hash-checked in the existing
probe output, the packaged continued-axis control returned six paired ticks and
the expected 1-to-3 session / 0-to-10 value calibration. It took **3.9091576 s**
process wall time, 3.3844652 s pipeline time, with empty stderr and exit 0.
No calibration was applied. Result SHA-256:
`5df87cd5ce0e4dc04b8cee84c56ba5fa521921f5840c952cd0a4f7220c9d8a78`.
This is one owned execution control, not representative accuracy or native UI
acceptance. Native interaction remains waiting under REV-011. No installer,
public release, tag, clean-machine pass or production activation is claimed.

The ledger makes 443 the sole retained identity and the next hosted workflow
uses this verified artifact. After this delivery record is pushed, switch the
latest pointer to `0.4.43-20261114T055422359Z-bf0d0a56`, then retire the old
442 build/ZIP and completed `artifacts/hosted-443-check` and
`artifacts/ocr-padding-check` scratch after their current verification readers
finish. Preserve shared user Data, current runtime/model dependencies and the
separate active native QA App. No replacement package is needed for diagnostic
pin or documentation updates.
The new 443 ledger entry also passed the existing 62 version-policy checks
locally, with both public and cadence eligibility derived from the policy and
false. Temporary policy repositories stayed inside the delivery scratch.
