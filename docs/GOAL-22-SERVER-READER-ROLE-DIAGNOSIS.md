<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Server reader gains and phase failures, 2026-09-30

The larger reader recovers 20 native tick readings without losing an earlier
correct tick reading. Its 29-reading net gain also includes word and condition
labels, but three earlier phase/condition labels and one participant label are
lost. These are exact primary strings, with no normalization or alternatives.
All 453 labels from 21 development sources remain in the denominator.

| Truth role | Labels | Baseline exact | Server exact | Recovered | Lost |
| --- | ---: | ---: | ---: | ---: | ---: |
| phase_heading | 85 | 72 | 70 | 0 | 2 |
| y_tick | 99 | 68 | 87 | 19 | 0 |
| participant | 31 | 12 | 18 | 7 | 1 |
| axis_title | 52 | 50 | 51 | 1 | 0 |
| legend_text | 37 | 28 | 28 | 0 | 0 |
| annotation | 62 | 54 | 54 | 0 | 0 |
| condition_label | 85 | 63 | 67 | 5 | 1 |
| x_tick | 2 | 1 | 2 | 1 | 0 |

The separate full-workflow audit reproduces the original 11 wrong-phase rows
and all other canonical metrics without rerunning inference. All 11 occur in
one train source that already exported under the baseline. Six first-baseline
rows change from `a1` to `a`, two later-baseline rows become `phase3`, and three
remain `phase5`. Saved OCR contains damaged baseline/withdrawal headings and
a missing later `A`. The unchanged phase reasoner loses evidence of repeated
baselines. It must not repair these strings through graph-specific whitelists.

This diagnosis supports testing numeric-context composition separately. The
existing dual-recognizer pipeline keeps general text apart from numeric roles,
but no composed server/native run has been performed or selected here. Detection
still misses 63 labels under the server reader; composition alone cannot
establish the existing 95% product bars. No model, threshold or runtime changes.

The [aggregate evidence](GOAL-22-SERVER-READER-ROLE-DIAGNOSIS.json) binds
the saved observations, scoring audit, source and acceptance policy. The phase
audit took 10.21 seconds. No private/sealed reads, new inference, training,
dependency, packaging, promotion or release. Existing Apache-2.0 provenance
is unchanged. Goal 22 remains incomplete.
