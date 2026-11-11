<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Internal preview artifact delivery

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
The verifier test is included in Windows CI. No dependency was added; the future
GitHub runner is an MIT-licensed local tool, not an application payload.

Actual delivery remains pending. After a meaningful checkpoint passes, prepare
and commit the next version, build into an isolated replacement staging root,
validate and upload its exact ZIP, then push the ledger before changing the
live pointer or retiring the old build. Preserve user Data. This batch did not
build a product package, install/register a runner, upload bytes, or change the
current build identity.

For an isolated build checkout, `Build-DevPortable.ps1` now accepts
`-OpenCvAttestationPath` alongside `-ReviewedOpenCvEvidenceRoot`. This forwards
the already existing private file to the review validator without copying it
into the preview. The actual reviewed-native installer flow passed an isolated
fixture with the external path; the package-producing builder has not yet run.
