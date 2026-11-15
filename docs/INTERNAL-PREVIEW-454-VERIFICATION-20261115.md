<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Internal preview 0.4.54 verification

## Integrated change and source

This ManualPreview integrates the native candidate integer-representation repair,
the endpoint label-lane repair, and preservation of explicitly selected geometry
when Suggest calibration reads and refits labels. It keeps the explicit review
and Apply gate. It adds no model, dependency, threshold, search budget or schema.
See [routing evidence](SELECTED-GEOMETRY-CALIBRATION-20261115.md),
[native representation](NATIVE-REVIEW-INTEGER-REPRESENTATION-20261115.md), and
[fixed development trace](ENDPOINT-LABEL-LANE-20261115.md).

Source is `ee2fd7871f0d6a0b45d0245f9514539607caebf1`. Full Windows
[CI 43733623111](https://github.com/SKang393/Graph-auto-reader/actions/runs/43733623111)
passed all 13 .NET suites: 2,995 passed, 30 skipped, zero failed. Rust passed 63
tests. Version, ledger, retention, localization, bounded OCR, evidence and
packaging checks also passed; release jobs were skipped. Final job cleanup was
`2026-11-15T16:19:15.9841886Z`.

The single [hosted package run](https://github.com/SKang393/Graph-auto-reader/actions/runs/43733648987)
succeeded. Its App/Domain/integration subset passed 1,337 tests, seven skipped,
zero failed. It uploaded
[artifact 14168569794](https://github.com/SKang393/Graph-auto-reader/actions/runs/43733648987/artifacts/14168569794)
at `2026-11-15T16:12:47Z`, expiring `2027-02-13T16:07:07Z`.
No local runner, paid service, public release or tag was used.

## Download and package verification

The outer artifact is 87,337,065 bytes. The inner
`GraphAutoReader-0.4.54-win-x64-internal-preview.zip` is 87,595,265 bytes.
Its 477 payload files total 196,267,143 bytes. Every extracted file matched its
ZIP hash and size. Safe ZIP paths, source/private/Data exclusions, clean source,
ManualPreview mode, native notices, Rust static CRT metadata, OCR model/manifest
and unchanged false production/release flags were verified.

Build identity: `0.4.54-20261115T161209017Z-ee2fd787`.

| Identity | SHA-256 |
| --- | --- |
| Outer artifact | `5296b6df415a67323b61f9c7c20824971c4aa32bfc96f66ab624b08d1d04885c` |
| Inner ZIP | `b23e218d1564bed9e0fd754b9d338e2cfe8cf78c7de5ec705763cd67ae82b81a` |
| App EXE | `a76f68d7758bb1c2278990b599d67979ea425370bd9b46e02ce6609b63363472` |
| App DLL | `5cd7abaf9751dbd80cedcd6f65c21041819741d46ecc46e32d1f5e30b872ca93` |
| Build metadata | `56b4327190306ca44ce34efb96646ec9771b2c722b828e0cd2dfb83efac3cbad` |
| Rust worker | `38fedac724097029096361b728d17bc23c538178033f47e60008ead0417b255a` |
| OpenCV native | `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31` |
| OCR model | `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` |
| OCR manifest | `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a` |
| Local intake record | `d84762805138cb7c12c2df56257bc227ce6aa3a2c137d32073f1976f92cfc5af` |

One local intake attempt stopped with `VERSION_INVALID` before creating a
candidate directory: dot-sourcing the shared verifier reset its same-named
expected-version/source parameters. Passing both arguments explicitly fixed the
helper. The same downloaded ZIP then passed; there was no second download,
package build or extraction. Corrected helper SHA-256 is
`9604ba2cb4496a1b6c24799698bf2edafdc9f607d7546cb65d121138536f9b6d`.

| Actual local packaged command | Expected / actual exit | Wall ms |
| --- | --- | ---: |
| `GraphReader.App.exe --portable-smoke` | 0 / 0 | 935.0369 |
| `GraphReader.App.exe --production-runtime-smoke` | 2 / 2 | 389.9918 |

Both streams were empty. The second check verifies refusal of the unapproved
production runtime, not production readiness. These are functional startup
timings, not a comparative benchmark.

## Native owned-pixel check

Computer Use launched the exact staged executable. The owned 1280 by 960 PNG
contains two unscaled copies of the existing owned 640 by 480 ticks fixture at
(0,0) and (640,480). Its SHA-256 is
`f70d78d4bedecc32396e58d1cbc69feea7b4f259f5693b86eb99e1aff9003514`;
the original fixture remained
`c1750cab62d79c96b75310cdb5419edcb91a1e99218b248894a26b731aa43dda`.

Import automatically prepared two panel tabs. On the selected second panel,
Review axis choices returned one unselected measured choice, bounds (64,25) to
(544,325). Selecting it and using its axes established blank-value anchors at
(104,325), (104,25), (504,325), with the geometry-review warning visible.
No geometry or numeric values were supplied to the workers.

Suggest calibration retained those anchors and warning while reading actual
pixels. It returned six numeric regions and six measured pairs: X 1/2/3 at
104/304/504, Y 10/5/0 at 25/175/325. The UI reported the RX 6600 XT GPU. The
draft values were first session 1, final session 3, Y maximum 10. Apply remained
disabled until the reviewer checked the confirmation box. Apply then recorded
sessions 1 through 3; Undo restored the prior uncalibrated state and enabled
Redo for Calibrate axes. No Save or export was part of this check.

The input's automatic panel separation meant this was not an ordinary-ambiguity
scenario. Ambiguity and zero redundant axis calls are covered by the owned
unit control, not inferred from this window check. This run does not measure
real-graph accuracy, inference latency, GPU utilization or clean-machine behavior.
An independent read-only audit agreed with the four captured native states;
their JSON SHA-256 is
`409e4ed394ecf3d399c4b61116a4a0effd3a1df37ba062788abeae1dd85b48eb`.

The app closed normally. At `2026-11-15T16:23:06.3705339Z`, all 477 payload
files were reverified unchanged, all 16 shared user Data files were byte-identical,
and no candidate process remained. Four task-created Data files totaling 8,697
bytes were moved into the temporary QA directory, outside the package.

## Retention and remaining scope

Ledger/outcome commit `6ce6a5041e3045f8122b2267955bbfcb84c246f4` reached
origin/main and the primary checkout before activation. The verified payload
and inner ZIP were moved, not copied, into the primary build directory. The
latest pointer switched at `2026-11-15T16:26:41.7149199Z`; its SHA-256 is
`e69872b9244879da60a41de42238d682012ebfc9fd49ee283cff15016385402b`.

The first direct Windows PowerShell `-File` launcher invocation was rejected by
the host execution policy before the application ran. The existing repository
CMD wrapper, which supplies its documented process-local invocation settings,
then passed `-Wait -PortableSmoke`: exit 0 in 2,948.6448 ms. No system policy
changed. All 16 shared Data files remained byte-identical; the current build has
478 files / 283,862,408 bytes, including its inner ZIP, and no internal Data tree.

The completed retention inventory is 478 files / 283,847,583 bytes for 0.4.53,
seven files / 87,428,642 bytes for `hosted-preview-454`, and 16 files / 69,081
bytes for `routing-native454-check`. Retire these three exact directories after
this activation record is pushed and final consumer/keeper checks pass. The
inner ZIP and extracted current runtime remain as one build identity. Preserve
originals, shared user Data, the existing owned fixture and all independent data.

This batch passes internal-preview checks. Production approval, clean-Windows
acceptance, the installer/portable production pair and full real-graph automatic
calibration remain incomplete. The fixed development trace still produced zero
complete session drafts; this package does not claim a new accuracy result.
No private/independent evaluation, optimizer step or Mirage comparison was rerun.
Licenses and approved runtime identities are unchanged.

## Next useful work

Prioritize the documented geometry coverage failure: five of the fixed twelve
full-trace panels lacked usable geometry, and no complete session draft passed.
The later representation repair exposed complete review choices on six panels;
that does not establish correct automatic plots. Existing association metadata
does not justify wider pairing bounds or another threshold change. Keep the
unchanged451/Mirage baseline and 51 independent projects untouched.

A read-only source audit also identified synchronous image cloning/hashing at
`ManualPreviewWorkspaceService.AxisSuggestions.cs` before the first await in
label reading. The worker repeats identity hashing off the UI thread. Measure
this prefix on an owned valid raster before deciding whether an off-thread
preparation change is material. No benchmark or code change has been made for
that finding, and it does not explain the calibration coverage failure. Avoid
another package for a small isolated fix. No paid credits or independent banked
reset is authorized; checkpoint at zero weekly remaining for dot coordination.
