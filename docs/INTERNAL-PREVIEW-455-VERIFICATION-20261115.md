<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Internal preview 0.4.55 verification

This ManualPreview integrates bounded native geometry search, exact grid-loop
completion, raster endpoint repair, and interval-centered OCR label provenance.
Ambiguous interval clusters remain visible and cannot supply calibration values;
independently separated labels remain available for review. Explicit review and
Apply remain required. No model, threshold, dependency or frozen schema changed.
See [cluster evidence](INTERVAL-CLUSTER-CONTAINMENT-20261115.md),
[grid evidence](GRID-SUBSET-COMPLETION-20261115.md), and
[normal search capacity](NATIVE-CAPACITY-256-20261115.md).

## Source and hosted validation

Source: `991b3f3031ac315e8b93692a15de30fcebaa0b29`.
Full Windows [CI 43755505445](https://github.com/SKang393/Graph-auto-reader/actions/runs/43755505445)
passed all 13 .NET suites: **3,081 passed, 30 skipped, zero failed**. Rust passed
72 tests. All applicable policy, localization, evidence and packaging checks
passed, including 68 artifact-validation controls. Public release jobs skipped.
The earlier mode-localized accessible Name/help/validation and language-refresh
tests are included in the 1,218 passing App tests.

The single [hosted build 43756866661](https://github.com/SKang393/Graph-auto-reader/actions/runs/43756866661)
passed its App/Domain/integration subset: **1,384 passed, seven skipped, zero failed**.
It uploaded [artifact 14179055847](https://github.com/SKang393/Graph-auto-reader/actions/runs/43756866661/artifacts/14179055847)
at `2026-11-15T20:18:37Z`, expiring `2027-02-13T20:13:44Z`.
No local runner, public release, tag, paid service or independent reset was used.

## Download and packaged runtime

Build identity: `0.4.55-20261115T201813466Z-991b3f30`.
Outer artifact: **87,354,031 bytes**. Inner
`GraphAutoReader-0.4.55-win-x64-internal-preview.zip`: **87,611,447 bytes**.
All **477 payload files / 196,315,271 bytes** matched their archive hashes and
sizes. Safe paths, duplicates, link/reparse exclusion, source/private/Data
exclusions, clean source and ManualPreview identity were checked.

| Identity | SHA-256 |
| --- | --- |
| Outer artifact | `5cc2a069e1c5ffcd548426bf029b7b5796afe1321225865a4b9605873a40b9fb` |
| Inner ZIP | `6bc8d3ce70fa5541274b6bfda77708b6687495164dc4b7d91a0237cf43570355` |
| App EXE | `39780bf3f5c982be780d7c16b5e9a6cfcf209c631ba61ec8b6b5e1d0a0ad7b32` |
| App DLL | `b2ac9af7e84463ead7380836270e02639f056f7ca937cb9fcbd4b3694b2d0d90` |
| Build metadata | `3fb76dcf92acbe4d2ed8ccab197feffebc9920fdd0e43be370af74eaac9f528b` |
| Rust worker | `e8bcd29b2f54643883961dd91d5fefb87b3b8c9df33b99ee1a71c0f2c570adf7` |
| Staging manifest | `6ac8953d92cde668276305bd234a2ccb417bda7500f9838bcdff56a0488dd55a` |
| Hosted receipt | `a55bb3eed56b3f35db826d5da95f91f964f92a985dd489ce0ee7a5040729f80c` |

The Rust worker hash matches both receipt and static-CRT metadata. Its standard
library notice hash is `5647be074c8edf7339fd863055923a8fc80bc5610a8d4661ec3b767b9d392c27`.
OpenCV `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`,
native provenance/lineage records, OCR model
`7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`,
OCR manifest `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a`,
review-assistance metadata and required OCR/OpenCV/VC notices match retained
0.4.54 byte-for-byte. A separate read-only review verified the receipt-to-worker
link and the asset/notice comparisons omitted by the initial archive preflight.
Clean-machine evidence, release approval and production approval remain false.
The OCR manifest retains its reviewed Apache-2.0 license and redistribution terms.

| Actual packaged command | Expected / actual exit | Wall ms |
| --- | --- | ---: |
| `GraphReader.App.exe --portable-smoke` | 0 / 0 | 959.6938 |
| `GraphReader.App.exe --production-runtime-smoke` | 2 / 2 | 377.8388 |

Both streams were empty. Production exit 2 verifies refusal of an unapproved
runtime. These single startup measurements are not comparative benchmarks.

## Native owned-pixel workflow

Computer Use launched the exact staged executable. A newly generated 1280 by
960 owned PNG contains unscaled copies of the retained 640 by 480 ticks fixture
at (0,0) and (640,480), rendered on white with System.Drawing SourceCopy.
Its 10,765 bytes hash to
`b5e52b541b060891ca9e11c4866698ad4c5d367ec47e9d090be7ac975679f3f0`.
The unchanged source fixture is
`c1750cab62d79c96b75310cdb5419edcb91a1e99218b248894a26b731aa43dda`.
This new encoding is not claimed byte-identical to the retired 0.4.54 composite.

Import automatically prepared two panels. On the selected second panel,
**Suggest calibration was invoked directly**, with no supplied geometry,
numeric targets or prior candidate selection. It returned six measured pairs:
X 1/2/3 at 104/304/504; Y 10/5/0 at 25/175/325. The UI reported the
AMD Radeon RX 6600 XT GPU and retained review-only text. Draft values were first
session 1, final session 3 and Y maximum 10. Apply was disabled until the review
checkbox was checked, then enabled. Applying recorded sessions 1 through 3;
Undo restored the uncalibrated state and the prior 100/1/20 manual inputs,
with Redo labeled Calibrate axes.

Five retained accessibility states have SHA-256
`63a7e62f4537cef2eaa0a98ee4bcf41ef02fc353d2b31b9471b279f10056da2c`.
The immediate Apply capture preceded UI settling; the subsequent settled state
confirms Undo availability. A separate read-only review found no contradiction.
An initial client call used a nonexistent window-object method, and a dialog
element-index click reported a stale cached element. Refreshing state and using
the visible filename field completed import. These were automation plumbing
errors, not application failures. Closing completed normally; a state request
during asynchronous closure had no foreground process. The next window listing
confirmed no GraphReader windows.

At `2026-11-15T20:28:46.8909219Z`, all 477 payload files remained unchanged,
the 16 shared user Data files remained byte-identical, and no staged runtime
consumer remained. Four task-created Data files totaling 8,682 bytes were moved
to the owned QA directory outside the package. The input hash remained unchanged.

This check does not exercise ambiguous interval warnings, Save/export, marker
editing, the installer, a clean Windows host or real-data accuracy. The new
ambiguity guard is covered by owned automated tests and the fixed development
trace, not inferred from this simple native control.

## Development blockers and retention

The existing fixed development trace has 144 detected regions, 94 literal
numeric rows and 85 associated rows (39 X, 46 Y; 20 estimated) across eight
OCR-returned panels. Four other panels lack geometry. All eight OCR panels
retain a calibration reason. Panel-local distinct reason counts overlap:
insufficient pairs 4; unpaired labels 3; invalid range 2; nonmonotonic 2;
inconsistent spacing 2; observed zero required 2; ambiguous association 1;
duplicate association 1; invalid numeric label 1. The two printed-X drafts
fail the session-range requirement; three Y-only drafts preserve an independently
valid Y axis while X remains blocked. These counts establish no new source
defect and do not justify relaxing association or validation bounds. No private
inference was repeated to obtain this aggregate breakdown.

The [owned prefix measurement](OWNED-OCR-UI-PREFIX-20261115.md) did not establish
a material UI delay, so no speculative threading change was made.
The current geometry diagnosis instead concerns three bounded-search refusals
and one rotated-geometry refusal. No private/sealed truth, optimizer step,
weight/threshold selection or unchanged Mirage comparison was used.

Delivery/outcome commit `8715a6240c5bda8b25bda06ec230665e9190eb5e` reached
origin/main and the primary checkout before activation. The existing ledger
generator fixture suite passed six controls; the actual package/ledger binding
was checked separately by the activation guard. The verified payload and ZIP
were moved, not copied, into the primary build directory. The latest pointer's
write time is `2026-11-15T20:40:26.3407869Z`, SHA-256
`d8a65b32b75ed10c7b87efc24ea12dbd06a23006de747341985144fbb9e160bc`.
The CMD launcher passed `-Wait -PortableSmoke`: exit 0 in 3,071.654 ms;
all 16 shared Data files stayed unchanged. The keeper contains 478 files /
283,926,718 bytes including its inner ZIP, and no internal Data directory.

Independent helper review corrected a copied old-payload byte total before any
execution. The corrected activation/retirement helper is
`9a777822d6aef434a3ab842420eb926f8f16c1324a8ba684da0baf5731605fea`.
Read-only guards and subsequent mutations completed without a failed attempt.
The exact superseded directory `0.4.54-20261115T161209017Z-ee2fd787` was
retired: 478 files / 283,862,408 bytes, including inner ZIP
`b23e218d1564bed9e0fd754b9d338e2cfe8cf78c7de5ec705763cd67ae82b81a`.
Exact archive/tree inventory, no internal user Data, resolved path containment,
no reparse points or live consumers, pushed ledger scope and the replacement
keeper were checked before removal. At `2026-11-15T20:44:09.2992481Z`, the
old directory was absent and only 0.4.55 remained in the builds directory.
Shared Data and both unrelated modified primary-checkout files were preserved.

Completed staging, native QA, prefix measurements and cluster unit-test outputs
were retired after closeout `a3b0fa612b2313cebe735470f93bf4b4a6e93ee8` was
pushed and integrated in both checkouts:
`artifacts/hosted-preview-455`, `artifacts/geometry-native455-check`,
`artifacts/ui-prefix-measurement`, and
`artifacts/interval-cluster-containment-check`. All consumers had finished.
The four directories contained 66 files / 111,247,490 bytes. The guarded
validation and removal both passed; absence was verified at
`2026-11-15T20:57:42.1910402Z`. Per-file manifest SHA-256:
`fc9f16154eb1f40b3687cf1d4693d10b964cbf5ac44b17633cfe7d547659e17a`.
Helper SHA-256:
`39f08a81664f54ff9f94906c22add4cf15db177378ac481bf9923bb20bedcf0d`.
Every target was resolved beneath this worktree's artifacts directory, with
matching counts/bytes/hashes, no reparse points and no live consumers. The sole
package, all 16 shared Data files, development trace and unrelated primary
edits remained unchanged. The temporary helper and manifest retire after this
receipt is pushed; their current purpose is this closeout only.
The small current fixed-development trace remains the comparison input for the
next geometry revision; originals, the owned source fixture, models and active
compilation outputs are outside cleanup scope.
Production readiness, the installer/portable production pair and complete
real-graph automatic calibration remain incomplete.
