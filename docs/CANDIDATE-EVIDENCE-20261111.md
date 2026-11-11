<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Candidate evidence binding

The release builder previously required installer, portable, and pair-parity
evidence before emitting the pair needed for those observations. Explicit
candidate mode now defers those three gates and verifies the unchanged pair
after reviewed evidence is recorded. Other production prerequisites remain
mandatory. A separate frozen-publish handoff now addresses OpenCV/PDFium's
creation and commit-binding cycle without rebuilding observed application bytes.

`packaging/Test-CandidateEvidence.ps1` checks a reviewed, tracked receipt
against existing schema-1 release metadata and actual installer, portable,
and common-publish bytes. It checks every common file's path, size, and hash,
the existing sorted manifest digest, and the package/shared-payload identities.
The candidate source commit is separate from the later evidence-record commit.
The embedded audit must match the candidate source, or its separately validated
native-evidence commit for a frozen publish. Only the status, evidence, and
notes of the three named post-build gates may change in the later tracked
audit. Other prerequisites must remain passing and unchanged.

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

After `63623c86` reached `origin/main`, that completed diagnostic log was
removed (one file, 32,527 bytes). The earlier application startup smoke was
also retired after its record was pushed (five files, 502 bytes). Active test
compiler outputs remain inputs to the ongoing native-staging work.

No real candidate, production evidence, gate-status change, or public-release
promotion occurred. Exact native evidence and remaining production
prerequisites still block a real candidate. The checker alone cannot authorize
publication.

## Preserve the observed native application bytes

Application informational versions embed the source commit. Rebuilding after
recording native evidence therefore changes the application DLL. Diagnostic
preparation now freezes a clean Production publish from source A with a complete
file manifest and independently supplied receipt hash. Preparation creates no
package and records no native approval. Candidate resume copies and rechecks
these bytes after evidence commit B; only the two native reports and their
audit-gate fields may differ from A. Existing typed OpenCV/PDFium installers
must validate the actual reports, native resources, application bytes, and
harnesses before installing approved runtime metadata into the new staging copy.

Metadata records source A and native-evidence B separately. Final evidence C
is limited to the existing three post-build gates and their records. Verification
reruns both native validators against temporary copies of the candidate bytes,
requires a clean checkout, and does not mutate the frozen input or package pair.
The [clean-machine instructions](../packaging/clean-machine/README.md) describe
the exact preparation, resume, and final-verification arguments.

`powershell.exe -NoProfile -ExecutionPolicy Bypass -File packaging/tests/Test-ReleaseArtifact.Tests.ps1 -CaseFilter 'Frozen native A B C handoff uses both typed installers on exact synthetic bytes'`
passed one complete synthetic handoff. It rejects a changed frozen DLL, runs
both actual typed native installers, constructs a synthetic internal pair, then
verifies final evidence and unchanged input/package hashes. Reports and approvals
exist only in the synthetic repositories; no real native library or guest was
validated. Standalone frozen-native and candidate-evidence tests also passed
under Windows PowerShell. PowerShell parsing and diff checks passed, and
independent read-only review found no remaining concrete bypass.

This test exposed and fixed existing integration bugs: the wrong version-object
property passed to native installers, missing native-resource classification,
rejection of the reviewed PDFium `bin` path, separator mismatches, and native
installer console output contaminating the builder's return object. Native file
exceptions are restricted to validated, hash-bound resource paths.

[CI for `63623c86`](https://github.com/SKang393/Graph-auto-reader/actions/runs/42979967868)
passed 2,471 solution tests with 29 optional skips, but packaging reported
66 passing cases and one failure: Windows PowerShell rejected a BOM-prefixed
audit read from Git. Parsing now removes only the leading UTF-8 BOM while raw
hashes remain unchanged. The exact failed case passed its Windows PowerShell
rerun. Explicit parentheses also repaired an OS-update-revision type check
exposed under that host. The next clean CI run remains the full-suite gate.

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
