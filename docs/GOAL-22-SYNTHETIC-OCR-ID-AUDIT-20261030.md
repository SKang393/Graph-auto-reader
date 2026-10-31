<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Historical synthetic OCR replay and ID audit

The pinned V46 synthetic candidate was replayed with the historical frozen v4
runtime from commit `d174c3d5fd367b0dc090637df3974c4c06a6993b`, not the
current main runtime. The replay retained raw OCR and stage evidence for 31
panels and completed 1 of 21 workflows; the other 20 stopped during automatic
calibration. These results do not establish current-build accuracy or release
readiness.

The offline scorer uses the existing maximum-cardinality matching rule and
an intersection-over-union cutoff of 0.5. Among 494 predictions against 1,135
synthetic truth regions, it matched 300 and left 194 unmatched. Their surviving
region-ID classifications were 174 retained raw-detector IDs, 12 completed
header-glyph IDs, and 8 unrecognized IDs. These classifications do not prove
the earliest runtime origin of an assembled or modified region.

No private or sealed inputs were used in the replay or offline scorer. The
result cannot be compared directly with the earlier 446-of-491 aggregate.

- Scorer: `tools/Audit-SyntheticOcrFalsePositives.py`
- Aggregate: `artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/v46-probe-current-main-replay-20261030-v9-attribution/false-positive-attribution.json`
- Aggregate SHA-256: `fe90d35629f0fe8598740ff5ccb374bf90a87fa4713b6883463bf947c2429621`
