<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Complete visibly truncated legend text

The optional original-pixel refiner can now extend a short legend crop inside
a small, closed frame with one detached symbol. It follows nearby visible ink,
excludes frame-connected strokes from the search, and rejects overlaps with
another detection. Rows already reaching the established frame margin stay
unchanged. Recognition still receives original pixels, never a guessed word
or a cleaned replacement. The result remains unreviewed with an explicit cue.

Actual CPU OCR completes both fixed 37-panel inventories. On corrected layouts:

| Development measure | Before | After |
| --- | ---: | ---: |
| Exactly read labels | 177/183 | 178/183 |
| Correct roles | 174/183 | 175/183 |
| Missing labels | 2 | 1 |
| Extra labels | 10 | 9 |
| Character edits | 39 | 17 |

The corrected development inventory clears all five central bars: precision
95.29%, recall 99.45%, exact reading 97.27%, character error 1.67%, and roles
95.63%. This is a small diagnostic inventory, not sealed or real acceptance.
Corrected training exact reading remains 430/709. All original-image failures,
distorted training cases and earlier regressions remain counted.

The final repair changes one crop per inventory. Original development gains
one correct role but no exact reading: an overlapping arrow still obscures
part of that legend. No previously correct text or role is lost. All other
chosen text, roles, geometry and review states remain unchanged. One corrected
crop's unchanged neighbor has different recognition alternatives and a
4.03e-6 confidence shift due to batch composition; it is explicitly counted.

The boundary review retained two earlier attempts. The first could follow a
crossing arrow; the second lost one correct original-training reading through
unnecessary extension. Excluding frame-connected strokes and preserving
already complete rows resolves that loss in the final measured version.

Validation passes 357 OCR tests with 16 existing optional skips, 74 application
tests, and 52 evaluator checks. Compilation has zero warnings/errors and takes
9.80 seconds. Original and corrected comparisons take 43.47 and 58.23 seconds;
scoring takes 1.22 seconds. Raw detector outputs, models and image inputs are
unchanged. Evidence includes frozen source snapshots and executable hashes.

No training, private/sealed read, model revision, production activation or
packaged build occurred. The retained portable is build 433, version 0.4.33.
All four Goal 22 outcomes remain incomplete. Next: pass measured phase geometry
through the complete workflow and expose this exact unapproved OCR composition
to its existing acceptance runner, then verify broader automatic behavior.

Source and evidence hashes: [aggregate record](GOAL-22-LEGEND-CROP-COMPLETION.json).
