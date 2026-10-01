<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Separate detached header glyphs from neighboring words

The [frozen diagnosis](GOAL-22-SEPARATED-HEADER-GLYPH-DIAGNOSIS.json) shows
composite OCR boxes containing a participant or phase word and a separately
spaced condition glyph. The original ink contains a 16- or 17-pixel gap,
but recognition merges the labels into one string.

The repair requires a bound, horizontal, unreviewed original-pixel header with
an already recognized single-letter boundary token. One compact edge component
must be separated from its neighboring word by more than the shorter ink
height. Ambiguous candidates, protected contexts, human decisions and competing
text remain untouched. Glyph and word use separate recognition batches, with
no supplied character or role. Both reads must succeed and be nonempty before
replacing the composite. Numeric misreads cannot become calibration ticks.
Existing recognition batches remain unchanged and cache identities advance.

## Fixed workflow evidence

Each fixed model configuration runs the same 31 native and 39 CSV panels as
the marker-glyph checkpoint, using only owned synthetic inputs.

| Native OCR measurement | Retained V44 / English | Closed V46 / server |
| --- | --- | --- |
| Exact readings / 453 | 357 -> 358 | 423 -> 427 |
| Matched regions / 453 | 402 -> 403 | 442 -> 444 |
| Extra regions | 54 -> 54 | 49 -> 49 |
| Character errors / 3100 | 489 -> 485 | 129 -> 123 |
| Correct roles / 453 | 369 -> 370 | 410 -> 412 |

V44 replaces 1 native composite and gains 1
correct readings. V46/server replaces 2 and gains
4. No earlier correct reading is lost. CSV has
0 and 0 replacements respectively.
Each actual replacement matches both measured child crops in the frozen
diagnosis. Unrelated semantic regions and masks are unchanged.

Markers remain 742/838 with 26 extras for V44 and 746/838 with 20 extras for
V46/server. CSV remains 15 exports, 414 correct values and 409 correct rows
for V44; 16 exports, 429 values and 416 rows for V46/server. All 866
exported rows retain finite original-pixel traces and valid calibration.
Wrong-scale, duplicate and failed-source residual rows remain zero.

Axis geometry, calibration, initial/post-NMS centers, classification, accepted
marker properties and exclusion membership are unchanged. The audit records
93 panels with rebound exclusion IDs, 0 otherwise identical
readings with new IDs, and 0 panel/configuration pairs with changed
decoded proposals. Full details are in the
[outcome](GOAL-22-SEPARATED-HEADER-GLYPH-RECOVERY.json).

## Verification and timing

The first check passed 674 OCR tests and failed ten new fixture cases. The
fixture drew its word as one solid component wider than the detector's
existing 15-percent image-width limit. The repair draws separate glyph
components across the same word extent. Recovery code is unchanged. Failed
evidence is retained and no model evaluation used that check.

`Run-SeparatedHeaderGlyphChecks.v2.ps1` passes 684
OCR and 562 App tests, with 16 optional OCR skips.
Three builds have zero warnings and errors. Checks take
131.608 seconds; four fixed comparisons take
901.584 seconds. Sources, tests, runtime,
models, licenses, observations and scores are checksum bound.

## Remaining work

Code retains Apache-2.0 headers; no dependencies or model payloads change.
Keep V44/English/V27/V5; V46/server remains closed and unapproved. Tight and
overlapping labels, other OCR/marker errors, failed exports, real acceptance
and release gates still prevent Goal 22 completion. No private/sealed reads,
optimizer, activation, packaged build, tag or release occurred. Build 433
remains 0.4.33.
