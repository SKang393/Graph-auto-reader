<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recover enclosed marker centers, 2026-09-21

The application now measures enclosed original-pixel interiors as additional
marker candidates. Correct detections increase from **666 to 721 of 838**, with
all 666 previous matches retained. False detections increase from 17 to 27.
The full CSV check improves from 403 to 408 correct unique-value matches and
399 to 404 correct relational rows, with no new source failures. Accuracy
acceptance and Goal 22 remain incomplete.

## Cause and implementation

One dense owned development source contains 148 points and previously misses
104. A saved-stage audit finds 43 misses without a proposal within five pixels
and 40 near rejected classifier proposals. Other misses involve suppression,
text exclusion or competing nearby truth points. These proximity categories
locate losses; they are not causal proofs. Original-pixel crops show hollow
glyphs connected by lines, which prevents whole-ink-component isolation.

The new recovery pass measures each small enclosed background region using
the existing 0.12 ink threshold and four-connected background. Its bounding
box supplies the center and radius. Search excludes image-edge background,
oversized or tiny interiors, recognized text, measured legend frames and
points outside the plot. Existing centers remain unchanged. Every addition
still passes the existing classifier at cutoff 0.5 and duplicate suppression.

Candidates carry `NeedsReview` internally and disclose uncalibrated geometric
confidence. The frozen domain represents them as `Unreviewed`, retaining
confidence and provenance. The adapter identity includes the recovery version;
original pixels, cancellation, calibration and scientific export guards remain.
No model, acceptance bar or production-approval switch changes.

## Verification

| Measurement | Before | After |
| --- | ---: | ---: |
| Native correct points | 666/838 | 721/838 |
| False / missing points | 17 / 172 | 27 / 117 |
| Native precision / recall | 97.51% / 79.47% | 96.39% / 86.04% |
| Exact text readings | 345/453 | 345/453 |
| Full-workflow exports / failures | 12 / 11 | 12 / 11 |
| Correct unique-value matches | 403/706 | 408/706 |
| Correct relational rows | 399/724 | 404/724 |
| Extra unique-value matches | 14 | 11 |

All **494 App tests pass**, including eight new scenarios covering connected
outlines, open/image-edge outlines, component size, exclusions, duplicates,
immutable pixels, padded stride, transformed input and cancellation. The native
harness builds with zero warnings or errors. Checks take 88.49 seconds; native
inference/scoring takes 147.94 seconds; full CSV verification takes 158.90 seconds.
All observed model calls use CPU.

Every prior exported numeric value remains; one source adds two physical rows.
The existing canonical series/point scorer counts five additional correct
matches. This report does not attribute all five to new detections. Source
states/errors and upstream OCR/axis observations are unchanged. Three explicit
unknown phase rows remain counted. There are zero wrong-scale, duplicate or
failed-source residual rows. The separate [phase diagnosis](GOAL-22-REMAINING-PHASE-DIAGNOSIS.md)
preserves the existing reasoner behavior.

The first isolated diagnostic is void: an empty classifier batch reports an
unused provider descriptor, causing an overbroad CPU assertion. Its failed
process was stopped. A preserved retry checks CPU on actual inference and
catches exceptions cleanly; it completes in 11.36 seconds and predicts the
same 721/838 result. No sealed budget is consumed. Earlier evidence remains.

Validation uses the App test project, the native tool's
`--run-frozen-candidate-synthetic` operation and its
`--score-frozen-workflow-csv` operation. Exact sources, binaries, commands,
fixtures, all 23 workflow sources and result hashes are bound in the
[evidence record](GOAL-22-ENCLOSED-CENTER-RECOVERY.json).

## Limits and next work

Enclosed text and arrowheads can still create false points. Recall remains
below 95%, and this is open synthetic development evidence. Continue remaining
marker and OCR coverage, then sealed prerequisites, real-data/Chandler checks,
promotion and the final installer/portable verification. No inference result
here approves a model or completes any missing Goal 22 outcome.

Code is Apache-2.0 and uses existing reviewed dependencies and weights. No
private/sealed read, optimizer step, new import, package or release occurred.
Build 433 remains 0.4.33. Source checkpoint e6c56b0 passed CI 35638527641 before
this repair. No subagent was used under the maintainer instruction.
