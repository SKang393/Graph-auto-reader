<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Assisted phase-divider validation diagnostic

Repeat the fixed seven-panel assisted training candidate with a diagnostic
wrapper around the unchanged phase adapter. Before forwarding the same request,
the wrapper reruns the same deterministic divider detector and counts each
condition checked by the phase service: divider identity, plot bounds,
separation, style and source, confidence, segment IDs, and source-panel IDs.
The wrapped service still makes its original decision. Record aggregate
condition counts and compare panel status and marker observations with the
prior assisted run. Keep individual panel data private and project-local.

The anchors use simulated confirmation and are imperfect recorded training
data. This diagnostic does not relax a phase constraint, verify a phase label,
train a model, read development or sealed payloads, or authorize a release.
