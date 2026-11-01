<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Residual axis ranker: manual-training anchor diagnostic

Use the already captured 20 unmodified, user-authorized training images and
their native axis candidate snapshots. Freeze the synthetic-trained residual
ranker's selection for every captured panel before opening the existing local
anchor bindings. Compare the native and candidate horizontal-axis lines on
the seven panels whose three stored digitization anchors produced eligible
session-first calibration bindings. One of the eight anchor examples was
previously excluded because its derived skew exceeded the manual contract.

The metric is the mean perpendicular distance of the derived session-1/zero
and X-maximum/zero anchors to each infinite horizontal-axis line, divided by
panel height. It checks alignment with an imperfect digitization reference,
not axis endpoints, the vertical axis, OCR, graph values, or automatic
calibration. The anchors can be interpolated or extrapolated and have not
been independently verified against the printed graphs. Report all seven
cases in aggregate, including improved, equal, and worsened counts. Do not
choose a threshold or change production behavior from this comparison.

CPU and RX 6600 XT forward selections must agree before scoring. This is
inference only, with no optimizer. No real-development or sealed payload is
authorized. Image identities, coordinates, and per-case observations stay in
ignored project-local outputs and are not committed. This exposed training
subset cannot establish generalization or release acceptance.
