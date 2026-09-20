<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recover missing text in a framed legend

A synthetic graph had no detected text region inside its visible legend. Its
symbol was consequently treated as a graph observation and blocked export.
The existing crop-completion repair could not extend an absent detection.

The candidate OCR composition now proposes text from original pixels only
when a closed frame contains a single text row and a unique detached symbol.
Recognition supplies the words; geometry does not invent them. Existing
detections prevent duplicate recovery, and the new reading remains unreviewed
with an explicit warning. Open frames, ambiguous symbols and extra rows are
rejected. The general component reader keeps its existing grouping by default;
this recovery opts into separate components so an enclosing frame cannot absorb
its letters. Cache and candidate-composition identities distinguish the change.

All 376 OCR tests and 373 application tests pass, with 16 existing optional
OCR skips. Application results are reused from the identical production-source
snapshot; the subsequent change only preserves a historical source binding in
an OCR test. The x64 tool builds with zero warnings/errors and its fictitious
private-runner check passes. The preceding phase-vocabulary cache version is
also advanced to prevent reuse of stale role results.

The unchanged 23-source synthetic workflow takes 164.16 seconds and reads
`Observed outcome` from the recovered crop. All 699 prior primary texts, roles,
geometry and review states are unchanged. Eleven alternate-hypothesis or
confidence results differ because recognition batching changes. Three nearby
marker centers move by at most 0.158 pixels, within the existing five-pixel
tolerance. No previously detected truth point is lost: one non-data legend
symbol is removed and one additional actual center is recovered.

All 250 previously exported point locations, values and x-value provenance
remain identical. Completed sources increase from ten to eleven, with twelve
still failing. The new export adds 22 points. Correct unique values increase
from 218/706 to 226/706, and correct relational rows from 210/724 to 218/724.
Incorrect series grouping limits the gain: the new graph's accurately located
points are split among several predicted series. The full denominator retains
480 missing truth points and 46 unmatched predictions. Five matched rows still
have wrong phases; no matched numeric value is outside the frozen tolerance.

[Full evidence and source bindings](GOAL-22-MISSING-FRAMED-LEGEND-INTEGRATION.json)
retain the initial ineffective workflow run, its wide-canvas regression, the
test-only normalized-pixel assertion failure, and the historical-source-lock
failure. The consumed historical OCR experiment keeps its original checksum
and failed outcome. Exact and geometric before/after comparisons are both
retained; no scoring tolerance or acceptance bar changed.

No training, private/sealed read, model revision, activation or package occurred.
Build 433 remains 0.4.33; all four Goal 22 outcomes remain incomplete. Next:
diagnose series fragmentation using measured connection and classifier evidence.
