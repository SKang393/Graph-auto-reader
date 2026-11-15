<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Flex5 existing-host 0.4.53 verification

On FLEX5LAPTOP, Windows NT 10.0.26200.0, the retained ManualPreview ZIP from source `9adee655d526d62f936e208063eaaa2e4296291c` was uploaded once to an isolated validation directory. The ZIP was 87,590,680 bytes, SHA-256 `36733f04d8df7525527ca98b806b385321f252e00046c8cf163d8e4589349060`. All 477 extracted files, totaling 196,256,903 bytes, matched their archive-derived hashes and sizes. The self-contained runtime reports .NET and Windows Desktop 10.0.11.

Packaged App EXE SHA-256 `c32696a14fb2c208339ae63fea59bb1753406dbfa72e64fbb960b8c0ca493ea0`; App DLL `5d81550e05259003e089b6d7a85b00cc50dff00e9599f465aabd5a2a934ac6b2`; Rust worker `38fedac724097029096361b728d17bc23c538178033f47e60008ead0417b255a`; reviewed OpenCV native library `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`. The OCR model and manifest matched `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` and `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a` respectively.

The existing-host commands were `invoke-flex5.ps1 -Phase Prepare`, `-Phase Upload`, `-Phase PackageCheck`, `-Phase OwnedCheck`, and `-Phase Inventory`. Hidden portable smoke returned expected exit 0 in 858.0687 ms; production-runtime smoke returned expected exit 2 in 241.4046 ms. Both had empty stdout and stderr. Two small settings files were created only under isolated Data.

One existing project-owned 640×480 PNG, SHA-256 `c1750cab62d79c96b75310cdb5419edcb91a1e99218b248894a26b731aa43dda`, was used. Normal Rust geometry returned the owned plot with three measured ticks per axis in 153.3612 ms. The separate review protocol returned one choice with the same owned bounds and ticks in 25.3854 ms. Both outputs were review-only.

The same CPU OCR worker read the image twice with both measured tick arrays. Both reads returned six numeric, review-required rows. Cold and warm client wall times were 2,054.9579 ms and 1,040.4714 ms; worker pipeline times were 1,720.2395 ms and 1,038.1730 ms. The second read reused the one session; the worker exited 0 on EOF. It reported one intra-operation, one inter-operation and one worker thread, a verified 0.4992-logical-core Windows Job cap and BelowNormal priority. OCR stderr contained 3,087 bytes of ONNX Runtime initializer warnings. The sampled working set reached 172,331,008 bytes; this is an observation, not a memory ceiling.

Package, owned-run and inventory result SHA-256 values are `555300b642026c5af5bfd0009c0e2a7915bffd9b3a3dd68b58e77a4d67e6e159`, `3d339070d2998a227496c3f56c3589c7a903bf3666bfed43e15a0c4312fe24d6` and `27989b760606201d3736c5a9b6087a846fa682de33af4294e335ea576550437b`. At 15:34:37.6340203 UTC the disposable remote scope contained 501 files and 284,288,731 bytes, with no reparse points or live scoped consumers observed.

This is an existing-host, one-fixture compatibility check. It does not establish clean-machine, installer, GPU, accuracy, production approval or full user workflow acceptance. No private input was used and no calibration was applied. Remote validation staging and local generated receipts are pending cleanup after this record is pushed and the current keeper is independently verified.
