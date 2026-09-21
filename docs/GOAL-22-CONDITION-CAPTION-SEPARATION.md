<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Condition caption separation, 2026-09-21

The owned generator left 17 independently annotated caption pairs visually
crowded across nine open graphs. For example, a phase code appeared beside a
participant name or bracketed note as one continuous phrase. Four pixels of
ink clearance did not preserve the intended grouping.

The existing phase-bound caption helper now uses measured font extents to
keep neighbouring text groups separate on the same row. Captions stay inside
their explicitly authored phase interval. A fully off-canvas starting position
also receives a bounded occupancy check instead of an indexing error.

All 16 focused tests pass in 25.94 seconds. On distinct corrected copies of
all 21 open scenes, 16 captions move and one remains explicitly unresolved
because its phase header has no clear space. The final spacing audit finds
only that retained unresolved conflict. Every one of the 453 labels and 3100
characters remains, with fonts, degradations, scientific fields and marker
masks unchanged. Reapplying the transform changes nothing.

The frozen OCR runtime reads 328/453 labels exactly with 337 correct roles,
378 matched regions, 147 false detections, 75 misses and 1031 character errors.
These are different input pixels, not proof of improved model performance.
Compared with the prior layout, exact text rises by one but correct roles fall
by four; no candidate is selected from that comparison. OCR still fails its
shared bar. Inference takes 50.47 seconds; the full queued test, preparation
and diagnostic job takes 435.63 seconds. The unresolved narrow layout remains
a generator finding and has not been omitted from evaluation.

See [hash-bound evidence](GOAL-22-CONDITION-CAPTION-SEPARATION.json). Historical
and sealed inputs remain unchanged. No application code, model payload,
threshold, private/sealed read, training, activation or build is involved.
The separate numeric crop diagnostic is investigative only and has not changed
recognition or exported numbers. Build 433 remains 0.4.33 and Goal 22 remains open.
