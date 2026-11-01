<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Train-only marker overlay corpus

This diagnostic expands the earlier two-source pilot using the authorized
manual-data training catalog. It groups projects by study and exact embedded
image, excludes the two previously examined pilot groups, and selects one image
from each of the remaining 20 groups. Fifteen groups form an internal build
partition and five form an internal holdout. This split is within the existing
training catalog; overlap with historical or sealed sources has not been
fully established.

Each selected image receives ten separate controlled overlays: five marker
shapes and five distractor shapes. A bounded opaque patch is drawn around a
recorded curve-point coordinate. The original data and all pixels outside the
patch remain unchanged. Only the injected shapes carry labels; native content
is unknown and is not a source of negative labels. Source images, case-level
identities, and generated overlays remain in ignored local artifacts.

The completed v7 run produced 200 overlays, comprising 75 marker and 75
distractor examples in the build partition and 25 of each in the internal
holdout. Independent verification checked all v7 overlay hashes and group
assignments against the byte-identical v6 output. A separate check on v6
verified source hashes and decoded pixels outside every patch. The run used
52,227,029 bytes, read no development or sealed payloads, and performed no
optimizer steps.

This is a data-pipeline feasibility result, not a trained model or production
acceptance. The current corpus is small, uses simplified shapes and erased
patches, and cannot measure full-image false-positive precision. The current
marker feature path also requires runtime-bound OCR and artifact-mask channels.
An unapproved candidate capture has now produced those channels for all 200
overlays; see the separate
[marker-input diagnostic](../../../../tools/GraphReader.ManualOverlayCapture/README.md).
The proposal-input tensor matches the C# runtime on one synthetic fixture, but
real-image plot-domain membership remains under investigation. Do not
substitute fabricated masks or treat this corpus as training-ready until the
real input path and a bounded GPU-only training and evaluation protocol are
verified.

## Run bindings

| Input or result | SHA-256 |
| --- | --- |
| Protocol | `8dfebf2efdcea7824d44341f6364b66f157692289bbfea3b0ca341271c3b9eee` |
| Plan | `34431350ed5e94dce0b2debd481b855ed673fdb7839d669527db397a48b64428` |
| Source-extraction receipt | `23964f24a72fa7c34344f252f08d17582e6855111945ffd4c360fdeb7977bd21` |
| Overlay receipt | `b7631113c9425cd06c62e4198dd173e8423013d973040e4a05ef5448bcd7a02f` |
| Materializer source | `5ac5dafccee9f663fdc0a179a262006794b7b5f542d29870952afae8284769c4` |
| Extractor assembly | `76c1db5354b0321ef133cc6f48e1862de482eefdeae7e8e6a286148b5680b8dd` |

The source and runtime closure is recorded in `protocol.json`; the local plan,
capture receipt, images, and overlay receipt are under ignored
`artifacts/goal22-runs/marker-train-overlay-corpus-*-v7` directories. They
are diagnostic inputs and must not be packaged or committed.

Seven focused Python tests passed, including a complete synthetic CLI run,
partition selection, source bounds, output byte limits, deterministic recipes,
and preservation of pixels outside the patch. The extractor's metadata-only
preflight and train-only capture also passed. No CPU or GPU model training was
performed by this pipeline.
