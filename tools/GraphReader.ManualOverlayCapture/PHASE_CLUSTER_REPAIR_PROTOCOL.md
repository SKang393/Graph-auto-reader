<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Phase-divider clustering repair verification

Compare the prior seven-panel assisted phase-validation capture with the same
training images, anchor binding, and unapproved model composition after the
local divider-clustering repair. Report phase validation condition counts,
completion and failure counts, accepted-marker identity, returned point counts,
and whether the new wide-cluster warning reaches the workflow result. Preserve
failed panels in all denominators. Compare returned point geometry on panels
that complete in both runs. Do not interpret phase regions as correct without
independent phase-boundary labels.

The repair groups local segments against the current weighted cluster center,
matching the service's requirement that returned centers be separated. When
a grouped set spans more than the configured tolerance, it emits an explicit
review warning because nearby strokes may be distinct boundaries. Manual and
cross-panel conflicts remain subject to the existing service checks.

The seven anchor bindings use simulated confirmation and imperfect training
data. This comparison cannot approve a model, prove automatic calibration or
phase accuracy, or validate a product release. No optimizer, development or
sealed payload read is authorized.
