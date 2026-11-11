<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Internal preview artifact delivery

## Build 437 preparation

The next internal ManualPreview is `0.4.37`. It packages the native-QA repairs
from `0641f5c6`: an explicit safe-default discard decision before closing
unsaved work, and readable graph headers with complete overflow layout and
keyboard navigation. The observed unsaved-close loss path makes this a
meaningful repair to the current preview. Build 436 and shared Data remain
active until the replacement's package verification, upload and ledger push
complete. This is not a production activation or public release.

## Upload mechanism

The existing Windows workflow uploads only release-eligible pairs. A new
manual-only workflow can upload one locally built, reviewed ManualPreview ZIP
through a dedicated one-job runner, without moving private native attestation
data to hosted CI. It neither builds nor publishes a release. Repository admin
permission was confirmed, and no registered runner existed at that check.

`internal-preview-upload.yml` accepts an explicit runner label, source commit,
version and ZIP checksum. It runs only from `main`; the package source commit
must exist in the checkout and be an ancestor of the workflow commit. This
allows a verification-only workflow correction to upload the already-built,
immutable package without rewriting its build metadata or rebuilding it. The
checkout uses full history for that ancestry check. Both actions reuse the
existing pinned checkout/upload revisions. The local package directory comes only from the runner process's
`GRAPHREADER_INTERNAL_UPLOAD_ROOT`, not from dispatch input. It must contain one
ZIP named `GraphAutoReader-<version>-win-x64-internal-preview.zip`.

`Test-InternalPreviewUpload.ps1` copies the exact hash-matched ZIP into runner
temporary staging, validates safe archive names, rejects user Data, projects,
source/debug/key files and private provenance, and binds ManualPreview metadata
to the requested version and source commit while requiring that commit to be
ancestral to the workflow checkout. It permits root .NET
`System.Private.*.dll` runtime assemblies while continuing to reject private
directories and provenance entries. It also binds actual application/native
bytes. Required
portable and application entry files must exist. Native provenance/review flags
must be present while clean-machine and release flags remain false. This checks
recorded metadata and package consistency; it does not replace the source
builder's native review or runtime smoke.

Only that staged ZIP is uploaded. Requested artifact retention is 90 days;
record the server's actual artifact ID, URL, digest and expiration in the build
ledger and replace or refresh the current artifact before expiry. No push-triggered
build, version bump, release tag, public release, or private-attestation upload
is part of this workflow.

## Verification and delivery boundary

`pwsh -NoProfile -File packaging/tests/Test-InternalPreviewUpload.Tests.ps1`
and the same test under Windows PowerShell both passed. Synthetic cases cover
matching bytes, wrong package/source identities, ancestor and non-ancestor Git
commit relationships, accepted root `System.Private.*.dll` assemblies,
rejected private directories, absent portable mode, invalid version components,
forbidden Data/private/debug content, traversal and reserved Windows names.
YAML parsing, step-shape checks and `git diff --check` passed.
The verifier test is included in Windows CI. No application dependency was added.
The GitHub runner is an MIT-licensed local tool, not an application payload.

## Verified build 436 delivery

Internal ManualPreview `0.4.36` packages the completed per-panel calibration
warning, source-bound save/recovery, and unsaved-close protection changes. Its
application source is `0ffdfa51f7d4db3e141b1bf8968a46792f572267`.
`Build-DevPortable.ps1 -FastTestsOnly` ran from the clean isolated checkout with
the existing reviewed OpenCV evidence and explicit private attestation path.
It completed in 142.067 seconds: 806 application, 23 domain, and 141 integration
tests passed, with 7 integration tests skipped. Portable smoke exited 0;
the Production-mode probe exited the expected 2 for ManualPreview. The source
checkpoint's Windows CI run `42990061725` also completed successfully.

The package contains 468 files totaling 185,971,166 uncompressed bytes. The
79,867,038-byte `GraphAutoReader-0.4.36-win-x64-internal-preview.zip` has SHA-256
`c751d6d9953dd7f60a0c8480798646a8e228b5dc265966abd619d682034ea2ce`.
The original verifier rejected the standard `System.Private.CoreLib.dll` name;
the corrected verifier and both PowerShell regression runs passed without
changing or rebuilding the ZIP.

The manual workflow ran from `b53bc76d8e027e6ec4d6e28026b2a001317928de`,
which contains the package source as an ancestor. Run `42992048647` verified
and uploaded exactly one ZIP. [Artifact 13841873219](https://github.com/SKang393/Graph-auto-reader/actions/runs/42992048647/artifacts/13841873219)
was downloaded again and matched the original package SHA-256 and size.
Its server expiration is `2027-02-09T09:04:11Z`; replace or refresh the current
artifact before that date. The server archive digest is recorded separately
from the enclosed package hash in `BUILD_LEDGER.json`.

The task-specific Actions runner 2.338.2 handled one job, exited, and
automatically deregistered. The repository runner count returned to zero.
Its downloaded tool ZIP matched the published SHA-256
`a1f17aa987fc8158bc7521d284912267a0d01b7c2c4684b33c86783b5e8221cc`.

After ledger commit `890f8ff41fc156fb33c8d54416a7987f3da17dd7` reached
`origin/main`, the live pointer switched to
`artifacts/dev-portable/builds/0.4.36-20261111T085128044Z-0ffdfa51`.
The moved executable and retained ZIP hashes matched the ledger, and a hidden
portable smoke from the final directory exited 0. Hashes of all 12 existing
shared Data files were unchanged by the switch; the smoke used disposable Data.

The superseded 0.4.33 directory, stopped one-job runner, and two verification
ZIP copies were removed after exact-path and no-reparse checks: 5,851 files,
848,939,034 bytes. The old build had no embedded Data or project data files.
One 0.4.36 build directory and its ZIP remain under `artifacts/dev-portable`.
Existing shared Data, feedback, and manual-evidence directories were preserved.
The completed delivery staging (20 small files, 439,243 bytes) and isolated
build checkout (6,323 files, approximately 541 MB including compiler outputs)
are disposable after this closeout record is pushed. Their source and measured
outcomes are retained in Git and the remote artifact; neither is an OCR input.

This is an internal manual preview. A later
[native QA session](MANUAL-PREVIEW-NATIVE-QA-20261111.md) checked manual
calibration, point editing, Review, Export preview and autosave, and reproduced
tab readability and main-window close-protection defects. Native CSV writing
and explicit save/reopen remain unverified. Automatic stages remain unavailable;
clean-Windows VM, installer and production-model acceptance are not established.
No public release or tag was created. Follow-up source fixes do not modify the
immutable 0.4.36 package.

For an isolated build checkout, `Build-DevPortable.ps1` now accepts
`-OpenCvAttestationPath` alongside `-ReviewedOpenCvEvidenceRoot`. This forwards
the already existing private file to the review validator without copying it
into the preview. Both the isolated installer fixture and this actual preview
builder passed the existing source-audit and private-review validators.
