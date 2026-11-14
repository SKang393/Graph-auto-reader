<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Inward ticks and transparent raster handling, 2026-11-14

## Inward tick handling

The Rust axis proposal now distinguishes an absent tick scan from a rejected
scan. It tries inward-facing ticks only when no outward ticks are present;
rejected outward evidence does not trigger fallback, and positions from the two
sides are never merged. It excludes the contiguous axis band from tick evidence
and rejects a long member inside a grouped tick run. Existing thresholds are
unchanged.

The owned control generator `tools/GraphReader.CalibrationOcrProbe/New-TickCalibrationFixture.ps1`
adds `-InwardXTicks`, which draws the X ticks above the horizontal axis. Both
the original outward control and this inward control were run through the
actual tick-proposal path using the delivered 0.4.43 App/OCR assets and the new
Rust worker. Both returned a complete six-pair review proposal, with X
positions 140/340/540, Y positions 80/230/380, session values 1..3, and Y range
0..10. Neither proposal was applied.

| Control | Exit | Process wall | Pipeline | stderr | Result SHA-256 |
| --- | ---: | ---: | ---: | ---: | --- |
| Outward | 0 | 4.6499687 s | 4,125.4398 ms | 0 bytes | `6b89fff019fe1933cab42f50777e1363eddfc4c216aa972da740652b68c365cb` |
| Inward X | 0 | 4.2015806 s | 3,738.3195 ms | 0 bytes | `6f0ca0aeb844e2f444cac7a462f32d8ddd12353ea09f7fd67831b190aee7c9bc` |

The owned fixture image hashes are `c1750cab62d79c96b75310cdb5419edcb91a1e99218b248894a26b731aa43dda`
(outward) and `ca13eeabe4c21e6c10974d8207319610817025c3314e62066164fcc605500903`
(inward). These controls verify the targeted geometry path only; no real
candidate accuracy or private-case improvement is claimed.

The 20-test Rust suite passed. The release compile took 3.38 seconds. Rust
release worker SHA-256:
`1494a76d41713a1c9075e5395cacf65a8a9cae3c8e9a4325ff10eb7e5776ee26`.
OCR model and manifest remained `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`
and `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a`.

```sh
CARGO_TARGET_DIR=artifacts/native-engine cargo test --manifest-path native/graphreader-engine/Cargo.toml --locked --offline --jobs 1
```

The two actual probe invocations used these owned request files:

```powershell
$probe = 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\hardware-final-build\GraphReader.CalibrationOcrProbe.exe'
$build = 'E:\Graph-auto-reader\artifacts\dev-portable\builds\0.4.43-20261114T055422359Z-bf0d0a56'
$assets = Join-Path $build 'review-assistance\ocr'
$rust = 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\native-engine\release\graphreader-engine.exe'
& $probe --tick-proposal-check (Resolve-Path 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\inward-tick-check\outward-request.json') (Join-Path $build 'GraphReader.App.exe') $assets $rust
& $probe --tick-proposal-check (Resolve-Path 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\inward-tick-check\inward-x-request.json') (Join-Path $build 'GraphReader.App.exe') $assets $rust
```

## Transparent raster follow-up

The owned 2 x 1 alpha control showed that converting transparent black beside
opaque black directly to WPF Gray8 yields `[0,0]`; the transparent pixel should
appear white on the product's white image background. The new
`WhiteBackgroundRaster.Normalize` helper converts to BGRA32, composites partial
alpha onto white, sets output alpha to opaque, preserves an already-opaque
source, and checks cancellation without modifying the source buffer. Its
focused tests cover transparent, partial, opaque, premultiplied and indexed
pixels, source immutability, and cancellation.

The App test build completed in 112.44 s with zero warnings/errors. A focused
VSTest run passed 48 tests, with 0 failures and 0 skips, in 2 seconds. The
probe-only final build completed in 7.57 s with zero warnings/errors. The owned
real-dev self-test passed its two affine checks and privacy checks, rejecting
an unavailable helper before payload selection with zero payload reads.

The source App and probe identities were
`c9c306e65b989968321bc0fdfe6c0eade40c0dd071aa3e3cf920e7bb6c3630c9` and
`c5e523a21778256c8d3ab90cd92411cd57deb6033a6e3378fbb4cb6977effc3e`. Rust
remained `1494a76d41713a1c9075e5395cacf65a8a9cae3c8e9a4325ff10eb7e5776ee26`.
The focused TRX SHA-256 is
`14e73863c778eca6bf97535fd259bb4deb8f305dd4d004f8750612897f42ea5b`.

The transparent inward 640 x 480 PNG is 2,891 bytes, SHA-256
`02ad1d5271e3eb783fc5062fcc4d7c5c0b69355529d632c14a7e328db3f1f473`. Actual
source-App proposals for outward, inward-X, and transparent-inward controls
were all complete, with six expected ticks and no application of calibration:

| Source-App control | Process wall | Pipeline | Proposal SHA-256 |
| --- | ---: | ---: | --- |
| Outward | 4.1807626 s | 3,745.224 ms | `61c759f49e160dfedb95a2b73ddb6a408233d1e55bed159cccda12cea1e8f515` |
| Inward X | 3.5259525 s | 3,188.8702 ms | `58ad88448e9fa7d6a8c04f1dc64deca2723cff9e72ab0b86db8729266ccff5d4` |
| Transparent inward | 7.0879998 s | 6,647.5768 ms | `34fee7510d7b55637359c7d3c3c08adc79788e07145cdc0c6182c5776d6a463c` |

Opaque inward and transparent inward geometry and full proposal JSON matched
exactly, including confidence. Two 1280 x 800 marker controls each found the
two expected centers with zero center error. Both also verified cancellation
and actual child exit followed by restart:

| Marker control | Process wall | Worker | Cancel | Restart | Result SHA-256 |
| --- | ---: | ---: | ---: | ---: | --- |
| Opaque | 3.1090189 s | 1,345.2761 ms | 22.0997 ms | 1,086.874 ms | `b07deb120957f9c858343d499e7c98f2a911b75392a6f968cf2063ec59acdf1b` |
| Transparent | 2.9371274 s | 1,277.4613 ms | 20.9787 ms | 1,155.4725 ms | `2f38e035e6b314990ea59a39c938db4bfd3d8daa5a1da9669c2fd987a9c84f2d` |

The new temporary BGRA buffer is bounded by existing dimensions to 64 MiB for
axis/OCR and 16 MiB for markers, including for opaque inputs. The alpha
composition loop checks cancellation, but WIC
`CopyPixels` itself is synchronous. These owned timings are not a general
performance guarantee.

The fixed 0.4.43 preflight still fails closed because its packaged App predates
the alpha helper. No packaged pins were changed and no private payload
execution followed. A new reviewed package is required before any private
confirmation run.

The 48-test VSTest filter was
`FullyQualifiedName~WhiteBackgroundRasterTests|FullyQualifiedName~CalibrationOcrWorkerTests|FullyQualifiedName~CalibrationTick|FullyQualifiedName~AxisSuggestionTests|FullyQualifiedName~MarkerExemplarBackendTests`.
Its TRX SHA-256 is recorded above. The source App/probe build and tests validate
the helper and owned controls only; they do not validate private inputs.

## Left-axis continuation

An additional owned control was generated by
`New-TickCalibrationFixture.ps1 -LeftAxisContinuation -InwardXTicks -TransparentBackground`; its
640 x 480 PNG is 2,920 bytes, SHA-256
`bde8136069556bd5d04deb2a7e97b4c57c71c8bd90ce117007455664dc68e1b4`. With the
older Rust worker `1494a76d...`, the source App returned X positions
140/340/540 but no Y positions and an incomplete proposal (`UNPAIRED_LABEL`,
`INSUFFICIENT_PAIRS`), in 5.7887937 s (5,218.8282 ms pipeline), report
`335221c486fac0f584a0380e887cb3e02350ad206ad5f29691b96442dbcc7d78`.

The new Rust repair skips the long, contiguous horizontal-axis band and ends
the tick group before that band; an unconnected long stroke still rejects the
scan. It deliberately preserves a visible short outward intersection stroke,
which can be a real axis tick. The corrected proposal was complete, retained X
positions 140/340/540, and returned measured Y ticks at 80/215/350, including
the observed zero at 350 above the axis baseline at 380. Session range was 1..3
and Y maximum 10. It exited 0 in 3.9921644 s (3,597.3528 ms pipeline), with no
stderr and no application. Report SHA-256:
`42152d00c6f6fbb6d98b496202456f4b4fe04fcfad1f87c1c36edc9c696cc6a3`.

The repaired Rust release worker SHA-256 is
`cbad4af7755e946434bcc01f296a4cb76bc8832dd9d942356919a348caff9589`; release
compile took 2.21 s. Its 24 Rust tests passed; test compilation took 3.20 s
(4.774 s wall). This additional change is an owned geometry regression only,
not a private accuracy result. The package, model and acceptance thresholds
remain unchanged.

No new dependency or license change was made. The synthetic tick and raster
probes are review-only and do not change the calibration acceptance gate. The
source App, Rust worker, and probe evidence do not constitute packaged release
validation or a private-corpus accuracy result. The inward-continuation repair
has only the owned synthetic reproduction described above; broader automatic
calibration acceptance remains open.

Source and outcome are based on `46b6d039`. The only new local fixture/report
scratch was `artifacts/inward-tick-check`, below 1 MB. After the 0.4.44 package
consumers finished and delivery record `11b24b0f` was pushed, its 40 disposable
files totaling 142,837 path bytes were retired. The original corpus and current
runtime assets were preserved.
