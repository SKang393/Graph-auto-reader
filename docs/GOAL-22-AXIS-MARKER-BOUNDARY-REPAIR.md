<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Axis extents and points on plot boundaries

The native development workflow recovers 59 previously missed points and loses
one previous match after two connected repairs. All 21 sources, 31 panels and
838 truth points remain counted. The model weights and probability thresholds
are unchanged. These are measured development improvements, not acceptance.

## Failure and repair

Two actual vertical axes collapsed to detected segments only 11.50 and 16.39
pixels high. The existing bridge rejected short terminal segments and gaps
larger than nine pixels before checking whether the source ink connected them.
The repair permits a bounded search across larger symbols and shorter terminal
segments. Collinearity and a connected path through actual original pixels are
still required. Empty gaps remain unbridged and source pixels stay immutable.
The two detected extents become 162.17 and 190.74 pixels.

The first full CSV trial exposed a second defect: an axis shifted by about
0.2 pixels and the strict final plot filter removed a real first-session point.
The next point then correctly failed the session-origin safety check. The
unapproved plot-domain path now retains an observed center within the existing
two-original-pixel geometry tolerance. It does not move the center or change
its numeric interpretation. Image bounds remain enforced, and each retained
out-of-polygon candidate emits a boundary-uncertainty warning.

Phase assignment initially rejected those retained points using its stricter
rectangle. Its input bounds now include the actual retained centers within
the same tolerance and image bounds. Farther points remain invalid. The
calibration axes and point coordinates are unchanged by this adaptation.

The axis stage, marker adapter and cache identify the new processing behavior.
The current native factory verifies the new identity. Historical evidence is
not transferred to the changed pipeline; new acceptance is still required.
Approved V24 processing, model manifests, and all calibration/export safeguards
remain unchanged.

## Native development comparison

| Measurement | Before | After |
| --- | ---: | ---: |
| Matched points | 503 / 838 | 561 / 838 |
| Extra detections | 74 | 82 |
| Missed points | 335 | 277 |
| Point precision | 87.18% | 87.25% |
| Point recall | 60.02% | 66.95% |
| Printed axis corners within two pixels | 80 / 93 | 82 / 93 |
| Panels with all three corners within two pixels | 21 / 31 | 22 / 31 |
| Exactly recognized text regions | 330 / 453 | 330 / 453 |
| Correct text roles | 337 / 453 | 344 / 453 |

The combined native run takes 132.24 seconds. It records 27 boundary warnings
across 13 panels. Every calibration failure remains counted; this population
intentionally omits many printed x labels and is not the CSV fixture.

One tall, narrow plot extends farther into adjacent heading ink. One previously
matched point is lost, eight extra detections are added, and the remaining
marker and OCR errors are substantial. These regressions are retained in the
complete comparison. The unchanged acceptance bars are not met.

## CSV regression check

The unchanged 23-source, 706-point, 724-row workflow retains all 12 baseline
exports and all 11 calibration-review failures. Correct unique values remain
403/706 and correct relational rows remain 399/724. There are zero wrong-scale
rows and zero residual exported rows from failed sources. Fourteen extra
unique points and three wrong phase rows remain. Three exported point records
change across two sources after the geometry repair, but every matched numeric
value stays within the original tolerance. The check takes 148.80 seconds.

The first axis-only attempt lost one previously exportable source. Adding the
marker tolerance exposed phase-boundary rejections on two sources. Both failed
attempts remain recorded; the final phase integration restores all baseline
statuses and aggregate scores without weakening calibration review.

## Validation and evidence integrity

All 76 axis tests and 461 application tests pass. Added scenarios cover wider
connected outlines versus empty gaps, short axis terminals, two-pixel boundary
inclusion versus exclusion, resized-frame coordinates, and unchanged legacy
behavior, plus boundary acceptance and rejection through the actual phase
reasoner. The fresh native runner builds with zero warnings or errors.

The first boundary test run failed four stale identity assertions, while its
five new behavior cases passed. After those assertions were updated, the native
composition correctly rejected a second stale identity in its factory. The
factory was repaired and the successful run used a fresh authenticated build.
Failed attempts remain recorded; no sealed evaluation or training occurred.

An early local diagnostic incorrectly treated session calibration anchors as
printed axis endpoints. Anchors can intentionally lie inside the visible axis.
The corrected geometry comparison uses the generator's actual axis endpoints,
retains all 93 corners, and explicitly supersedes the earlier calculation.

The native per-stage records also reveal that classification used DirectML,
although the workflow declared a CPU-only profile. These paired development
comparisons used the same actual providers, but they cannot serve as CPU-only
acceptance evidence. Enforce the runtime host's provider policy next and rerun
development evidence. Preserve the earlier reports and this limitation.

The [evidence index](GOAL-22-AXIS-MARKER-BOUNDARY-REPAIR.json) binds source,
runtime, model, fixture, score, test, failure, and timing artifacts. The next
diagnostic will locate remaining native marker losses before classification,
at classification, and during text exclusion instead of selecting new weights
from aggregate failure alone.

This is project-owned Apache-2.0 work with existing reviewed dependencies and
models. No private data, sealed data, optimizer step, dependency change,
production activation or packaged build is involved. Build 433 remains 0.4.33.
All four Goal 22 outcomes are not yet jointly verified.
