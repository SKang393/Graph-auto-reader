<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Candidate evidence binding

The release builder previously required installer, portable, and pair-parity
evidence before emitting the pair needed for those observations. Explicit
candidate mode now defers those three gates and verifies the unchanged pair
after reviewed evidence is recorded. Other production prerequisites remain
mandatory. OpenCV/PDFium candidate-specific evidence has a separate creation
and commit-binding cycle that is not resolved by this batch.

`packaging/Test-CandidateEvidence.ps1` checks a reviewed, tracked receipt
against existing schema-1 release metadata and actual installer, portable,
and common-publish bytes. It checks every common file's path, size, and hash,
the existing sorted manifest digest, and the package/shared-payload identities.
The candidate source commit is separate from the later evidence-record commit.
The embedded audit must match the candidate source, and only the status,
evidence, and notes of the three named post-build gates may change in the later
tracked audit. Other source prerequisites must remain passing and unchanged.

The record requires a named approved review, each required distribution
observation, matching VM UUID/OS build/UBR, UTC installation and observation
times within 24 hours, QEMU and ISO identities, and the candidate source,
version, and executable. Per-gate reports are tracked and checksum-bound to
the candidate, VM, observation time, and harness at the candidate source commit.
These checks validate recorded claims, not actual guest execution or reviewer
identity. Full artifact validation and final release approval remain separate.
The default release path remains strict. The checker always reports
`releaseApproved=false` and `published=false`. Explicit synthetic-fixture mode
produces a distinct fixture result and does not relax provenance validation.

## Verification and limits

`pwsh -NoProfile -File packaging/tests/Test-CandidateEvidence.Tests.ps1`
passed using isolated synthetic fixture repositories and worktree-local
temporary files. Cases include matching records, altered packages or common
files, metadata size/shared-hash changes, false observations, missing review,
wrong source/executable/harness, malformed or wrongly typed VM data, stale or
negative guest age, and unrelated audit changes. Early fixture line-ending and
receipt-hash errors were repaired before the passing run. Review found a gap
in the first VM record validator; the observed identities and timestamps above
were added, tested, and re-reviewed without a remaining finding.

The test is now included in Windows CI. No real passing receipt, model
approval, actual package, version change, or audit-gate status was created.
No dependency or licensed asset was added. Fixture directories are removed by
the test harness. See [the command and record requirements](../packaging/clean-machine/README.md).

## Candidate emission and full verification

`packaging/Build-Windows.ps1 -CandidateOnly` performs a fresh common publish,
emits one internal installer/portable pair, and runs full structural checks.
It defers only `installer-clean-windows-vm`, `portable-clean-windows-vm`, and
`release-artifact-pair-parity`; each must be explicitly blocked. Other model,
license, native, workflow, and clean-source checks remain required. The version
must be the next ledger version; `-Force`, reused publish output, and the first
stable `2.0.0` transition are rejected. Output reports `ReleaseReady=false`.

`packaging/Test-ReleaseArtifact.ps1 -CandidateEvidencePath <record>` combines
the evidence checker with full artifact validation. It permits only the
documented audit differences after evidence validation and does not rewrite
either package. Synthetic records require `-AllowFixtureEvidence`, report
`FixtureEvidence=true` and `ReviewedCandidateEvidence=false`, and cannot be
passed through the builder. All verifier modes report `ReleaseApproved=false`.

The focused command
`pwsh -NoProfile -File packaging/tests/Test-ReleaseArtifact.Tests.ps1 -CaseFilter '*Candidate*'`
passed three cases, including an actual fresh synthetic `0.4.36` pair, ordinary
rejection, forbidden-content checks, integrated fixture receipts, immutable
package checks, tamper detection, and version-policy boundaries. A separate
audit case passed first-stable rejection and post-stable `2.0.1` acceptance.
Review found and corrected an initial first-stable candidate loophole.

The broad diagnostic run reported 64 passing and three failing cases. One
failed to create its test junction inside the sandbox and passed its focused
rerun with the required permissions. Two used the old `2.0.0` fixture while the
builder correction was in progress; the revised cases passed in the focused
run above. This was a mixed-revision run, not a clean full-suite pass. Fixture
restore disabled NuGet auditing because the network was unavailable. An earlier
Windows PowerShell hashing failure was repaired with stream-based SHA-256.
`git diff --check` passed. The diagnostic log
`artifacts/packaging-suite-candidate.log` is disposable after this outcome is
pushed; fixture directories are removed by the test harness.

No real candidate, production evidence, gate-status change, or public-release
promotion occurred. Exact native evidence and remaining production
prerequisites still block a real candidate. The checker alone cannot authorize
publication.

## Current application process smoke

The previously compiled `f96db033` source was also exercised with
`GraphReader.App.exe --portable-smoke` in ManualPreview mode. A temporary
`portable.mode` sentinel and an isolated `GRAPHREADER_DEV_PORTABLE_DATA_ROOT`
kept mutable files inside the task worktree. Runtime/model override variables
were cleared for the child and restored afterward; the sentinel was removed.
The process exited zero in 1.807 seconds and created `Settings`, `Cache`,
`Logs`, `Autosave`, and `Recovery`. Executable SHA-256:
`d56b43ec423d04b65a442e6bbc877307189058f77c64e86eddd617f9c5d6fcfc`.

This is startup, WPF construction, and portable-path evidence from routine
compiler output, not a packaged build or visual inspection. The smoke exits
before import and cannot exercise automatic calibration in ManualPreview.
App and Integration tests remain the evidence for warning behavior. Its
task-owned `artifacts/runtime-process-smoke-20261111` files and completed
compiler/evidence outputs are disposable after this record is pushed and
their active consumers finish.
