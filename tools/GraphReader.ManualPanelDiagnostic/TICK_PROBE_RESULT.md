<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Exploratory tick-rank probe

The diagnostic groups nearby axis-pair candidates, counts short horizontal
line evidence near each candidate vertical axis, and looks for a regularly
spaced run of at least four positions. It considers only candidates near the
baseline geometric score and with nontrivial reported plot extents. This rule
was developed after inspecting the eight build images and five earlier
holdout images. It does not change application axis selection.

On the eight build images, the probe selected the existing baseline in all
eight; the baseline and selected axes were each within five pixels of the
manual calibration at the evaluated intersection. On the six panels from the
five already exposed holdout images, the probe changed one selection from a
phase divider to the visible Y axis. It did not repair the two panels with very
short detected horizontal axes or the other visibly misplaced axis. These
observations are exploratory and are not independent validation or a release
pass. No optimizer ran and no sealed payload was opened.

The private build panel report SHA-256 is
`2b8e8c5d64d5cdfe9419ab2f66e5535780ae800a332bc3aadd3e5f307c5b7e9c`,
build predictions are
`cb2a78a243723dbdf1027abca87299cdfb3c4bc6d771111430d31ff5a77fa554`,
build score is
`f11a63c7c9992a50165d8fa1bcc3990791bc5e17072e27d1d57bcbd94e453f0b`,
and exposed holdout predictions are
`c81ea2d1928d79eeb0a7a355355fc72a5f151de2774e9e6914ea54a6d476bc29`.
The first build-score command wrote its score but exited nonzero when printing a
NumPy integer. The corrected scorer wrote the v2 score in a fresh path. The
subsequent registration changes input selection only; a direct comparison
recomputed all 14 probe selections and found them unchanged.

The diagnostic build succeeded with zero warnings and errors. Three synthetic
tests passed, covering regular evidence, rescue of an alternate axis, and
rejection of short or insufficiently supported alternatives. Predictions and
per-image geometry remain private project-local artifacts for the active axis
investigation.

## Seven additional training-source groups

The registered probe and panelizer predictions were frozen in
`FRESH_FREEZE.json` before the seven source images were visually reviewed for
this probe. The probe selected the baseline on all seven panels. Afterward, an
assistant selected a narrow window on each visible Y axis; the darkest mean
column in that window served as a reference at the window's midpoint. Both
baseline and probe were within five pixels on all seven, with a maximum
difference of 1.73 pixels. The private review report SHA-256 is
`8d21f0a929cf2f741d63e3ee8566ea39ee37ccc3455e73b6711a6f2c6bb4f805`.

This checks one horizontal position per visible Y axis. It does not verify axis
endpoints, calibration, OCR, markers, or the full workflow. The assistant chose
the reference windows after prediction and the review was not independently
adjudicated. No selection changed, so this check measures observed regression
only; it cannot demonstrate a benefit. Prior source exposure and historical or
sealed overlap remain unverified. The probe remains outside production.

## OCR follow-up on exposed images

Applying the same experimental tick rule to the repaired panel geometry, then
using the unchanged label proposal and official OCR reader, produced 46 exact
reads among 52 reviewed labels. Six labels were missed; 16 proposals were
unmatched, including two with numeric text. The same repaired geometry without
tick reranking produced 35 exact reads and 17 misses. The tick rule was designed
with these images already visible, so this is a development measurement, not a
blind or independent acceptance result. The unmatched numeric proposals were
not adjudicated as false positives.

The private OCR input SHA-256 is
`469b5ee37f1d2ad80b4dcb638a3d2183c4949e003de3322d9a186e140228779a`,
predictions are
`405e22bc6b700570aa2068fd01388ba39add23a869e85a18871041dd25cd6c68`,
and the aggregate score is
`e6cacc3acdabc8fa2cf8aa39939536701315a475a7b78e9ddef7c6f4b5f99c21`.
CPU inference performed no optimizer steps. The tick rule and OCR composition
remain unapproved and are not enabled in the production workflow.

## Study-partitioned development review

The fixed probe and panelizer predictions were frozen before calibration anchors
were exported. All 16 images had one reported panel, and the probe selected the
baseline for all 16. The images came from five development studies with no
exact overlap in the current training-image manifest; historical and sealed
overlap remain unknown. No sealed payload was read and no optimizer ran.

A calibration extrapolation to graph X = 0 is **not** a visible-axis accuracy
metric. Six of the graphs with a visibly drawn vertical axis begin at session 1,
and three have no drawn vertical axis. A previous 7/16 within-five-pixels count
against that extrapolation was therefore invalid and must not be used.

After seeing the predictions, an assistant selected narrow windows on the 13
visible vertical axes. The darkest mean column in each window was used as the
reference at that window's midpoint. Both the baseline and experimental probe
were within five pixels for all 13, with maximum distance 1.59 pixels. The
other three graphs were marked as having no visible vertical axis; all three
were flagged for review by the detector, including a partial-Y-axis reason.
This comparison checks only one horizontal position, is not blinded or
independently adjudicated, and does not verify endpoints, calibration, OCR,
markers, or the complete workflow. Since no selection changed, it shows no
observed location regression and cannot demonstrate an improvement. The probe
remains outside production.

The private visual-window manifest SHA-256 is
`817477e33a88b369424c07c64e5e3d873648d93a0b067bab41743acb3d0d99a7`,
and the private score is
`26e4f878b8762a600ab10551bbb6936fc50d984c10719d267b76c7f60db9847d`.
The corrected calibration diagnostic is
`03ad8ccd4f8cca7f63eebb79964c2fbbd2593a3dbb9fd38cd92bef0456867efe`.
The private outputs remain active inputs to the ongoing axis investigation.
