<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Candidate evidence binding

The release builder currently requires installer, portable, and pair-parity
evidence before emitting the pair. Component-only OpenCV/PDFium staging does
not produce that pair. A complete candidate creation and verification path is
still needed. This batch supplies its independently usable evidence checker;
it does not claim to resolve candidate emission.

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
The default release path is unchanged. The result always reports
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

Candidate creation and integration with the complete artifact verifier are
unfinished. The checker alone cannot authorize package emission or publication.

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
