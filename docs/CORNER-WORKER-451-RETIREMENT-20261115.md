<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Corner worker 451 diagnostic retention

## Scope prepared for retirement

**Current status:** retained as an active baseline consumer for the authorized
Mirage paired comparison. The inventory below describes the earlier completed
451 diagnostic, not a current deletion authorization. Do not execute the old
helper while the comparison uses this staging directory. Its orientation
keeper guard is also obsolete because that separate output was subsequently
[retired](OWNED-ORIENTATION-RETIREMENT-20261115.md). Refresh the exact inventory,
consumer checks and guards only after the new consumer finishes.

The completed, isolated diagnostic output is:

```text
artifacts/corner-tick-worker-451-check
64 files, 42,716,164 bytes
SHA-256 manifest: cd53344da20475fe9c774c32b2aefbf5be44e73a661693393f7cc5f5b8380173
```

This directory held the probe-only build, staged runtime, process logs and
aggregate result for the packaged 0.4.51 bottom-corner worker control and its
prepared-calibration follow-up. Their pushed outcomes are
[BOTTOM-CORNER-WORKER-451-DIAGNOSTIC-20261114.md](BOTTOM-CORNER-WORKER-451-DIAGNOSTIC-20261114.md)
and
[PREPARED-CALIBRATION-451-DIAGNOSTIC-20261114.md](PREPARED-CALIBRATION-451-DIAGNOSTIC-20261114.md).
The corresponding outcome commits, `308d19e6eda2cc8516026c08bd46fb7c5969a241`
and `5e40e89bd3f311832111e34a6b44b70cee2d2dfc`, are ancestors of
`origin/main` at `7bc0dd6b361c7e5e3c64ee115a0911e279872dfa`.

The packaged worker control passed its two owned width variants. The separate
fixed prepared-calibration aggregate recorded 120 projects, 50 geometry/OCR
panels, 367 numeric rows and zero complete proposals. These records preserve
the relevant results and limitations; the generated probe/runtime copies and
logs were no longer active inputs when this scope was prepared. The later
Mirage comparison now reuses this directory. The original exact-name search found no source, runtime or
packaging consumer. Remaining mentions are historical outcome/setup prose in
the two outcome docs, the probe README, and the later 451 staging-retirement
note. The README commands describe how to regenerate the diagnostics; they do
not require the old output directory to exist.

The independent upright-digit orientation check lives under
`artifacts/owned-digit-orientation-check` and is explicitly outside this scope.
Its completed evidence has since been pushed and its disposable output retired.

## Guarded helper and preserved inputs

`artifacts/storage-retention-check/retire-corner-tick-worker451.ps1` is a
nonexecuted, exact-path helper. It requires an explicit pushed scope commit,
verifies that both outcome commits and the retirement record are in that
history, checks the exact inventory and absence of reparse points, and refuses
to proceed if normal-host CIM cannot establish there is no consumer. It also
checks the 0.4.51 latest pointer and package hashes, sole retained build
directory, and snapshots of shared `Data`, native 449/451 QA outputs, referenced
PNG fixtures, and the separate orientation-check output before and after any
future retirement.

No deletion has been executed by this record. Root must run `ValidateOnly` in a
normal host, review its result, then explicitly choose `Execute` with the same
pushed scope commit. The helper records a small receipt only after removal and
keeper verification. The 0.4.51 package remains the sole retained build. No
private corpus, user projects, source fixtures, model assets, or active
orientation-check output is in the target.
