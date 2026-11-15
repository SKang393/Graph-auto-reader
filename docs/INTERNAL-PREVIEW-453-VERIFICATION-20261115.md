<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Internal preview 0.4.53 verification

## Delivered scope and source

This ManualPreview integrates explicit Rust/OpenCV axis choices, preservation of
measured Rust ticks through literal OCR and refitting, printed-X review, and the
mode-localized accessible text/language-refresh correction. Review choices do
not select a graph or invent numeric values. Automatic production stages remain
unapproved. The later native rejection observer and integer-representation
repair are separate source work and are not in this package.

Source is `9adee655d526d62f936e208063eaaa2e4296291c`. Exact-source Windows
CI [43720654959](https://github.com/SKang393/Graph-auto-reader/actions/runs/43720654959)
passed 2,960 .NET tests with 30 skips and zero failures, plus 63 Rust tests and
the version, ledger, retention, localization, evidence and packaging checks.
The [hosted package run](https://github.com/SKang393/Graph-auto-reader/actions/runs/43721671825)
completed successfully at `2026-11-15T14:02:49Z`. Its selected App/Domain/
integration suites passed 1,322 tests with seven skips and no failures.

The artifact is
[14164748671](https://github.com/SKang393/Graph-auto-reader/actions/runs/43721671825/artifacts/14164748671),
created `2026-11-15T14:02:43Z`, expiring `2027-02-13T13:57:04Z`. The outer
archive is 87,333,319 bytes. One download reused the existing repository workflow;
no local runner, public release, tag, paid service or new model was introduced.

## Package identity and local checks

The 87,590,680-byte inner ZIP is
`GraphAutoReader-0.4.53-win-x64-internal-preview.zip`. Its 477 payload files total
196,256,903 bytes. Every extracted file matched its ZIP bytes; paths, links,
private/source/Data exclusions, metadata, OCR model/manifest, native runtime and
license/notice identities were checked. Build directory identity is
`0.4.53-20261115T140221861Z-9adee655`.

| Identity | SHA-256 |
| --- | --- |
| Outer artifact | `b871d2edb5c3f1493de3518dae9757711ab35057898e728d36e9b39aed2ed7fd` |
| Inner ZIP | `36733f04d8df7525527ca98b806b385321f252e00046c8cf163d8e4589349060` |
| App EXE | `c32696a14fb2c208339ae63fea59bb1753406dbfa72e64fbb960b8c0ca493ea0` |
| App DLL | `5d81550e05259003e089b6d7a85b00cc50dff00e9599f465aabd5a2a934ac6b2` |
| Rust worker | `38fedac724097029096361b728d17bc23c538178033f47e60008ead0417b255a` |
| OpenCV native | `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31` |
| OCR model | `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` |
| OCR manifest | `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a` |
| Build metadata | `aec83cbe6bcb483ba0658d6d2bb7423bd976e3005d91b715f39775f8cdb492a7` |
| Payload manifest | `d67a9f976675d5b5dceb37523d257b58355280070ebc0a00334dc27ff0b64105` |
| Local verification record | `5cc2981e2562650f5a86d65031fd02a041b703383f4a036bac33bc16342f82dc` |

Two local verifier attempts failed in the helper, not in the package. First,
the required-file set incorrectly combined LICENSE, NOTICE and
THIRD_PARTY_NOTICES.md into one path. The actual separate paths agree with
`packaging/common/publish.json`. The retry's resume branch then referenced an
unassigned hash variable while writing its final record. Both were corrected;
the existing ZIP/extraction were rechecked without another download or build.
Final verification passed, including all hashes and the original approval flags.

Actual packaged runtime checks after the GUI closed:

| Command | Expected/actual exit | Wall ms |
| --- | --- | ---: |
| `GraphReader.App.exe --portable-smoke` | 0 / 0 | 1,259.1162 |
| `GraphReader.App.exe --production-runtime-smoke` | 2 / 2 | 459.3417 |

Both streams were empty. This verifies portable startup and refusal of the
unapproved production runtime, not production readiness.

## Owned packaged worker checks

The packaged Rust `--review-candidates` worker returned both expected boxes
and their measured ticks on the owned in-memory two-grid control, exit 0,
empty stderr, 136.0057 ms. No geometry was supplied to that worker.

The native worker returned eight complete choices on the pinned owned grid
(`1d258bf5c7be0b7788286f01e64ac8d1ca1eaac8e0b574b927ca1e52011c6894`),
exit 0, empty stderr, 771.0544 ms. The initial smoke expected six and therefore
failed its count assertion. Source/fixture review showed four literal vertical
sides (100, 140, 340, 540) paired with two baselines (380, 230); both x=540
boxes are valid review options under the complete-list contract. No threshold
or detector changed to satisfy this check. A single native-only diagnostic
call took 1,802.2457 ms and reproduced the identical response SHA-256
`1672eda2110f756a4e7f39760094f990ca265d1d924fe247328f2b66546a15ab`.
Corrected validation of those captured bytes passed all eight boxes, order,
contiguous indices, unknown confidence and empty native tick arrays. The
passing Rust call was not repeated. These are functional timings, not a speed
comparison. Initial failures remain in the recorded accounting.

## Native window check

Computer Use launched the exact staged EXE. One owned 1280 by 960 two-panel
PNG was imported without supplied geometry. Its unchanged source SHA-256 is
`b67913c31dbbe4f6a9a651cc003b07c800f968e9f5e61461768c538bbd8b8642`.

- Review axis choices exposed both expected Rust boxes. No item was selected;
  Use selected axes and Undo were disabled.
- Selecting the first option previewed its axes without changing the project.
  Explicit Use produced anchors (140,380), (140,80), (540,380), blank values,
  disabled Apply and the persistent geometry warning.
- One explicit label read returned six literal regions. The UI reported
  `GPU, AMD Radeon(TM) Graphics`. Refit associated Y 10/5/0 with measured
  pixels 80/230/380 and X 1/2/3 with measured pixels 140/340/540.
- Draft values became Y=10, first=1, final=3. Apply remained disabled until
  the review checkbox was checked. Apply created one Calibrate axes undo entry;
  Undo restored the uncalibrated manual defaults 100/1/20 with Redo available.
- No Save, CSV export or marker edit was performed. The app closed normally;
  subsequent window and process checks found no GraphReader consumer.

The stage-local Data directory was created after extraction. Auto profiling
saved an integrated-GPU profile there at `2026-11-15T14:15:14.2258187Z`.
Startup can schedule this bounded measurement without a Check devices click.
Its settings file does not contain timings, so this check makes no device-speed
claim. No device policy was changed. Four task-created Data files totaling
6,245 bytes were moved, with hashes preserved, to staging `qa-data` before
build-tree verification. Original input and shared user Data were not moved.

This proves an assisted owned calibration path, not fully automatic real-graph
accuracy, fresh-machine acceptance, save/export completion or an installer.

## Activation and retention

The guarded Stage validation and execution both passed: 477 files /
196,256,903 bytes, exact ZIP and source identities, no active consumers, and
all 16 shared Data files preserved. The build and ZIP moved into the final
versioned directory without a duplicate extraction. Ledger/outcome commit
`c6b6164d90204871a9bef6a805c6e32528e42b2d` then reached origin/main as an ancestor
of `c0f7a2f97f3fd74f82f0c6b92ed4cfa165f40b1f`.

The first activation preflight failed before mutation: strict-mode access to
`directory` on a historical ledger row without that optional field. The scratch
helper now checks property presence before selecting the old build. Corrected
ValidateOnly/Execute guards passed for activation and then separate retirement:
remote scope and exact ledger blob, package/source hashes, complete inventories,
resolved in-root paths without reparse points, no active consumers, and unchanged
shared Data. Latest switched at `2026-11-15T14:42:29.2812418Z`; pointer SHA-256
is `85d7a1fa0d2bdf8c4597e485802fbc65a9a497f24928ed0a262367ac5a3acf35`.
The exact superseded 452 directory was removed: 478 files / 283,716,647 bytes.
The current build tree has exactly one build identity, 453, comprising 478 files
and 283,847,583 bytes including its ZIP. All sixteen shared Data files survived.

The actual primary-checkout launcher then passed
`packaging/Run-Latest-DevPortable.ps1 -Wait -PortableSmoke`, exit 0, in
1,648.9601 ms. The child was hidden for this headless smoke. It resolved the
453 executable and shared Data through the active pointer and ledger; all
sixteen shared files had unchanged hashes, no new shared files appeared, and
no Data directory was created inside the retained build. This is a launcher
check, not another visual workflow pass.

After those consumers finished, the seventeen completed staging files totaling
87,432,451 bytes were retired at `2026-11-15T14:46:47.0190461Z`. This includes
the outer downloaded archive, verifier/activation helpers, raw worker/runtime
records and the task-created QA Data. The pushed outcome and retained inner ZIP
were checked before deletion. The remote artifact remains available under its
bounded retention. Current runtime/model assets, originals and user Data were
not removed. Historical result hashes below identify retired evidence, not files
promised to remain on disk.

The completed owned-grid consumers were also retired at
`2026-11-15T14:52:10.2490767Z`: `artifacts/axis-candidate-review-check`
(one file / 6,122 bytes) and `artifacts/rust-review-ocr-owned-check`
(twenty files / 45,368 bytes). Their image hashes matched the documented controls,
no live source consumer remained, and the pushed package outcome was checked.
These are regenerable owned fixtures and worker records, not original figures.

Result hashes for concise retention provenance: initial worker record
`c9591007d9eaefd16ad60fc40dcd0149b0c97f975cb6cd5b878791929cfef4ea`,
native diagnostic `77b0b3af904a894f8ef3f710029ecf55a0bde106c9f1ebf827426bb229d81919`,
corrected cached validation
`c3f595dedbdff28a753117ea3295522487d114439bfaa64a4a833a5cf8e678d8`,
runtime smokes `ceb1ef7a42080dc1f541cbdd480a71c855cfd952db7748e97ca5f5df5a0db82d`,
and QA move manifest
`f4c71d7b3b2cf7598357c605f21f8c63836bfcb2643749f3e43356fa5e1c45d8`.
No dependency or license changed. All production approval flags remain false.
