<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Detector-emitted Y-tick visual review

Use all 115 Y-tick regions from the frozen detailed candidate capture on 20
previously exposed training images. Before looking at individual OCR readings
for this review, display original-pixel crops and local context without showing
model text, confidence, alternatives, or review flags. The assistant will label
each region as a readable numeric tick, a numeric fragment or uncertain crop,
other text, or graph structure. For a clearly legible complete numeric tick,
record the visible transcription; do not infer missing digits from a sequence.

After the review is frozen, join it to the captured readings and report all
regions, readable correct and incorrect numeric values, nonnumeric or
uncertain regions, and existing or new review signals. Do not count omitted
regions as correct or assume every unmatched proposal is false. This assesses
emitted regions only; it cannot count tick labels the detector missed. The
assistant has previously inspected some images and several crops, and the
review is not independently adjudicated. No optimizer, sealed-payload read,
model approval, or release gate change is authorized by this review. Private
images, annotations, and per-region outputs remain project-local.
