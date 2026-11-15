<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Owned digit orientation check retention

## Completed output scope

The finished owned orientation reproduction, actual-reader control, and App
integration output occupy:

```text
artifacts/owned-digit-orientation-check
231 files, 145,252,904 bytes
SHA-256 manifest: f310fc711089ca5df5b27d5906f3639df77b064233515f60dd99ddc74ad72880
```

The source and outcome are pushed in
[`CALIBRATION-OBSERVED-ORIENTATION-20261115.md`](CALIBRATION-OBSERVED-ORIENTATION-20261115.md)
at `b6f14f5d8b34b7b3979f61d36332967cf146f222`; its Windows CI run
`43660888089`, job `132649733800`, succeeded. The record reports 85 OCR tests,
the owned actual-reader control (no literal-reading gain demonstrated), and the
200-test App calibration/marker integration. It preserves the limits and
identities needed to understand the change without compiler outputs, test
results, or runtime copies.

Exact-name source search found no live source, runtime or packaging consumer of
the output directory. Remaining references are the pushed outcome's
reproduction commands and the probe README's output-directory recipe. Those
commands rebuild the output and do not depend on this existing run. All owned
inputs are generated in memory; the target contains zero image files.

The new Mirage comparison at `artifacts/mirage-comparison-20261115` is active
and changing. The retirement helper checks lexical path disjointness but does
not enumerate, snapshot, read, or remove that directory. Its retained 0.4.51
baseline package and `artifacts/corner-tick-worker-451-check` output are explicit
protected keepers.

## Guard and status

`artifacts/storage-retention-check/retire-owned-orientation-check.ps1` is an
exact-scope helper. It required a pushed scope commit
containing this record and the source outcome; verifies the exact inventory,
all ancestry/descendant reparse conditions, a normal-host no-consumer process
scan, and the 0.4.51 package/latest identity; and snapshots shared `Data`,
native QA folders, PNG fixtures and the corner-worker diagnostic before and
after any future removal. It fails closed if CIM is unavailable. The Mirage
comparison is checked only for nonoverlap, not snapshotted while active.

After scope commit `a6fcee849978f71e212b15130654748b0ebee16e` was pushed,
`ValidateOnly` passed with no process consumers and eight protected keepers.
The separately reviewed `Execute` completed at `2026-11-15T01:26:46.0238083Z`,
removing exactly 231 files / 145,252,904 bytes. The receipt confirms the target
is absent, the active 0.4.51 build is unchanged, and shared `Data`, native QA,
corner-worker output and referenced PNGs are unchanged. The active Mirage
directory remained disjoint and untouched.

Receipt: `artifacts/storage-retention-check/owned-orientation-retirement.json`,
SHA-256 `a789b0cf7c97d82c22c40d257671a5e00539140a6caaac1a2fc9911ed84c5d0f`.
Helper SHA-256:
`6d677be796816cb51b9c8b6fae2ae5f03ad162bbab7309e3f0aafbdd2f8248b`.
This retirement is complete; do not rerun the helper.
