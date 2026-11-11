<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Development preview launch identity

The development launcher now requires `latest.json` to identify the single
retained entry in the tracked [build ledger](BUILD_LEDGER.json). Directory,
version, source commit, and executable SHA-256 must all match. The executable
must also be inside its declared build directory. Missing or inconsistent
evidence stops launch with a corrective message before starting a process.

This closes a concrete mismatch observed at source baseline `cd1cd4d8`: the
checkout's local pointer named build 433 (`0.4.33`), while the ledger retained
build 435 (`0.4.35`). The earlier executable still existed. Checking its hash
alone therefore allowed an obsolete preview to be presented as current.

The launcher accepts `-RepositoryRoot <checkout>` when copied to a custom
output directory. This locates the canonical ledger and local runtime inputs
without creating another ledger copy. Normal checkout-relative paths retain
their existing defaults. A newly built candidate must be recorded before the
watcher's optional launch can succeed; do not regenerate the ledger from a
stale pointer to bypass the identity check.

## Verification

`pwsh -NoProfile -File packaging/tests/Test-DevPortable.Tests.ps1` passed
12/12 checks. The test process used a writable worktree directory for `TEMP`
and `TMP` and a process-scoped Git ownership exception for its temporary
fixture repositories. An initial run with the sandbox's default temporary
directory failed on an atomic move with access denied; the corrected run
completed. The tests cover matching and mismatched identities, custom output
paths, missing ledgers, nonretained entries, executable tampering, and path
traversal. Rejected-launch assertions verify that no capture marker is written.
`git diff --check` passed. These are synthetic launcher fixtures, not an actual
application or distribution smoke test.

## Remaining artifact reconciliation

This change does not create a package, change a version or ledger entry, or
approve a model. The known build-435 preview worktree, root preview directory,
and remote-testing directory did not contain its executable or ZIP when
checked. The repository's Actions artifact listing was empty. This bounded
search does not establish that every possible copy is absent.

After `edce79b6` reached `origin/main`, the copied local launcher was updated
from the tracked script. Its prior contents were first checked against the
baseline to preserve any independent edits. Invoking its portable-smoke path
then returned the expected retained-build mismatch before starting the app.
The old local pointer still requires reconciliation with a verified retained
artifact. Preserve existing application data and the usable
older executable until a replacement is verified, uploaded, and recorded under
[the retention policy](ARTIFACT-RETENTION.md). Do not invent availability or
overwrite the historical build identities. No dependency or licensed asset was
added. Temporary fixture directories are removed by the test harness; local
validation logs are disposable after this outcome is pushed.
