<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Direct open OCR diagnosis, 2026-09-21

The development tool can now emit source-bound OCR predictions for explicitly
open, project-owned development fixtures. It runs actual import, axis geometry
and one composed OCR pass per source without marker inference or export. It
checks the request, recognized open generator schema and every dev split
before reading source pixels. Missing open authorization, private inputs and
registered reserves are rejected. Shared sealed records retain JsonIgnore;
sealed evaluation and the old aggregate command remain aggregate-only.

The warning-free build and 50 memory/boundary, 15 worker and 13 archive checks
pass. Both 21-source diagnostic runs reproduce their previous aggregate
metrics exactly. The full build/check/two-run job takes 158.77 seconds.
No models, thresholds or application behavior changed.

Source-scale diagnosis counts 282 labels with both exact text and correct
roles, 56 matched misreads, 35 exact labels with wrong roles, and 80 missing
labels, plus 156 false detections. These are all 453 labels on the corrected
owned layout fixture. Missing labels include 15 legends. Inspection found
separate detected legend words being left unassembled outside the plot;
the existing completion path refuses to absorb another detection. The next
repair uses the visible frame and detached symbol to assemble that row without
guessing text or joining separate legend rows. Detached header notes, small
glyphs and calibration still require work.

See the [aggregate evidence](GOAL-22-OPEN-OCR-DIAGNOSTIC.json). Detailed owned
predictions remain local under artifacts. No private/sealed read, training,
dependency/license change, promotion or packaged build. All four Goal 22
outcomes remain incomplete. Build 433 stays 0.4.33. Continue implementation.
