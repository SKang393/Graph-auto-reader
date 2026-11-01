<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Training-only assisted workflow diagnostic

Prepare private panel-local anchor coordinates for the seven examples that
passed the preceding feasibility check. The graph-to-screen affine mapping is
derived from the three existing Engauge anchor points; the chosen session-1,
zero, and maximum sample coordinates may involve interpolation or extrapolation.
Retain the eighth skewed case as excluded. Bind each prepared row to the
previously captured source image, panel ID, panel image, and crop transform.

An isolated candidate-only workflow may supply these seven recorded mappings
through its existing manual-calibration interface solely to inspect downstream
stage completion and marker matches. The interface's confirmation flag is a
diagnostic simulation and is not an actual user confirmation. Do not write the
bindings, original images, coordinates, or per-image outcomes to Git.

Compare against the fixed earlier 20-image training capture and retain failures
in the seven-image denominator. These imperfect recorded anchors and incomplete
point labels cannot establish automatic calibration, precision, generalization,
or product acceptance. The experiment authorizes no optimizer, development or
sealed payload read, production activation, gate change, or release.
