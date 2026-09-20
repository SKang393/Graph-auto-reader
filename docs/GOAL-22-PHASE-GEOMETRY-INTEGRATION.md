<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Phase geometry integration

Measured phase lines were held as possible grid lines, while legend-frame
fragments could become full-height dividers. The runtime now uses original
legend pixels and a consistent heading row to interpret existing line evidence.
It does not create lines, change recognized text, or regroup points.

All 352 application tests pass. The Windows x64 tool builds with zero warnings
or errors; its fictitious private-runner check passes. Actual inference on the
unchanged 23 synthetic sources takes 127.15 seconds. Nine measured boundaries
gain heading support and three unsupported legend-fragment dividers are removed.

Using the [corrected phase reference](GOAL-22-PHASE-NAMING-REFERENCE.md) on both
versions, correct relational rows improve from 0/724 to 46/724. All 46 comparable
rows have correct phase labels. Unique point/value matches remain 49/706.
Every previously exported point, numeric value, series membership, inclusion
relationship and printed/estimated x provenance is preserved. Six sources
complete and 17 still fail. All artifact integrity checks pass.

The earlier attempt applied phase-resolved lines to grouping and fragmented
series, losing two exported points. That attempt is retained as rejected
evidence. The final repair applies context only after grouping and legend
reasoning. Earlier failed checks are also retained. A fixture exposed a separate
problem: provisional all-baseline series roles can survive later phase
interpretation and yield an empty export. Its resolution is still required;
the phase-only fixture verifies both point assignments in Review.

[Evidence hashes and full metrics](GOAL-22-PHASE-GEOMETRY-INTEGRATION.json)
bind the tested source snapshots, runtime and unchanged model payloads.
No training, new model revision, private/sealed read, activation, package or
release occurred. Build 433 remains 0.4.33. Next: associate numeric labels with
measured tick positions, then reconcile series roles with final phases and
reject empty exports. Goal 22 remains incomplete.
