<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Seven-source axis probe protocol

Select all seven source groups in the existing 15-group training-build
partition whose exact image hashes are absent from the eight images previously
used for the axis-label experiment. The existing plan groups by study and exact
embedded-image hash; it also excludes the five separate internal holdout groups.
The selection reads the plan and image hashes, not text transcriptions or axis
coordinates. Its private input manifest SHA-256 is
`bd158b9bebc87e7eaf831a0e224d78af3e3f1e9350c9f5a30587728055eb457d`.

Before viewing these seven images for this probe or reading their calibration
anchors, run the existing panelizer and axis detector, then the fixed
`tick_rank_probe.py` selection rule. Freeze and hash both sets of predictions.
After that, compare the selected and baseline vertical axes with reviewed
visible axes or the corresponding Engauge calibration anchors. Report panels
without usable axes and ambiguous truth separately; do not substitute oracle
coordinates into inference or regard unreviewed content as a negative.

These seven sources are outside the 13 images used to formulate the current
probe, but they belong to the existing training corpus. Earlier exposure and
overlap with historical or sealed sources are not established. Annotation is
not independently adjudicated. This is a limited development diagnostic, not a
production acceptance gate. No optimizer training or sealed payload read is
authorized by this protocol.
