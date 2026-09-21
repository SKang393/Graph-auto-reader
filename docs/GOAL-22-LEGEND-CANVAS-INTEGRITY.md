<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Legend canvas integrity, 2026-09-21

Open development diagnosis found legends whose full text was expected even
though the source image clipped the letters. Fixed template widths also let
larger fonts cross the legend frame. Clearance checked only visible collision
pixels, so an off-canvas legend could incorrectly appear clear.

The opt-in generator repair now measures font extents, expands the frame to
enclose text and symbols, and treats canvas clipping as a placement defect.
It safely handles completely off-canvas bounds. A single-panel outside legend
may use that figure's outer margins; multiple-panel legends retain their own
panel boundaries. Placement category and scientific geometry are preserved.
When no space exists, the original content remains with an unresolved finding.

Nine tests pass, including both canvas edges, fully off-canvas content,
undersized frames, idempotence, privacy rejection and unchanged marker masks.
Pytest reports only an optional-cache write warning. On all 21 owned open
sources, 29 legends are repaired, including all 10 clipped legends, with zero
remaining legend placement findings. All 453 labels, 3100 characters, fonts,
degradations, scientific fields and marker masks are retained. Earlier
non-legend layout findings remain separate and are not declared resolved.

These are distinct corrected input pixels. The same frozen source-scale OCR
runtime reads 323 labels exactly instead of 317, with 326 correct roles instead
of 325. False detections increase from 156 to 158; the OCR gate still fails.
The paired runtime experiment on these same corrected inputs reaches 327 exact
and 330 correct roles with 152 false detections. That separate runtime repair
awaits its complete native export regression before integration. The generator
test/preparation/two-inference job takes 269.49 seconds.

See [hash-bound evidence](GOAL-22-LEGEND-CANVAS-INTEGRITY.json). Historical
failures and sealed archives remain unchanged. No private/sealed read, training,
dependency/license change, promotion or packaged build occurred. Build 433
stays 0.4.33. Current checkpoint 1a5d5cd passed CI 35578231539; the prior
774b5a5 passed CI 35577486017. All four Goal 22 outcomes remain incomplete.
