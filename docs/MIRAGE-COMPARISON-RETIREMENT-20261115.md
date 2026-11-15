<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Mirage comparison scratch retirement

## Closed diagnostic scope

The fixed Mirage comparison and its owned controls are complete. Their pushed
records are [comparison outcome](MIRAGE-COMPARISON-20261115.md),
[paired-pipeline evidence](MIRAGE-PAIRED-PIPELINE-20261115.md), and
[Windows adapter provenance](MIRAGE-WINDOWS-ADAPTER-20261115.md). They record
the fixed 120-project development pass, aggregate-only output, zero sealed
reads, and the decision to retain the 0.4.51 baseline. They do not claim
accuracy or production approval. Authored adapter source remains in
[`tools/GraphReader.CalibrationOcrProbe/mirage_windows`](../tools/GraphReader.CalibrationOcrProbe/mirage_windows/README.md).

Three ignored output directories are scoped for retirement after these records
and this scope are pushed and the lead releases all readers:

| Exact relative path | Files | Bytes | SHA-256 inventory manifest |
| --- | ---: | ---: | --- |
| `artifacts/mirage-comparison-20261115` | 46 | 24,450,950 | `aca8a3ec0eac210d2031377a39838f44c530aed46952fbdcfda0cd3bfcc50ac6` |
| `artifacts/mirage-paired-451-check` | 10 | 10,737 | `167c41da08ff1ec9c650d6a4e15b22cc704e2c409008fceb95fef36d5f42392c` |
| `artifacts/corner-tick-worker-451-check` | 64 | 42,848,968 | `d0afa6313bde2295f1915d5b4af7b77e95316e29a75e8498caa1f9c69f50f7ea` |
| **Total** | **120** | **67,310,655** | individually pinned above |

All three inventories were checked without following links; no reparse points
were found. The corner-worker inventory is a fresh snapshot. It supersedes the
older 42,716,164-byte snapshot in the prior temporary-retention note; the
prepared helper requires this exact newer manifest. No deletion has occurred.

The comparison route's source still names its run-specific asset/output root,
and the owned control reads the copied 600-by-400 fixture there. These are
closed diagnostic setup requirements, not an application, CI, or active run
consumer. A future comparison needs a new authorized setup and regenerated
scratch. Historical setup references in the probe README and prior outcomes do
not require those generated directories to remain. The C: handoff originals
and the original 1200-by-1700 owned source stay outside the deletion scope.

## Protected keepers and guard

`artifacts/storage-retention-check/retire-mirage-comparison.ps1` is prepared
but has not been executed. It accepts only `ValidateOnly` or `Execute` plus an
explicit scope commit. It checks that the scope and all three outcome documents
are present byte-for-byte in the supplied commit on `origin/main`, and that
the pushed ledger still retains only 0.4.51. It pins each directory's file
count, byte total, and sorted path/size/SHA-256 manifest; checks literal
containment and every ancestor/descendant for reparse points; and requires a
successful normal-host CIM scan with no process referencing a scoped target.
The CIM scan in the preparation sandbox was denied, so consumer absence is not
claimed here; the helper fails closed until the lead can run it on a host with
CIM access.

Before and after any future execution it snapshots and verifies the sole 451
build, executable, build metadata, ZIP and latest pointer; shared `Data`; native
449/451 QA projects and their referenced owned PNGs; the authored Mirage helper
sources; the exact external C: handoff file set; and the original 1200-pixel
synthetic PNG. The image keepers are pinned to SHA-256
`6f1e161799715d031cd8391263df264030dec2a1baecf3b56fff8ea146c00ca2` (600 by
400, C: handoff) and
`75edb079ccfa8a3a2806bef467ec2dd10d13905bee762fd48ee9be21361be98d` (1200 by
1700, `artifacts/goal19-realesrgan/public-synthetic/seed-393/images/`). The
sole 451 ZIP is pinned to SHA-256
`6bf48f3f16513a2c3766381d43ad47b8e65406e9499663c3ecde31319c31bd44`.

No private pixels, sealed payloads, current application model assets or tracked
source files are part of this retirement. Rejected Mirage downloads and copied
diagnostic helpers are disposable; their authored source is preserved. Do not
run `Execute` until the outcome and
scope are pushed, all run/document readers are released, and a successful
`ValidateOnly` result has been reviewed. The helper will write a compact
receipt only after the exact targets are absent and every keeper still matches.
