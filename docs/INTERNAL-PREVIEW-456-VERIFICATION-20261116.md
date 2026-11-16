<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Internal preview 0.4.56 verification

This ManualPreview combines automatic tilted-frame review, reversible
calibration reading, marker assistance inside confirmed affine/printed-X plots,
and preservation of manually marked or corrected tilted axes. Scientific values
still require valid observed evidence and explicit review/Apply. See the
[manual/correction outcome](MANUAL-AFFINE-AXIS-DRAFT-20261115.md),
[rectification evidence](AFFINE-CALIBRATION-RECTIFICATION-20261115.md) and
[marker evidence](AFFINE-MARKER-ASSISTANCE-20261115.md).

## Source, upload and package checks

Source `472b715f29c5ca54ef268e5d8d7d0b31681d5c1e` passed exact-head
[Windows CI 43781221439](https://github.com/SKang393/Graph-auto-reader/actions/runs/43781221439):
**3,180 .NET tests passed, 30 expected skips, zero failures** across thirteen
projects, plus **72 Rust tests** and **68 packaging verification controls**.
Applicable version, localization, evidence and packaging audits passed.
Public release jobs were skipped.

One [hosted build 43782484405](https://github.com/SKang393/Graph-auto-reader/actions/runs/43782484405),
job `133022589157`, passed **1,476 tests, seven skips, zero failures**:
App 1,310, Domain 23 and the selected Integration subset 143.
It uploaded [artifact 14189315574](https://github.com/SKang393/Graph-auto-reader/actions/runs/43782484405/artifacts/14189315574)
at `2026-11-16T00:53:18Z`, expiring `2027-02-14T00:47:48Z`.
No local runner, public release/tag, paid service or reset was used.

Build identity: `0.4.56-20261116T005249234Z-472b715f`, built at
`2026-11-16T00:52:49.2341256Z`. Outer artifact **87,373,821 bytes**;
inner `GraphAutoReader-0.4.56-win-x64-internal-preview.zip`
**87,632,560 bytes**. All **477 payload files / 196,379,271 bytes** matched
archive hashes and sizes before and after the packaged smokes. ZIP paths,
duplicates, links/reparse points, source/private/Data exclusions, self-contained
host files, exact source/version and ManualPreview identity were checked.

| Identity | SHA-256 |
| --- | --- |
| Outer artifact | `7fcb1bbe4ce5cbccc623eb5a4dded29c2b7c94fe89cf50e53b2bff528c90edf5` |
| Inner ZIP | `92c1a19420fb51c38d7eb838804fa3c0b7c1347bc1ce76dcb7074a8f04dda4f8` |
| App EXE | `cbe92fd80ce4e0df7d6a18dcadeda6795b7d68e1e51da65f3916f4db37ed68e0` |
| App DLL | `9d884921899aa127542e86bdc4ffa11d02d9aef4ed9a09226a01190669c13fba` |
| Build metadata | `f8fa14ebe1ffa1b0456091a40ded72a32bdc8501c3c6b8e8c975db84657ff7c6` |
| Rust worker | `e8bcd29b2f54643883961dd91d5fefb87b3b8c9df33b99ee1a71c0f2c570adf7` |
| Staging manifest | `de418647a55635d332819de61d797e722a252a292e5b05632d75a11665a757c2` |
| Hosted receipt | `cf4e06cc810dfb543270fe40ddd9ddbe0df7d1cc71370939af4a204d584449b8` |

FileVersion is `0.4.56.0`; ProductVersion is `0.4.56+472b715f29c5ca54ef268e5d8d7d0b31681d5c1e`.
The Rust hash matches receipt and static-CRT metadata. OpenCV `c96f91b3...f3b31`,
its lineage and four notices, and all five reviewed OCR assets/manifest/notices
match 0.4.55 byte-for-byte. OCR remains the reviewed Apache-2.0 model
`7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`.
No dependency, weight, threshold, license or frozen contract changed.

## Headless packaged checks and limitations

| Command | Expected / actual exit | Wall ms |
| --- | --- | ---: |
| `GraphReader.App.exe --portable-smoke` | 0 / 0 | 1,189.7703 |
| `GraphReader.App.exe --production-runtime-smoke` | 2 / 2 | 502.8679 |

Both hidden child runs had 30-second bounds and empty stdout/stderr.
Production exit 2 confirms refusal of the unapproved runtime. These single
startup samples are not benchmarks or UI acceptance. The two task-created
three-byte portable settings were hash-checked and moved outside the payload
to owned smoke output. Shared user Data and the 0.4.55 keeper were untouched.
No stage consumer remains. Peak outer/inner/payload staging was about 371.4 MB.

The first sandboxed GitHub query could not connect; normal-host access succeeded.
There was one artifact download and no package/smoke retry. The manifest's
aggregate byte field was initially null despite valid per-file records; it was
corrected to their independently checked sum before activation, without changing
or rerunning the payload. The first read-only activation preflight used the
wrong old ZIP location and stopped before mutation; the actual `packages/`
location was verified and the helper corrected. Independent review also added
an exact pushed-ledger blob guard and process command-line consumer checks.

Native tilted-graph review remains blocked by the helper's persistent stop state,
despite explicit authorization and the maintainer reporting no new Escape.
There is no new native, clean-machine, installer, complete automatic real-graph,
or production acceptance claim. The separate supplied-box refusal diagnostic
is not included in this frozen build.

## Activation and retention

The delivery ledger selects the verified replacement and marks activation and
superseded-build retirement pending. Push it before switching the pointer.
Then move the payload and inner ZIP into the primary build directory, verify
the latest launcher, and retire the exact superseded 0.4.55 directory only after
its uploaded ZIP/tree match, no-consumer checks and shared Data preservation.
Keep the 16 shared Data files outside both package directories. The guarded
helper and compact staging manifest remain active until that transaction closes;
retire them and the outer download after the completion record is pushed.
