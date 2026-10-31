<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Stroke-seeded XTick training crops

The production OCR recovery currently associates detected text components
with axis strokes. It does not generate a crop from each stroke. This
train-only experiment defines a new 48-by-48 source-window proposal and
records its reversible mapping to the original panel and model canvas.

The verified V6 materialization rendered 24 procedural source groups and
681 crops: 235 complete one-label positives, 409 text-free backgrounds and
37 excluded crops containing other text. The verifier checked all 1,362 image
and annotation files. Only rows marked `trainable_single_box` may be used for
the one-box model. The generator uses project-local Noto fonts and preserves
source group identities for future separation. It applies the renderer's
recorded affine warp to nested source-frame annotations, rejecting non-affine
cases. No model training or production activation was performed.

- Manifest SHA-256: `12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a`
- Generator SHA-256: `98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d`
- Focused tests: 8 passed, including warp, crop mapping and target admission.

Earlier attempted outputs are superseded and must not be consumed. Synthetic
crop verification alone does not demonstrate image recognition accuracy or
independent source performance.
