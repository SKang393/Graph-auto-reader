<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Training-only assisted calibration feasibility

Eight previously exposed training images already have extracted Engauge axis
anchors and are also present in the 20-image candidate workflow capture. Check
whether their affine anchor mappings can be represented by the application's
existing axis-aligned three-anchor manual calibration contract inside exactly
one captured panel. Count unsupported anchors, reversed axes, excessive skew,
and missing or ambiguous panel containment rather than inventing values.

This is an input-feasibility diagnostic, not an automatic-recognition score.
If a later assisted workflow uses these mappings, they are imperfect recorded
digitization values, not actual new user confirmations. It must be isolated to
an explicitly unapproved training-only candidate. No model training,
development or sealed payload read, approval, or release is authorized here.
