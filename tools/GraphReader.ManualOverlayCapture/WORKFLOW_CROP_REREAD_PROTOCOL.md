<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Direct rereading of workflow-emitted Y-tick boxes

The frozen workflow review found two wrong numeric primary readings among 105
visually transcribed complete tick crops. This exploratory comparison rereads
the emitted boxes directly with the pinned candidate server recognizer, first
with its standard one-pixel padding and then with no horizontal padding.
Source images, boxes, and visual labels have already been exposed; the choice
of comparison follows that review and is not independent validation.

Translate the frozen panel coordinates back to their original source images.
Admit only boxes whose full one-pixel padded bounds remain inside the original
panel, retain exclusions in the denominator, and do not pass transcriptions or
original OCR readings to the recognizer. The direct cropper uses horizontal
orientation and source-image decoding, so it is a diagnostic reread, not a
claim that the full workflow used identical crops or orientation. After both
outputs are frozen, compare them with the recorded workflow readings and the
assistant-reviewed labels, including wrong values and nonnumeric crops.

This comparison cannot establish detector recall, complete calibration, or
release acceptance. It does not change production settings, review guards,
model approval, or release gates. No optimizer or sealed payload may be used;
private images, labels, and per-region predictions remain project-local.
