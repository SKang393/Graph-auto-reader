<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Orientation-disagreement review verification

Repeat the unapproved wide-horizontal candidate on the same 20 previously
exposed training images. When it changes the component's vertical crop
orientation to horizontal, preserve a review reason for any resulting tick
reading. Compare the new capture with the previous candidate for tick readings,
panel outcomes, marker observations, and added or removed review warnings.
Specifically count whether the two previously reviewed regions whose warning
disappeared regain a region-specific review signal. Read the old reviewed
manifest only during scoring; no truth is needed during inference.

The candidate's OCR and calibration remain unapproved. This verification does
not measure independent generalization or missing text. Do not relax a release
gate or treat a review signal as proof of the correct reading. Keep per-region
results private, use no optimizer steps, and open no sealed payload.
