<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Saved marker errors and native context

Do not start another classifier revision from the previous aggregate failure.
The saved V28-center comparison contains several distinct errors, and its
isolated classifier results do not substitute for the native V27 workflow.
This diagnosis preserves both failed outcomes and every development example.

## What the saved comparison establishes

All 352 per-scene metric records reproduce exactly, covering both classifiers
on 167 component scenes and nine graph-family scenes. No inference or training
is repeated. The attribution calculation takes 3.39 seconds.

| Same V28 centers and fixed 0.10 operating point | V5 | V7 |
| --- | ---: | ---: |
| Component points matched | 1,450 / 2,004 | 1,819 / 2,004 |
| Component extra detections | 53 | 105 |
| Component misses before classification | 81 | 81 |
| Component misses with every nearby center rejected | 473 | 104 |
| Graph-family points matched | 199 / 206 | 199 / 206 |
| Graph-family extra detections | 27 | 29 |

V7 recovers 370 component truths, loses one previously matched truth and leaves
184 missed by both. All 206 family truth outcomes stay identical. The 11
annotated family structure hits remain two brackets, three marker-like
letters, three arrowheads and three legend symbols. These are descriptive
attributions, not new gates.

Of V7's 105 component extras, 53 lie inside the nearest true symbol's nominal
radius. The remaining 52 do not match a recorded structure center or that
radius. Proximity alone does not identify an object or prove duplicate
localization. A fixed contact sheet of the first three distinct scenes in each
group shows overlapping large symbols, connecting-line gaps, brackets,
arrowheads, text and legends. Some apparent marker features are therefore
ambiguous in an isolated crop.

The diagnostic does not remove these cases, change the five-pixel matching
distance, alter probabilities or select another threshold. The small contact
sheet is illustrative; all 2,210 truths and all predictions remain in the
numerical record.

## Actual native workflow

The unchanged native composition ran on all 21 current open development
sources, with 31 authored and observed panels and 838 points. It includes the
real panel, axis, OCR, text/legend exclusion and artifact-filtering stages.
The native combination is V27 centers with V5 classification at threshold
0.10 and balanced enclosed geometry. The ordinary adapter default of 0.25 is
not the operating point of this explicit candidate diagnostic.

The result is 503 matched points, 74 extra detections and 335 misses:
87.18% precision and 60.02% recall. Every source and truth remains counted
despite all 21 sources failing calibration. This population intentionally
lacks many printed x labels; it is not a CSV acceptance fixture and cannot
be compared to the older 23-source export denominator.

Of the misses, 180 lie outside every selected plot polygon and 155 inside.
Only ten overlap an OCR mask. These are locations, not exact rejection-stage
attributions. Native line replay reproduces all 31 saved polygons within
1e-6 pixels in 23.83 seconds without model inference. Two selected vertical
axes are only 11.50 and 16.39 pixels long, even though the source pixels
contain much longer axes. Investigate the existing connected-ink bridge's
short-segment and gap limits next, preserving its actual-pixel requirement.

## Diagnostic runner repair

The first attempt failed the strict input-identity schema and then remained
in native crash handling. Its authenticated images had already been read;
no model inference began. The exact child was stopped and the failed attempt
retained. This is an open-development tooling failure, not a sealed run.

The repaired runner permits an explicitly empty training manifest with a
nonempty development manifest. Original manifest hashes, source ordering,
complete inventories, split labels and truth/mask rejection remain enforced.
Four new source-binding scenarios pass alongside existing checks and seven
synthetic export self-tests. Invalid input now returns structured error JSON
and exit code 2 without creating an output directory. The fresh build has
zero warnings or errors. Build, preparation and native evaluation together
take 177.16 seconds; the native workflow exits 1 for its retained calibration
failures. These results do not constitute acceptance.

The [evidence index](GOAL-22-SAVED-MARKER-ERRORS.json) binds the saved inputs,
executed source, failure, tests and complete attribution.

No application behavior, model, acceptance bar, license, private/sealed access,
production approval or package changes. Code and generated scenes remain
project-owned Apache-2.0 work. Build 433 remains 0.4.33. Goal 22 remains open.
