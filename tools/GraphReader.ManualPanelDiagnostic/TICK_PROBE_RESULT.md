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
