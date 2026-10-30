<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Tick-lane predicate diagnostics

The optional synthetic tick-lane observation now records each failed seed
bounds check separately. A component can fail several checks, so these counts
overlap. A null bounds breakdown means classification did not run, not zero
rejections. The existing synthetic observation schema permits additive fields.
Normal candidate selection, OCR, calibration, and export gates are unchanged.

The full OCR suite passed 806 tests with 13 skips; subsequent focused OCR and
production-adapter tests passed after adding pipeline and JSON-shape coverage.
No thresholds, model weights, or release approvals changed.

## Frozen synthetic replay

A fresh build from commit `bde0ee0b88a784a5df4e1439fe2a3bb94d3cf095`,
using the same V13 models, source-scale composition, 21 synthetic sources and
acceptance policy, completed 1 source and failed 20 at calibration in
276,230.8 ms. The command exited 1 because the workflow did not pass. Its
31 panel sidecars matched all recorded SHA-256 hashes. Across 14 selections,
all 280 components were rejected by bounds checks. The overlapping counts are:

| Bounds check | Rejections |
| --- | ---: |
| Not below the plot | 271 |
| Too wide | 136 |
| Too short | 25 |
| Too narrow | 25 |
| Left of lane | 16 |
| Right of lane | 5 |
| Too far below the plot | 0 |
| Invalid bounds | 0 |

These are predicate failures, not verified text labels, and counts overlap.
They do not justify widening the lower gutter or inferring an absent session
number. No private or sealed data or optimizer steps were used.

The candidate binding SHA-256 was
`c0710044b3a723ebea5b2235b2a22ab295bd920266680898d26cb45a86f2e051`, input
manifest `6ca5bcf48156ad2c8dbe5bf6438e91d59e94ed300c8fedd2181e8a44cf63436a`,
report `86619f02ed0ba84c90512644f553d1e68504c9af72bc0cd0bfe0cb2e776bd1e3`,
and sidecar index `22c85452b38abba6e7bc0f91b02112a354323376124b03f65a9c5f6a7e7ac9df`.
The runner used CPU inference only, with no training or model promotion.
The command used the freshly built `GraphReader.RealAcceptance.Ocr.dll` with
`--run-frozen-candidate-synthetic`, the binding and input hashes above, and a
new output directory under
`artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-tools/tick-predicate-replay-20261030-v1`.
The complete command and provenance are in that temporary preparation receipt;
regeneration requires the same inputs and bound source build, not the old V13
runner. Completed diagnostic output can be retired after its active consumer
finishes, subject to the current deletion controls.
