<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Header bracket context, 2026-09-21

Nearby bracketed notes could be classified as phase headings because their
vertical separation was smaller than one text height. The optional header
resolver now checks immutable original pixels for a horizontal bracket with
two downward hooks above an independently corroborated heading row. Such a
note remains unreviewed and receives the existing review warning. Recognized
text, geometry, numeric values, explicit/reviewed roles and standalone phase
codes are preserved. The cache identity includes the new composition version.

On the same 21 corrected owned dev images, correct roles increase from 330 to
341 of 453. All 11 changes are notes, and all detected geometry and recognized
text remain identical. Exact OCR remains 327/453, geometry precision 71.32%,
recall 83.44%, and character error rate 33.74%. These still fail the shared OCR
bar. The historical 23-source OCR check gains one correct dev role, 174 to
175 of 183, with every other metric unchanged.

All 413 OCR tests pass with 16 existing optional skips; all 439 App tests pass.
Memory/boundary, worker and archive self-checks pass 50/15/13. Builds report no
warnings or errors. The first attempt stopped before inference on analyzer
CA1068; a compatible overload now keeps CancellationToken last. The repaired
check takes 240.41 seconds. The complete native workflow takes 183.37 seconds,
including 152.93 seconds inference using reviewed c96 native bytes.

All 23 source images and 39 panels retain the same 17 exports, six calibration
failures, 479/706 correct unique values and 474/724 correct rows. One upstream
OCR role changes; exported values, statuses and every CSV metric are unchanged.
This is regression evidence, not an end-to-end acceptance pass.

See [hash-bound evidence](GOAL-22-HEADER-BRACKET-CONTEXT.json). The preceding
5baaa10 checkpoint passed CI 35580859505. Next work corrects measured caption
crowding in explicitly open synthetic layouts, retaining all labels and
scientific geometry. No private/sealed read, training, dependency/license
change, model activation or packaged build occurred. Build 433 stays 0.4.33.
All four Goal 22 outcomes remain incomplete; development continues.
