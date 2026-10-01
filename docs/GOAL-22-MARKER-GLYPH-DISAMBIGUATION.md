<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Resolve OCR glyphs backed by plotted markers

The [frozen diagnosis](GOAL-22-MARKER-GLYPH-DISAMBIGUATION-DIAGNOSIS.json)
shows plotted markers also appearing as ambiguous annotation text. Existing
logic withholds their text masks, so marker detection succeeds, but both
interpretations remain in the semantic input.

The repair resolves only canonically flagged, unmasked, unreviewed original-pixel
glyphs. A recognized accepted marker must lie inside the actual OCR polygon,
and its square must cover at least half the OCR bounding area. The earlier
minimum-area shadow was rejected because a small false marker could hide a
wider misread word. The revised shadow preserves those words.

Original text, alternatives, confidence and geometry remain as rejected OCR
evidence in Review. Downstream semantic reasoning receives the resolved text.
The derived cache identity and marker-stage provenance record the associations.
Inference, recognition batches, masks and classifier thresholds are unchanged.

## Fixed workflow evidence

Each unchanged model configuration runs 31 native and 39 CSV panels from the
same owned synthetic inputs as the bracket-caption checkpoint.

| Native OCR measurement | Retained V44 / English | Closed V46 / server |
| --- | --- | --- |
| Exact readings / 453 | 357 -> 357 | 423 -> 423 |
| Matched regions / 453 | 402 -> 402 | 442 -> 442 |
| Extra regions | 75 -> 54 | 101 -> 49 |
| Character errors / 3100 | 510 -> 489 | 181 -> 129 |
| Correct roles / 453 | 369 -> 369 | 410 -> 410 |

Native semantic input loses 21 false glyph readings in V44
and 52 in V46/server. CSV input loses
3 and 4 respectively. Every removed
reading matches the frozen v2 shadow; none touches text truth. No correct
reading or role is lost, and no new semantic reading is added.

Markers remain 742/838 with 26 extras for V44 and 746/838 with 20 extras for
V46/server. CSV remains 15 exports, 414 correct values and 409 correct rows
for V44; 16 exports, 429 values and 416 rows for V46/server.

Axis geometry, calibration, initial/post-NMS centers, classification, accepted
marker properties and text-exclusion membership are unchanged. The audit
records 93 panels with rebound exclusion IDs, 0 otherwise
identical readings with new IDs, and 0 panel/configuration pairs with
changed decoded proposals. All 866 exported rows remain finite,
trace to original pixels and have valid calibration. Numeric rows and source
statuses are unchanged; wrong-scale, duplicate and failed-source residual rows
remain zero.

## Verification and timing

The first check passed 561 tests and failed one integration test because
the new provenance entry used the OCR stage after markers had run. The retry
changes that entry to the marker stage; the glyph predicate is unchanged.
The failed evidence is retained and no model evaluation used that build.

`Run-MarkerGlyphDisambiguationChecks.v2.ps1` passes 562
App tests with no skips. App and native-runner builds have zero warnings and
errors. Checks take 119.559 seconds; four fixed comparisons
take 1000.657 seconds. Sources, tests, runtime,
models, reviewed licenses, observations and scores are checksum bound in the
[outcome](GOAL-22-MARKER-GLYPH-DISAMBIGUATION.json).

The integration test verifies original OCR evidence in the Review projection,
two actual exported rows and preservation of the source image. Frozen workflow
observations occur before that projection and are not presented as full
Review-audit verification for every synthetic case.

## Remaining work

New changes retain Apache-2.0 headers. No dependencies or model payloads change.
Keep V44/English/V27/V5; V46/server remains closed and unapproved. Exact text
accuracy, marker recall, failed exports, real acceptance and release gates
still prevent Goal 22 completion. No private/sealed reads, optimizer,
activation, packaged build, tag or release occurred. Build 433 remains 0.4.33.
