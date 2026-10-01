<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recover isolated glyphs beside bracket captions

The [frozen diagnosis](GOAL-22-BRACKET-CAPTION-GLYPH-DIAGNOSIS.json) identifies
three missed condition-glyph crops across two fixed model configurations.
They lie beside upper bracket captions, outside the three-heading row used
by existing recovery. Original ink and the bracket survive in each case.

The repair requires a bound, horizontal, unreviewed and nonnumeric caption,
exactly one uncovered compact component, a separating gap, and an actual
original-pixel bracket spanning both. It leaves ambiguous multi-component
groups and protected contexts unchanged. No character or role hint is supplied.
New crops use a separate recognition batch. Failed or empty results are ignored;
a numeric misread remains reviewable and cannot become a calibration tick.
OCR and workflow cache identities advance.

## Fixed workflow evidence

Each unchanged model configuration runs 31 native and 39 CSV panels from the
same owned synthetic inputs as the bound-heading-word checkpoint.

| Native OCR measurement | Retained V44 / English | Closed V46 / server |
| --- | --- | --- |
| Exact readings / 453 | 356 -> 357 | 421 -> 423 |
| Matched regions / 453 | 401 -> 402 | 440 -> 442 |
| Extra regions | 75 -> 75 | 101 -> 101 |
| Character errors / 3100 | 511 -> 510 | 183 -> 181 |
| Correct roles / 453 | 368 -> 369 | 408 -> 410 |

One missed glyph becomes exact in retained V44, and two in V46/server.
No previously correct native reading is lost. Markers remain 742/838 with
26 extras for V44 and 746/838 with 20 extras for V46/server. CSV remains
15 exports, 414 correct values and 409 correct rows for V44; 16 exports,
429 values and 416 rows for V46/server.

Axis geometry, calibrations, initial/post-NMS centers, classifications, accepted
marker properties and text-exclusion membership are unchanged. The audit
records 93 panels with rebound exclusion IDs, 0 otherwise
identical readings with new IDs, and 0 panel/configuration pairs with
changed decoded proposals outside the plot. All existing semantic readings
are retained. Individual new glyphs are recorded in the
[outcome](GOAL-22-BRACKET-CAPTION-GLYPH-RECOVERY.json).

All 866 exported rows are finite, trace to original pixels and have valid
calibration. Numeric rows and source statuses are unchanged; wrong-scale,
duplicate and failed-source residual rows remain zero.

## Verification and timing

`Run-BracketCaptionGlyphChecks.ps1` passes 663 OCR and 542 App
tests, with 16 optional OCR skips. Three builds have zero warnings and errors.
Checks take 123.378 seconds; four fixed comparisons take
797.337 seconds. Sources, tests, runtime,
models, reviewed licenses, observations and scores are checksum bound.

## Remaining work

New changes retain Apache-2.0 headers. No dependencies or model payloads change.
Keep V44/English/V27/V5; V46/server remains closed and unapproved. Merged and
overlapping labels, remaining OCR/marker errors, failed exports, real acceptance
and release gates still prevent Goal 22 completion. No private/sealed reads,
optimizer, activation, packaged build, tag or release occurred. Build 433
remains version 0.4.33.
