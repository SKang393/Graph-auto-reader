<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Complete header groups with bound heading words

The [frozen diagnosis](GOAL-22-BOUND-HEADER-WORD-DIAGNOSIS.json) measured a
30-pixel connected component where the standalone-glyph width limit was 24.
That split `Shared` from an already recognized `Baseline` region. The repair
lets a bound, horizontal, unreviewed phase-heading word contribute its measured
geometry to the group. The recognizer reads the resulting original-pixel crop;
the implementation never concatenates recognized strings.

All existing grouping, divider, review, semantic-role and failure guards remain.
The group still requires at least two recovered fragments. Other roles and
reviewed words cannot become anchors. The composition version advances so OCR
and workflow caches invalidate the earlier result.

## Fixed workflow evidence

Each unchanged model configuration runs 31 native and 39 CSV panels from the
same owned synthetic inputs as the previous word-recovery checkpoint.

| Native OCR measurement | Retained V44 / English | Closed V46 / server |
| --- | --- | --- |
| Exact readings / 453 | 355 -> 356 | 421 -> 421 |
| Matched regions / 453 | 400 -> 401 | 440 -> 440 |
| Extra regions | 77 -> 75 | 101 -> 101 |
| Character errors / 3100 | 540 -> 511 | 183 -> 183 |
| Correct roles / 453 | 367 -> 368 | 408 -> 408 |

`Shared Baseline` is newly exact in retained V44. No previously correct native
reading is lost. Markers remain 742/838 with 26 extras for V44 and 746/838 with
20 extras for V46/server. CSV remains 15 exports, 414 correct values and 409
correct rows for V44; 16 exports, 429 values and 416 rows for V46/server.

Axis geometry, calibrations, initial/post-NMS centers, classifications, accepted
marker properties and text-exclusion membership are unchanged. The audit
separately records 93 panels with rebound exclusion IDs,
5 otherwise identical readings with new IDs, and 0 panel/configuration
pairs with changed decoded proposals outside the plot. No changed proposal
survives into retained centers. Changed word contents and constituent readings
are reported explicitly in the [outcome](GOAL-22-BOUND-HEADER-WORD-RECOVERY.json).

All 866 exported rows are finite, trace to original pixels and have valid
calibration. Numeric rows and source statuses are unchanged; wrong-scale,
duplicate and failed-source residual rows remain zero.

## Verification and timing

`Run-BoundHeaderWordChecks.ps1` passes 639 OCR and 542 App tests, with 16
optional OCR skips. Three builds have zero warnings and errors. Checks take
122.229 seconds; four fixed comparisons take
787.607 seconds. Sources, tests, runtime,
models, reviewed licenses, observations and scores are checksum bound. A local
audit variable-name collision was corrected before publication; the failed
attempt is preserved and no model workflow was repeated.

## Remaining work

New changes retain Apache-2.0 headers. No dependencies or model payloads change.
Keep V44/English/V27/V5; V46/server remains closed and unapproved. Remaining
OCR/marker errors, failed exports, real acceptance and release gates still
prevent Goal 22 completion. No private/sealed reads, optimizer, activation,
packaged build, tag or release occurred. Build 433 remains version 0.4.33.
