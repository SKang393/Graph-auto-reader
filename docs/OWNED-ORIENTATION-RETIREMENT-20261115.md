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
exact-scope helper, prepared but not executed. It requires a pushed scope commit
containing this record and the source outcome; verifies the exact inventory,
all ancestry/descendant reparse conditions, a normal-host no-consumer process
scan, and the 0.4.51 package/latest identity; and snapshots shared `Data`,
native QA folders, PNG fixtures and the corner-worker diagnostic before and
after any future removal. It fails closed if CIM is unavailable. The Mirage
comparison is checked only for nonoverlap, not snapshotted while active.

No deletion has been performed. Run `ValidateOnly` after this record is pushed,
review the result, then separately choose `Execute` with the same pushed scope
commit. The sole retained 0.4.51 build and all named inputs remain outside the
target.
