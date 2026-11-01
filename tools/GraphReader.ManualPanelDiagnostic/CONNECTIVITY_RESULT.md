<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Axis-connected proposal diagnostic

This train-only probe measured whether each of 62 frozen Y-label proposals
shares a local original-pixel connected component with the candidate vertical
or horizontal axis. Axis geometry came from the previously exposed experimental
tick-rank probe. The capture read no label transcriptions. It tried two dark
pixel cutoffs, 128 and 200, and bounded the component analysis to a local crop.

After capture, 46 proposals matched assistant-reviewed text labels at IoU
0.5, six labels remained missed, and 16 proposals were unmatched. An assistant
visually identified seven unmatched numeric-looking predictions as graph marks:
two marker portions, one boxed symbol, and four axis strokes or ticks. The
remaining nine unmatched proposals were not classified.

| Measured connection | Reviewed text proposals | Seven reviewed graph marks |
| --- | ---: | ---: |
| Vertical axis, cutoff 128 | 0/46 | 7/7 |
| Horizontal axis, cutoff 128 | 2/46 | 4/7 |
| Vertical axis, cutoff 200 | 0/46 | 7/7 |
| Horizontal axis, cutoff 200 | 2/46 | 4/7 |

These counts use five already inspected training images and incomplete,
assistant-reviewed labels. They do not prove that connected text is absent in
other graphs, or that a production filter would be safe. A candidate touching
one axis can also share a component with the other through the origin; the
probe measures component connectivity, not the exact contact path. No runtime
filter, model approval, or release decision was changed.

| Private evidence | SHA-256 |
| --- | --- |
| Frozen proposal input | `469b5ee37f1d2ad80b4dcb638a3d2183c4949e003de3322d9a186e140228779a` |
| Truth-blind connectivity features | `7b4993a21fde2e6322774f85439ef6ee25634dc170186232e76291dd76ebfd96` |
| Assistant visual classification | `810a87a771b2798c2d720f2a3e392b3cdc8ac587f9342ef08c1719eb0c23c3a8` |
| Aggregate score | `b9c8bd237402e99f4fda1c42824ef6c74e2d5bffeafc2418f665c6538ed3d9a8` |

Three synthetic tests and pyflakes passed. The first test fixture incorrectly
expected an axis-connected component near the origin to touch only the
horizontal axis; moving the fixture away from the origin tested that distinction.
The diagnostic used CPU image processing, no optimizer steps, and no sealed
payloads. Its private features and visual review remain project-local for the
active structure-versus-text investigation.

## Additional reviewed training boxes

The same component probe was applied to 76 earlier assistant-selected label
boxes from eight other training images, using their previously captured
candidate axis geometry. All 76 were measurable. None shared a component with
the selected vertical axis at either cutoff; four shared one with the horizontal
axis. Together with the first 46 matched text proposals, this yields zero
vertical connections among 122 reviewed text boxes in these exposed samples.

This does not establish a safe exclusion rule. A synthetic fixture deliberately
places label ink against an axis, and the same connectivity check flags it.
Real labels can touch axes, and the current component test does not distinguish
that case from a connected tick or marker. The study-partitioned development
set and full automatic workflow have not yet validated this signal.

The truth-blind feature report SHA-256 is
`d890336d4c5fd5004a72cd6b208e17bc550c2407902c1a2925ceb0ddc88ff5e2`;
the aggregate score is
`1c1f84a2c951f49e17ab5063c72c94ddc22d4dbb292b1f159688607a5adc47bb`.
The image and label-box inputs remain private project-local artifacts.

## Review-only workflow safeguard

The application now requests review when an unreviewed numeric Y-axis reading
shares a local component of original-pixel ink with the selected vertical axis.
It preserves the OCR region and text. A user-confirmed reading can proceed
through the existing calibration path. This is a positive review signal, not a
filter: legitimate text can touch an axis, and the connection is not proof that
a reading is wrong. A uniform crop, out-of-bounds box, or local crop over one
million pixels supplies no positive connection signal; that is not proof that
the reading is safe.

The actual C# implementation and production raster decoder matched the earlier
Python connectivity measurements on all 196 frozen proposals: 62 from exposed
training images and 134 from development images. This checks implementation
parity, not recognition accuracy. The development review is described in
[DEV_CONNECTIVITY_RESULT.md](DEV_CONNECTIVITY_RESULT.md), including its small
number of structural examples and non-independent classifications.

The frozen candidate workflow then processed all 20 original training images.
One of 21 panels gained the new review warning; the other 20 retained the same
status and error. All 21 panels reached the marker stage and returned structured
recalibration-required results. Nine had invalid session origins, ten needed
review, and two had insufficient evidence. All 20 images failed to complete,
and no final points were produced. The center and accepted-marker stages matched
250 and 207 of 332 imperfect recorded points, unchanged from the preceding run;
the incomplete point annotations do not establish precision.

| Private evidence | SHA-256 |
| --- | --- |
| Parity input | `7e5e8dc0bf40521bdbc66fa91a370a7613eb8da65d9cd128bd9fca1af2151dd5` |
| Runtime description before and after | `f2349f37ee9e345a277038d7bc933363413e71655cae88dd150743fd4a0d677b` |
| C# versus Python parity report | `26678a2ee6c475189baa7cc02efb05e5f4257f0760d2dacc6e9260fb35b8e8b2` |
| Full workflow report | `f7910323ae4536c8d84998fd71c5ffb13c12ea96182ceea725ba92c67b343efb` |
| Full workflow score | `c2e1d149262bb88ee5c0b46767ef524eda5e4c2ea44b9f0b3ba3372f03674a4e` |

All 763 application tests passed after correcting an older synthetic fixture
that drew its simulated label directly against the axis tick. The integration
suite passed 170 tests with seven skipped; its first attempt lacked the
process-local Git safe-directory setting. Nine Python diagnostic tests and
pyflakes passed, and the diagnostic build had no warnings. These checks used
CPU inference or image processing, no optimizer steps, and no sealed payloads.
They do not approve a model or establish product acceptance.
