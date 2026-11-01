<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Wide horizontal model orientation candidate

Evaluate an opt-in, unapproved candidate on the same 20 previously exposed
original training images. When the model supplies a zero-degree polygon angle
and its bounding box is at least 1.2 times as wide as tall, retain the model
angle instead of inheriting a component angle. Preserve the existing behavior
for other regions. The threshold was selected after examining exposed cases;
this is exploratory and must not be represented as an independent evaluation.

Capture the complete workflow with frozen model and input identities, no truth
access during inference, no optimizer steps, and no sealed payload reads. After
capture, compare panel outcomes and the previously reviewed 115 Y regions with
the prior workflow. Count missing or reclassified regions, numeric corrections,
regressions, and warning changes. Use the existing incomplete point annotations
only as a descriptive marker comparison. The known nearly square rotated case
is outside this rule. Do not approve a model or relax a release gate on these
results. Record any regression and keep all per-region data project-local.
