<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Development crop transcription diagnostic

The fixed 134 proposal crops in `dev-connectivity-proposals-v1.json` come from
16 already exposed development images. Their boxes and category-only visual
review were frozen before this diagnostic. Before viewing any recognizer output
for these crops, an assistant will inspect the existing flag-blind review sheets
and transcribe each clearly legible numeric crop. Ambiguous, clipped, or
non-numeric crops remain explicitly unscored for exact transcription. The
assistant may use the immediate visual context to locate the crop, but must not
invent a missing character from a sequence. Private transcriptions, sheets, and
predictions stay inside ignored project-local artifacts.

The input to recognition will contain only proposal IDs, source-image identities,
and original-pixel boxes. It will exclude the transcriptions, category labels,
and connectivity flags. The recognizer will use the existing pinned candidate
server model, production decoder, cropper, and crop settings, without optimizer
steps. After predictions and label files are frozen by SHA-256, a scorer will
report every exact-scored crop, primary mistakes, and whether the existing
positive numeric-alternative review rule flags each mistake. Any proposed
confidence threshold must be selected from training data before measuring it on
these development data; this diagnostic alone does not authorize a threshold.

The review is assistant-produced and not independently adjudicated. These
development images have repeated layouts and prior visual exposure. The result
cannot establish whole-image localization recall, graph-level calibration,
precision, independent generalization, model approval, or release acceptance.
The sealed split must not be opened. CPU image processing and inference are
permitted; training, if separately required, must use the RX 6600 XT.
