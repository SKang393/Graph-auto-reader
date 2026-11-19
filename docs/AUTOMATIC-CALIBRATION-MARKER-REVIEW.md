<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Marker review after automatic calibration fails

On one designated development graph the automatic evaluation produced **zero
of ten reference rows**. The unapproved candidate completed axis detection,
OCR and marker classification. Ambiguous axes, competing numeric readings on
eight X labels and three Y labels, and a first-session conflict prevented
calibration. Phase assignment and export were never reached.

## Automatic results

The workflow received the embedded image alone. No manual anchors, marker
centers, numeric labels, session origin or phase names were supplied to it.
Scoring used the original digitizer centers and a separately stored numeric CSV.
The same original panel and the same model configurations were used both times.

| One panel | Baseline at `cad76a11` | With pixel review |
| --- | ---: | ---: |
| Marker candidates available to review after refusal | 0 | 51 |
| Editable data points | 0 | 0 |
| Valid calibrations | 0 | 0 |
| Exported rows / reference rows | 0 / 10 | 0 / 10 |
| Matched / missing / extra sessions | 0 / 10 / 0 | 0 / 10 / 0 |

Of the 51 candidates, **4** matched a reference center, **47** were extra and
**6** reference centers were missing. Matching was one-to-one within five
original-image pixels. Baseline internal centers were unavailable for scoring
after refusal. Both runs have zero comparable Y values. The reference contains
no phase labels. The baseline took 25.967 s. The run with pixel review took
28.972 s including the WPF review callback.

The checksum-bound evaluation used the production candidate workflow locally
on CPU with one inference thread. Its detector was `graph-scale-coverage-db-head-v46-p1`
0.0.1, its recognizer `PP-OCRv5_server_rec` 5.0.0, its marker-center model
`marker-center-component-diversity-v27` 0.0.1 and its classifier
`graph-marker-classifier` 0.1.0. The ManualPreview Analyze command remained
disabled. This evaluation used zero sealed inputs.

## Review behavior

A recoverable calibration failure now carries the accepted marker centers in
original panel pixels. The review surface verifies their run, project, panel,
image and bounds before offering **Show uncalibrated marker candidates**. The
toggle starts off. Turning it on draws dashed boxes. The next analysis clears
the previous boxes. They are transient and create no project points, session
values, phase assignments or export rows.

A nonactivating Windows WPF window rendered boxes at all 51 supplied locations
on the real panel with zero data rows. An owned native regression exercised the
final opt-in control through its automation peer. Two known boxes appeared at
their original coordinates, disappeared on the second toggle, and left the
point table and calibration empty. Batch regressions check panel identity,
rejected coordinates and preservation of prior review state.

The automatic numeric result on this graph remains a failure. The change
recovers visual evidence for a reviewer and leaves model selection, decision
thresholds and the calibration guard intact.

## Automatic exclusion of divider dashes

On the same development graph, 37 of the 51 accepted marker centers lay along
three dashed phase boundaries. The classifier called individual strokes crosses
and other symbols. Its output fed the session lattice. The existing white-gap
filter protected candidates centered on ink.

The automatic workflow now checks original luminance against the nearest
measured dashed or dotted divider. Exclusion requires one connected, elongated
stroke with visible ends. The measured divider must intersect that stroke, and
a lateral arm or separate ink within the symbol footprint protects the candidate.
Each exclusion carries a marker-specific audit reason. The detector, classifier,
operating threshold and calibration checks used the same configuration in both runs.

| One development panel | Baseline at `8c3798d4` | With dash exclusion |
|---|---:|---:|
| Detected centers before classification | 104 | 104 |
| Accepted centers | 51 | 30 |
| Matched / extra / missing centers | 4 / 47 / 6 | 4 / 26 / 6 |
| Marker precision / recall | 7.84% / 40% | 13.33% / 40% |
| Valid calibrations | 0 | 0 |
| Matched / extra / missing sessions | 0 / 0 / 10 | 0 / 0 / 10 |
| Exported rows / reference rows | 0 / 10 | 0 / 10 |

One-to-one matching within five original pixels retained the same four centers.
The scorer compared outputs with the independent reference after inference. The
candidate measured one X tick and two Y ticks. Both runs stopped at the same
axis-pair ambiguity, competing readings on eight X and three Y labels, and
invalid first-session origin. There were zero comparable Y pairs. Phase
assignment was unreached and the reference supplies no phase labels.

The final run took 39.271 s including the native Review check. The stroke
exclusion stage took 6.07 ms. An offscreen, nonactivating WPF window received
the actual failure and drew all 30 accepted centers at their original positions
through the Review checkbox. The workspace had zero editable points.

Owned regressions cover upright and tilted strokes, crossing symbols,
disconnected ink, clipped or continuous strokes, and absent or misaligned
dividers. The upright and tilted controls both reproduced the defect before
the correction. The focused Windows run passed all 106 tests in two seconds.

```powershell
dotnet test tests/GraphReader.App.Tests -c Release --filter 'FullyQualifiedName~ProductionMarkerStrokeGapExclusionTests|FullyQualifiedName~ProductionAutomaticDetectionAdapterTests|FullyQualifiedName~ProductionMarkerSuppressionRecoveryTests'
```

The reduction of 21 extra centers is exploratory evidence on one graph.
Generalization remains unverified. Marker recall and automatic numeric
extraction remain below acceptance. The evaluation used zero sealed inputs.
Only the aggregate record is retained.

## Axis ambiguity and automatic calibration

On the same development panel, several fits of one continuous stroke competed
as independent axes. The best two pairs shared all 323 horizontal segments.
The leading vertical fit used 51 segments, all of which also supported the
56-segment runner-up. Reapplying the existing angle and distance checks to
the first fit admitted the other five segments.

The fitter now refits each connected candidate until the set of supporting
segments stabilizes. Substitution requires connected support for the original
fit and both ends of its span. An unstable or displaced fit retains its original
candidacy. The 0.08 ambiguity margin still applies to distinct candidates.

| Automatic workflow on one development graph | Baseline at `6dd974f2` | Connected refit |
| --- | ---: | ---: |
| Input line segments | 1,920 | 1,920 |
| Axis pairs within 0.08 of the best, including the best | 23 | 1 |
| Best score minus runner-up score | 0.000132 | 0.133579 |
| Axis pairs requiring review, out of one selected pair | 1 | 0 |
| Measured X / Y tick marks | 1 / 2 | 1 / 2 |
| OCR X / Y tick labels with competing numeric readings | 8 / 3 | 8 / 3 |
| Valid calibrations / panels | 0 / 1 | 0 / 1 |
| Matched / extra / missing marker centers | 4 / 26 / 6 | 4 / 26 / 6 |
| Matched / extra / missing numeric sessions | 0 / 0 / 10 | 0 / 0 / 10 |
| Axis stage wall time | 1.328 s | 1.446 s |
| Automatic workflow wall time | 26.971 s | 25.930 s |

Both runs used the same image, model configuration and detected segments.
Marker precision was 13.33% and recall was 40.00% within five original
pixels. The workflow refused calibration because of conflicting OCR evidence
and an invalid first-session origin. There were zero numeric rows to compare
against the independent reference, so Y error is undefined. Neither run
reached phase assignment. Times are single observations and exclude the
subsequent native Review check.

In an offscreen Windows WPF Review window, the failure exposed all 30
accepted centers at their original positions, with zero editable rows and
no calibration. The window stayed inactive. This exercised the source-built
candidate on one development graph. Generalization and packaged acceptance
remain unverified.

The project-owned geometry regression reproduced the original ambiguity in
both input orders. With the refit, both resolve, and a separate competing axis
still requires review. A production-adapter control with recorded line segments,
OCR labels and marker centers returned both expected sessions and Y values
within `1e-9`. The Windows axis suite and all 61 production automatic-detection
adapter tests passed.

## Narrow glyph orientation in the printed X row

The detector uses the long edge of a text box to estimate orientation. An
upright digit can have a nearly vertical long edge. On one development panel,
the recognizer sampled such a digit sideways even though it lay in the same
horizontal text row as six other printed X labels. Refining the box against
the original pixels had discarded the distinction between a box-derived angle
and a specified text direction.

The proposal now retains the source of its angle through refinement. A
narrow X-lane crop can stay upright when two disjoint horizontal peers contain
its vertical extent. Explicit rotations retain their specified direction.
The recognizer still reports alternative strings, and the calibration stage
continues to check competing readings and session origin.

| Image-only development run | Baseline at `df38136d` | With row orientation |
| --- | ---: | ---: |
| Primary X readings matching the seven visible printed labels | 6 / 7 | 7 / 7 |
| X-role proposals outside the printed row | 3 | 3 |
| X / Y numerical fit inliers | 6 / 6 | 7 / 6 |
| Measured X / Y strokes | 1 / 2 | 1 / 2 |
| X / Y labels with competing numeric readings | 8 / 3 | 8 / 3 |
| Valid automatic calibrations / panels | 0 / 1 | 0 / 1 |
| Matched / extra / missing marker centers | 4 / 26 / 6 | 4 / 26 / 6 |
| Matched / extra / missing numeric sessions | 0 / 0 / 10 | 0 / 0 / 10 |
| OCR wall time | 18.070 s | 17.116 s |
| Automatic workflow wall time | 26.143 s | 26.444 s |

Printed-label agreement was checked against the visible axis. Numeric rows
were scored separately against the independent CSV after extraction. The
first detected marker column still conflicted with the automatic session-one
policy. The entered first-visible session and literal printed-X workflows
require explicit review. No origin override was supplied.

There were zero comparable Y values and no phase assignments. In native
Windows Review the actual refusal showed all 30 candidate centers at their
original positions, zero editable rows and no calibration. The window stayed
inactive. General OCR and numeric accuracy remain unverified beyond this
single development panel.

Owned regressions checked the recognizer's actual crop bytes for horizontal
peers, overlapping or misaligned peers, and explicit or box-derived rotations.
The local Windows OCR suite passed 943 tests and skipped 16. Seven focused
application tests passed, including genuine OCR conflicts, printed-coordinate
review and the native marker overlay.

## Corroborating same-crop digit alternatives

The local CTC decoder generates alternatives by substituting the runner-up at
one time step of the same recognition tensor. A disagreement between two
recognizers previously produced the same calibration warning as a weak
single-frame edit. The decoder now retains the product of the normalized
greedy probabilities across all frames, including blanks and repeats. Under
the CTC model this bounds the probability mass of the greedy transcript from
below. It measures support within one model output.

For unreviewed original-image integer labels, calibration can resolve a
single-digit insertion or deletion when the greedy alignment has at least 0.95
mass and two other original label crops bracket it on the same axis. All three
must have intact single-read evidence and belong to a valid fit. Interpolation
over the full crop intervals must admit the primary and exclude every disputed
value. Independent recognizer merges invalidate the single-read evidence.
Endpoints, uncertain reads and competing scales retain the review gate.
The raw alternatives remain available.

| One nonsealed development panel | Baseline at `275cb0e9` | With corroboration |
| --- | ---: | ---: |
| Matching primary printed X labels | 7 / 7 | 7 / 7 |
| Measured tick strokes, X / Y | 1 / 2 | 1 / 2 |
| Fit inliers, X / Y | 7 / 6 | 7 / 6 |
| Blocking competing readings, X / Y | 8 / 3 | 3 / 1 |
| Raw hypotheses for 16 tick-role regions, including primaries | 48 | 48 |
| Valid automatic calibrations | 0 / 1 | 0 / 1 |
| Matched / extra / missing centers, 5-pixel radius | 4 / 26 / 6 | 4 / 26 / 6 |
| Matched / extra / missing numeric sessions | 0 / 0 / 10 | 0 / 0 / 10 |
| OCR / automatic workflow wall time | 17.851 / 26.060 s | 20.568 / 31.202 s |

The remaining disputed readings comprise three axis endpoints and one off-row
proposal. Marker precision was 13.33% and recall was 40%. Scoring after
image-only extraction located one missing reference center before the first
accepted column. It survived center detection and classification, then an OCR
annotation mask excluded it. The session-first origin gate correctly stopped
projection. There were zero comparable Y values or phase rows.

On Windows, an inactive WPF Review window bound the actual refusal to the
imported panel. Toggling the overlay displayed all 30 candidate boxes at the
reported centers and left calibration empty with zero editable rows. A
separate controlled adapter test yielded both expected X/Y rows and export
evidence. Its negative controls covered near ties, independent readings,
same-length changes, off-lane labels, endpoints and overlapping value
intervals. The focused Windows runs passed 51 OCR tests and 68 application
adapter tests, with 12 calibration cases rechecked on the final source.
The development figures are single-run measurements on one panel.

## Filled circles inside mixed-symbol OCR annotations

An OCR annotation can cover a row of plotted symbols. When a recognizer
renders an open circle as a letter or digit, the mixed reading can acquire
a text mask. Polygon containment alone then excludes a classified solid
marker along with the text.

Text exclusion now checks individual filled-circle candidates in unreviewed
mixed geometric-symbol annotations. The original grayscale patch must contain
a solid core and a round boundary. The disk must occupy at least half the
annotation height and agree in radius within one pixel with a separately
classified disk in the plot. The witness patch must be clear of text masks
and legend frames. The measured center stays at the detector's original
position. A retained overlap carries a review warning. Other overlapping text
remains eligible to exclude the same candidate.

| One nonsealed development panel | Baseline at `09444614` | With disk corroboration |
| --- | ---: | ---: |
| Classified centers excluded as text | 62 | 61 |
| Matched / extra / missing centers within 5 pixels | 4 / 26 / 6 | 5 / 26 / 5 |
| Reference precision / recall | 13.33% / 40% | 16.13% / 50% |
| First accepted column satisfies session-one check | No | Yes |
| Blocking OCR conflicts, X / Y | 3 / 1 | 3 / 1 |
| Valid automatic calibrations | 0 / 1 | 0 / 1 |
| Matched / extra / missing numeric sessions | 0 / 0 / 10 | 0 / 0 / 10 |
| Text-exclusion time | 3.67 ms | 15.65 ms |
| OCR / workflow wall time | 18.732 / 27.711 s | 17.628 / 26.927 s |

Post-extraction scoring found one new matching center and all prior centers
at their original positions. The earliest reference center was recovered
within 1.81 pixels. All OCR polygons, readings, alternatives and masks were
identical across runs. Three endpoint readings lacked bracketing labels, and
the off-row proposal contained a fragment of text below the printed tick row.
The session-column RMS residual was 2.811 pixels against the configured
2.044-pixel tolerance. Calibration required review. No Y errors or phase
assignments could be scored.

The Windows regression run passed 53 marker and calibration cases. The final
text-exclusion suite passed 23 cases, including a masked disk on a
connecting line, hollow letters, stems, small dots, ordinary text, reviewed
annotations, and missing, masked, differently sized or framed witnesses. Native
WPF Review displayed the actual refusal and all 31 candidate centers with zero
editable rows. These timings and accuracy measurements cover one development
panel.

## Binding printed X labels to attached strokes

The calibration stage checks original grayscale pixels for short strokes
extending continuously from the fitted X axis, with axis ink beside each
stroke and a visible end below it. A printed row requires three or more
nonoverlapping numeric label boxes at distinct strokes with a shared vertical
band relative to the axis. A label can bind a unique stroke inside its own
horizontal footprint. Multiple strokes under the same label require review.

A small, unreviewed off-row reading enclosed in a larger masked, nonnumeric
word can be withheld from the fit inside the corroborated row's horizontal
span. Review retains both OCR regions and all readings. A line proposal wholly
on the Y spine cannot supply an unlabeled X observation without separate
outward ink. The scan supplies measured positions to associated printed
labels. It does not add every minor stroke as a separate RMS observation.

| One nonsealed development panel | Baseline at `fcee2048` | With attached row |
| --- | ---: | ---: |
| Printed X labels bound to original strokes | 0 / 7 | 7 / 7 |
| Off-row labels given stroke positions | 3 | 0 |
| Measured Y-label associations | 6 | 6 |
| X fit inputs / inliers | 10 / 7 | 8 / 7 |
| Blocking numeric conflicts, X / Y | 3 / 1 | 2 / 1 |
| Pooled column RMS / tolerance (pixels) | 2.811 / 2.044 | 2.345 / 2.046 |
| Printed-column RMS at selected origin and pitch (pixels) | 0.905 | 0.629 |
| Marker-column RMS at selected origin and pitch (pixels) | 2.890 | 2.706 |
| Center matches / extra / missing within 5 pixels | 5 / 26 / 5 | 5 / 26 / 5 |
| Valid calibrations / numeric sessions matched | 0 / 0 of 10 | 0 / 0 of 10 |
| Tick association / OCR / workflow time | 41.79 ms / 19.114 s / 28.215 s | 52.24 ms / 17.726 s / 26.557 s |

The new scan found 71 outward stroke candidates. Two off-row readings were
nested text fragments. One other off-row proposal remained a robust-fit
outlier. Three endpoint disputes still lacked bracketing observations. The
lattice included one fewer unlabeled proposal after the spine check.

Rebuilding the scored columns from original-pixel positions, the production
1.5-pixel duplicate tolerance, and capped confidence weights reproduced both
RMS results. Original pixels showed 16 accepted centers on dashes of three
vertical dividers. These formed four alignment groups. They accounted for
66.5% of baseline weighted squared error, and the spine proposal accounted
for another 22.3%. The same divider groups accounted for 93.1% after the
correction. The placement error remained above the unchanged 12%-of-pitch
limit.

Post-extraction reference scoring gave 16.13% marker precision and 50% recall.
All 31 centers, OCR readings, and masks agreed across runs. The recovered
first-session center remained within 1.81 pixels of the reference. Ten numeric
sessions remained missing, with zero extra sessions and no comparable Y or
phase rows.

The focused Windows run passed 59 cases. Controls covered detached marks,
missing axis ink, staggered labels, multiple strokes, genuine corner ticks,
partial word overlaps, reviewed values, and endpoint conflicts. A controlled
fit projected the prescribed X and Y values and required review when the
fragment or endpoint lacked sufficient evidence. Native WPF Review displayed
the actual refusal with 31 candidate boxes and zero editable rows. These are
single-panel development measurements.

## Repeated divider ink in marker proposals

Of the 16 retained dash centers identified above, 14 lay on complete, isolated
strokes that failed the projected aspect-ratio check. A few edge pixels or
a small slope difference can increase the projected width of an otherwise
uniform stroke. One remaining candidate lacked a measured divider through the
center, and the other had multiple ink runs on one scanline.

The additional original-pixel check uses the median scanline width only when
the candidate and the two nearest flanking strokes follow the same measured
dashed or dotted divider. It requires blank gaps, comparable lengths and
spacing on both sides, contiguous ink and a matching stroke width. A narrower
terminal row can taper into its neighbor. Split runs, disconnected tips,
off-axis bodies and widened symbols remain eligible for Review.

| One nonsealed development panel | Baseline at `ab0e5844` | With repeated-stroke check |
| --- | ---: | ---: |
| Retained dash centers / occupied column groups | 16 / 4 | 8 / 4 |
| All retained centers | 31 | 23 |
| One-to-one matches / extra / missing within 5 pixels | 5 / 26 / 5 | 5 / 18 / 5 |
| Marker precision / recall | 16.13% / 50% | 21.74% / 50% |
| First observed session / center error (pixels) | 1 / 1.801 | 1 / 1.801 |
| Printed X-label stroke associations / measured Y-label associations | 7 / 6 | 7 / 6 |
| Blocking endpoint conflicts, X / Y | 2 / 1 | 2 / 1 |
| Column RMS / tolerance (pixels) | 2.345 / 2.046 | 2.233 / 2.046 |
| Valid calibrations / matched numeric sessions | 0 / 0 of 10 | 0 / 0 of 10 |
| Stroke exclusion / full workflow time | 3.81 ms / 26.342 s | 8.93 ms / 28.295 s |

Extraction received only the image. All eight removed centers coincided with
previously identified divider dashes. Independent reference scoring found
the same five matched centers in both runs.
All four affected column groups still had at least one retained dash. The
lattice pitch stayed at 17.05 pixels, with the same 12%-of-pitch tolerance.
Ten reference sessions remained missing, with zero extra sessions and no
comparable Y or phase values. Timings are single observations.

The 99 focused Windows cases passed, including straight and tilted dash-dot
patterns, tapered ends, missing or displaced neighbors, and filled, open and
crossing symbols. A separate inactive native WPF window displayed the actual
calibration refusal. Toggling the candidate overlay drew all 23 boxes at
their expected positions while the panel had zero editable points.
