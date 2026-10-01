<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Read recovered header fragments as words

The [frozen diagnosis](GOAL-22-HEADER-FRAGMENT-WORD-DIAGNOSIS.json) identified
missing headings being recovered as disconnected letter crops. The repair groups
measured original ink in the existing header band and reads it in one separate
word crop. At least two recovered fragments must support each group. Existing
recognition batches remain unchanged. Only successful nonempty results replace
the fragments; failures keep their readings available and retry normally.

Reviewed text, other semantic roles, unbound detections, partial overlaps and
crossed phase dividers prevent grouping. A numeric reread cannot enter axis
calibration. Successful words remain unreviewed with replacement provenance.
The cache also distinguishes unavailable and measured-empty divider context
whenever header recovery is enabled.

## Fixed workflow evidence

Each unchanged model configuration runs 31 native and 39 CSV panels from the
same owned synthetic inputs as the preceding crop-completion checkpoint.

| Measurement | Retained V44 / English | Closed V46 / server context |
| --- | --- | --- |
| Native exact readings / 453 | 353 -> 355 | 419 -> 421 |
| Matched text regions / 453 | 397 -> 400 | 438 -> 440 |
| Extra text regions | 104 -> 77 | 113 -> 101 |
| Character errors / 3100 | 625 -> 540 | 241 -> 183 |
| Correct roles / 453 | 365 -> 367 | 406 -> 408 |
| Matched markers / 838 | 742 -> 742 | 746 -> 746 |
| Extra markers | 26 -> 26 | 20 -> 20 |
| Completed CSV sources / 23 | 15 -> 15 | 16 -> 16 |
| Correct CSV values / 706 | 414 -> 414 | 429 -> 429 |
| Correct CSV rows / 724 | 409 -> 409 | 416 -> 416 |

No earlier correct native reading is lost. The outcome records 6 successful
word groups and their previous readings. Retained V44 recovers `Criterion 3` and
`Alternating Treatments`; a crowded heading still reads `Interventions continlegbe`
and a separate group reads only `Shared`. These limitations remain visible.

Axis geometry, calibration, initial/post-NMS centers, classifications, accepted
marker properties and text-exclusion membership remain unchanged. The audit
reconstructs geometry for 93 panels whose exclusion IDs differ. It also
records 2 panel/configuration pairs with changed pre-refinement decoded
proposals; all changed points are outside the plot bounds. These differences do
not survive into retained centers.

CSV source statuses and numeric rows are unchanged. All 866 exported rows
are finite, trace to original pixels and carry valid calibration. Wrong-scale,
duplicate and failed-source residual rows remain zero. Existing phase errors and
failed exports remain reported.

## Verification and timing

`Run-HeaderFragmentWordChecksV4.ps1` runs the OCR and App suites and builds the
native runner using the reviewed OpenCV library. 635 OCR and 542 App tests pass;
16 optional OCR tests are skipped. Three builds have zero warnings and errors.
Checks take 122.148 seconds; four workflow comparisons take
827.872 seconds. The
[outcome](GOAL-22-HEADER-FRAGMENT-WORD-RECOVERY.json) binds sources, runtime,
tests, inputs, model licenses, observations, scores and complete local audits.

Three earlier attempts are preserved: a return-type syntax error, a concrete
dictionary analyzer requirement, and two incorrect cache expectations in new
failure fixtures. The last repair changes only tests; failures correctly retry
instead of being cached.

## Scope and remaining work

New code retains Apache-2.0 headers. Dependencies and model payloads are unchanged.
V44/English/V27/V5 remains retained; V46/server stays closed and unapproved.
Two-heading layouts, bracket captions, connected words outside the component
envelope, remaining OCR/marker errors and failed exports require further work.
Goal 22 acceptance remains incomplete. No private/sealed reads, training,
production activation, packaged build, tag or release occurred. Build 433 remains
version 0.4.33.
