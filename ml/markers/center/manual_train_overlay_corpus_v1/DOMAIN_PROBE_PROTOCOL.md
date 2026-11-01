<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Runtime proposal-domain probe for controlled overlays

Capture the frozen 200 train-only controlled overlays through the current
unapproved candidate raster, axis, OCR, artifact-mask and marker-center path.
Save the actual three model-input planes, plot polygon, and emitted proposal
centers using the existing stage capture. The inference capture must not read
overlay labels or construct an optimizer, and it must not open development or
sealed payloads. Pin the input, model binding, runtime description, protocol,
and source before capture, then verify the runtime description afterward.

Only after capture, join the fixed overlay receipt to the matching panel by
source and crop provenance. For lattice proposals near each controlled shape,
compare the Python patch extractor with the recorded emitted proposal centers;
retain separate counts for emitted and non-emitted positions. Check that the
entire 33-pixel patch lies inside the controlled opaque rectangle. Treat only
injected marker and distractor shapes as labeled; original image content is
unknown, never an implicit negative. Preserve the existing study-plus-image
build and internal holdout separation. Report failures and all 200 images in
the denominator, including ambiguous or missing panel matches.

This is a data-path diagnostic, not model training or production acceptance.
The small repeated artificial overlays cannot establish natural-marker accuracy
or full-image false-positive precision. Do not promote a model or start an
optimizer solely from this probe. Any later training must have a separately
fixed bounded recipe and run on the RX 6600 XT without CPU fallback. Keep
private images, coordinates, tensors, and per-image outputs in project-local
ignored artifacts.
