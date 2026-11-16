<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Internal preview 0.4.58 verification

This integrated ManualPreview exposes eligible choices after an incomplete
axis search and preserves actual tilted frames in the choice list. Selection
remains explicit, incomplete status remains visible, and the existing numeric
review, Apply and Undo guards remain mandatory. See the
[incomplete-search](INCOMPLETE-AXIS-REVIEW-20261116.md),
[tilted-choice](TILTED-AXIS-CHOICES-20261116.md) and
[fixed development availability](AXIS-CHOICE-AVAILABILITY-20261116.md) records.
The diagnostic's 855 alternatives are not an accuracy or calibration result.

## Source and single hosted build

Source `4279db15b54e6c065ad5bbe0aa6e3a09ad04bf7c` passed
[Windows CI 43828397926](https://github.com/SKang393/Graph-auto-reader/actions/runs/43828397926),
job `133167547326`, from `08:04:50` to `08:17:20 UTC`: **3,243 .NET passes**,
**30 explicit skips**, **80 Rust passes**, zero failures and **68 packaging
checks**. The newly added owned diagnostic preflight also passed.

One [hosted build 43830167389](https://github.com/SKang393/Graph-auto-reader/actions/runs/43830167389),
job `133173331627`, ran from `08:18:32` to `08:25:01 UTC` and passed **1,502
tests**, with **nine skips** and zero failures: App 1,336/2 skipped, Domain 23,
Integration 143/7 skipped. The two optional App worker controls remain skipped
in this hosted workflow. The Rust executable is byte-identical to verified
0.4.57, so those unchanged controls and the prior OCR benchmark were not repeated.
The newly packaged chooser was exercised separately below.

## Downloaded package

[Artifact 14207709231](https://github.com/SKang393/Graph-auto-reader/actions/runs/43830167389/artifacts/14207709231)
was created `2026-11-16T08:24:59Z` and expires `2027-02-14T08:18:29Z`.
The single download took **8.366 s**, finishing `08:26:46.7533914Z`. Its outer
archive is **87,383,852 bytes**; the inner
`GraphAutoReader-0.4.58-win-x64-internal-preview.zip` is **87,642,720 bytes**.
Build directory: `0.4.58-20261116T082429970Z-4279db15`; build time:
`2026-11-16T08:24:29.9706051Z`.

All **477 payload files / 196,397,703 bytes** matched the ZIP before and after
the local checks. Safe paths, no duplicates/reparse entries, no private/user
files, no internal Data directory, exact clean source/version, self-contained
host, ManualPreview metadata and Rust/native provenance were checked. **39
runtime, model and license files**, including all five review-OCR files, match
0.4.57. No new dependency, weight, license or production approval is introduced.

| Identity | SHA-256 |
| --- | --- |
| Outer artifact | `8559b39babd66dacd924eaaf5ad17a8aefa24565426cab247e91599a178b0b74` |
| Inner ZIP | `851d143b89d86e0c66c7539caee79c801eeb39444fca3ea392a6e8537f2ce21a` |
| App EXE | `5347c1a3fc6fa39747863d4775105de58f62baa6b1e43454b2abb8b14b4c3784` |
| App DLL | `b3a2f762ec6b704ca5bc2c9707dc3ba9b535ac69e4d01c290ea086e63e1a463a` |
| Build metadata | `23b8664714332b24962f21a63e376eeb4834e4f6c9322698f29dd86515fb663e` |
| Rust worker | `19e24a703907281341b183ffc0c16a6a7ab12bb1c4a2b4e97f41e16a76268347` |
| Payload manifest | `f1d64b42e635cd72bcebfd920b1879eaed1c5a3a2ec2a0b2a47090f908f4b72b` |
| Hosted receipt | `6c277c5b9ef7cbb20998f22c332c245ff853bb3c9fa7f468df394f237ad9cc5c` |

FileVersion is `0.4.58.0`; ProductVersion is
`0.4.58+4279db15b54e6c065ad5bbe0aa6e3a09ad04bf7c`. Reviewed OpenCV remains
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`;
its clean-machine and release approvals remain false. Review OCR remains
`7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`.

## Headless package checks

Hidden startup checks used isolated Data outside the payload and a 30-second
limit. `--portable-smoke` returned **0 in 864.394 ms**.
`--production-runtime-smoke` returned the required refusal **2 in 336.217 ms**.
Both produced empty stdout/stderr. The latter preserves the unapproved
production gate; it is not a portable startup failure.

One direct `--native-axis-candidates-worker` request used the retained owned
640 by 480, positive-two-degree PNG, SHA-256
`2a6b7431217fcc2267471c13601e92316a3a9098fd60e5d8b2301f4e8bf426ba`.
Only image bytes were supplied. The private length-prefixed worker returned
**one valid affine choice**, `EnumerationCompleted=true`, confidence zero,
exit **0**, empty stderr, in **715.291 ms**. Its existing half-core budget,
single OpenCV thread and 12-second watchdog remain in force; the client added
a 20-second outer deadline and a create-new invocation receipt.

The 285-byte owned response has SHA-256
`8b066d56e3970dfc05797dd627e662304b98b83471ee7ac9b91cf81e01eb2125`.
Result SHA-256: `4e963dd7e7605299e6ba0eef2cf510d4eed45e69363aefb93006bfdb69cfa2b0`.
The client checked framing, bounded one-of geometry, finite original-pixel
anchors, orientation, nondegeneracy and bounds. Descriptive distances to the
predeclared synthetic frame were **0.530 px** at the origin, **0.369 px** at
the X endpoint and **0.577 px** at the Y endpoint. These were calculated from
the one saved response, without another worker call or a new accuracy bar.

This is image-derived geometry on one owned control, not supplied geometry or
complete automatic numeric calibration. No candidate was selected, no numeric
values were supplied, and no tick/OCR/fit/Apply/export or private/sealed read ran.
All 27 protected input/shared-Data hashes remained unchanged. No Data appeared
inside the package. Native visual interactions, clean-machine/installer
acceptance and representative automatic accuracy remain incomplete. The desktop
tool's stale Escape stop persists despite renewed authorization and no new Escape.

## Activation and retention

Delivery commit `bfb1847ea94d88afcfcc55f021b0669519f00c05` was verified on
`origin/main` before the existing
[activation helper](INTERNAL-PREVIEW-ACTIVATION-20261116.md) ran. Inspect passed
in **6.207 seconds**. Activation passed in **7.785 seconds**, finishing
`2026-11-16T08:40:40.7700301Z`; all 477 payload files and all sixteen shared
Data files were verified. The latest pointer now selects **0.4.58** and has
SHA-256 `0215c6e05bedeb435ed6ab469b78cd88eee9f9ba60595b8bfa9a24795163443f`.

The actual primary `packaging/Run-Latest-DevPortable.cmd -Wait -PortableSmoke`
returned **0**, with 279 informational stdout bytes and empty stderr. The
measured **2,189.072 ms** includes wrapper and postcheck time, not just startup.
Verification finished `08:42:41.9517319Z`: all 27 protected hashes were
unchanged and no Data directory appeared inside the payload. The current keeper
contains **478 files / 284,040,423 bytes**, including its verified ZIP.

The activation outcome `4d5df76c` reached `origin/main` with `68595526` before
retirement at **2026-11-16T08:57:20.9918972Z**. Exact canonical-path,
no-reparse, no-tracked-file, inventory and active-consumer checks passed.
The old `0.4.57-20261116T050648697Z-4db78d87` keeper was retired:
**478 files / 284,026,401 bytes**. Completed `artifacts/hosted-preview-458`
staging and `artifacts/preview458-check` receipts/client were also retired:
**30 files / 87,673,816 bytes**. These historical raw paths no longer exist.
The separately recorded CI lookup receipt was retired: **one file / 98 bytes**.

All 27 protected hashes and the retained ZIP were unchanged after removal.
Exactly one packaged build directory remains: 0.4.58. Preserve that build,
the uploaded artifact,
original images, owned native-QA inputs, protected runtime checkpoint and current
assets. This is an internal portable preview with no installer, public release,
paid credit use or independent reset redemption.

## Existing Flex5 laptop, owned tilted choice

The missing 0.4.58 laptop check ran once on the existing trusted Windows x64
host, version **10.0.26200.0**, using the exact keeper ZIP and the same owned
positive-tilt PNG above. The initial sandbox SSH attempt was denied before
connection; the normal-host trusted preflight then succeeded. Existing host
keys and identity were reused with strict checking and noninteractive access.
No trust, credentials, dependency or installed application was changed.

One transfer of the ZIP, PNG and bounded PowerShell client took **3.5193 s**.
The new isolated scope was under `GraphAutoReaderValidation/preview458/`
`tilted-choice-20261116`, with Data outside the extracted payload. All **477
files / 196,397,703 bytes** matched their archive entries before execution.
One hidden `GraphReader.App.exe --native-axis-candidates-worker` request sent
only the existing PNG with a four-byte little-endian length prefix. The worker's
existing CPU/thread limits applied, with a 20-second outer deadline. The client
checked exact frame length, bounded JSON, geometry one-of, finite anchors,
orientation, nondegeneracy, image bounds and review-only zero confidence.

The worker exited **0**, with **empty stderr**, in **569.223 ms**. Its single
response contained **one valid affine choice** and `EnumerationCompleted=true`.
The **285-byte response body is byte-identical to the recorded desktop 458
response**. This equality was checked from the saved response without another
worker invocation. The SSH verification command took **12.6033 s wall**,
including **11,574.0958 ms** inside the verification script. These single host
observations do not establish a general speedup. The clean result file's UTC
modification time was **2026-11-16T12:20:53.4535527Z**, not an independently
recorded run start/end time.

The wrapper printed two `VoidTaskResult` lines before its aggregate JSON;
these are script output noise, not worker output. Its separate `result.json`
contains clean JSON. Every declared stage completed, with one worker attempt
and no failure, timeout or unattempted worker. All payload hashes, ZIP and input
matched afterward; isolated Data remained absent. The desktop keeper and all
sixteen shared Data hashes were also unchanged. No choice was selected and no
OCR, model, numeric fit, Apply, export, private or independent input was used.
This is **headless existing-host compatibility**, not native visual workflow,
clean-machine acceptance or complete automatic calibration.

| Follow-up evidence | SHA-256 |
| --- | --- |
| Client script | `2d1ba8e6f7fd20bd43e2db01af98b84fedfe7b803cf47efc4d514b261c179f77` |
| Remote clean result | `3067f42f4848362b3aa2966c0760db16cb599e67a07a0e053ccad8ba945ed600` |
| Complete framed response, 289 bytes | `7e3d3f890b9d3d88d8e349d2afc85a337f6c294f483b68c39dac7507a7093efd` |
| Local stdout receipt, 704 bytes | `090d08490e49dc77baea2191737e1b72de66ad863c132bde90cb38b03bec919a` |

Outcome `a8fabe1362b406a0dcb81579acd4e61d476e6a5d` reached `origin/main` before
guarded retirement. At **2026-11-16T12:32:39.1008648Z**, the new remote task scope
was removed: **482 files / 284,056,234 bytes**. At **12:32:48.3108592Z**, local
`artifacts/flex5-458-check` was removed: **four files / 12,188 bytes**. Both are
confirmed absent; these raw paths are historical records, not available inputs.
Exact paths, reparse ancestors/content, input/result hashes, inventory, Data
absence and live consumers were rechecked before deletion. The desktop keeper,
original owned PNG and all sixteen shared Data hashes still match. Host state,
other work and the ongoing isolated Axis profile were untouched. No package,
public release, approval change, paid credit or independent reset was created.
