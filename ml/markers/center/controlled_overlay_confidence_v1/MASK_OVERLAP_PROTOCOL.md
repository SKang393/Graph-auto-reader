<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Synthetic mask-overlap diagnostic

Reproduce the frozen candidate's nine family-scene predictions and original
five-pixel matching. For each matched and unmatched detection, count whether
its center lies on the fixture's text and artifact mask channels. Use the
fixture's existing binary channel values at 0.5; this is a descriptive mask
query, not a detector threshold or proposed suppression rule.

The masks are synthetic annotations, not independently verified production
OCR or artifact predictions. Overlap is not evidence that a given detection
is caused by the mask or should be accepted or rejected. The diagnostic uses
no optimizer, real corpus, sealed payload, model selection or release gate.
