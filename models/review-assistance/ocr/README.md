<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Internal calibration label review

This profile reuses the exact retained Apache-2.0 PP-OCRv5 English reader for
explicit local evaluation and internal-preview review assistance. It is not a
production model-store entry. The manifest's benchmark approval remains false.
No model weights are stored in Git, and the Production runtime rejects the
calibration review worker entry point.

The original conversion and license identities are recorded in
[the official archive audit](../../manifest/ocr/PP_OCRV5_OFFICIAL_ARCHIVE_AUDIT.md).
The current recognition-only observation is recorded in
[the bounded probe result](../../../docs/CALIBRATION-OCR-PROBE-20261113.md).
The manifest also declares the explicitly selected DirectML path measured in
[the bounded hardware check](../../../docs/OCR-HARDWARE-CHECK-20261113.md).
CPU remains the initial default; GPU sessions retain CPU limits and idle pacing.
These do not establish full-scene accuracy or automatic calibration approval.

`packaging/Stage-CalibrationReviewAssets.ps1` checks the exact model, manifest,
license and notice hashes before writing one new asset directory. The app
looks for `review-assistance/ocr` relative to its executable. A maintainer can
explicitly set `GRAPHREADER_CALIBRATION_OCR_ASSET_ROOT` for local evaluation;
the worker independently validates all four asset hashes before loading them.
No historical checkout path is compiled into the app.

The optional files are one model, this manifest and the two reviewed license
and notice files. Their purpose is a current worker input. Keep one current
asset set, and retire disposable evaluation staging after its consumers finish.
The exact converted model currently has no direct public download URL; hosted
delivery must reproduce the reviewed hash or receive a verified repository
artifact before this profile can be included in a hosted preview build.
