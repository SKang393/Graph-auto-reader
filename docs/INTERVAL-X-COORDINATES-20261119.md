<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Printed X labels between tick strokes

A height-based search around an OCR box could associate a label with one edge of a session interval. A shorter label in the same row could retain its own center. Combining the two coordinate conventions can produce a false pitch disagreement even when the markers occupy regular columns.

The production association stage now shares its original-pixel attached-stroke scan across both layout checks. When a numeric glyph lies wholly between adjacent attached strokes, covers their midpoint, and lies near both tips, it retains its glyph-center estimate. The evidence records both boundary positions and the estimate. Neither boundary becomes an attached numeric anchor. Additional ink across the label band outside the OCR box, competing readings, overlapping or mixed label rows and an uncorroborated interval layout remain review conditions. The detector, marker classifications, lattice tolerance and series membership rules are unchanged.

The regression supplies two OCR boxes of different heights, five marker centers from two series, a skipped session and independently drawn rail and boundary pixels. All four variants reproduced the incorrect snap on the parent implementation. With the correction, the whole-glyph control obtained three session assignments. The partial-glyph, mixed-layout and disputed-reading variants required review.

For the separate image-only control, run `python tools/GraphReader.ManualOverlayCapture/render_interval_column_control.py <new-output.png>` on Windows. The raster contains two series with shared and skipped sessions, fourteen printed interval labels and a dashed divider. Inference found all thirteen markers within five original pixels, made eleven distinct marker columns and recognized zero X labels. That run therefore produced no numeric rows. Regeneration matched the evaluated PNG byte for byte.

Image-only checks across two nonsealed development graphs remained at zero fully calibrated numeric rows against 26 reference observations. Correcting the association isolated an X geometry defect while leaving unresolved OCR conflicts visible for Review.
