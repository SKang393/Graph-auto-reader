<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Historical synthetic OCR replay and ID audit

The pinned V46 synthetic candidate was replayed with the historical frozen v4
runtime from commit `d174c3d5fd367b0dc090637df3974c4c06a6993b`, not the
current main runtime. The replay retained raw OCR and stage evidence for 31
panels and completed 1 of 21 workflows; the other 20 stopped during automatic
calibration. These results do not establish current-build accuracy or release
readiness.

The exploratory scorer uses maximum-cardinality matching and an IoU cutoff of
0.5, but it counts whole-source annotations for each scored panel. Multi-panel
sources can repeat truth annotations, and panel coordinates were compared
with source coordinates without the recorded transform. Its counts do not establish OCR accuracy or identify
validated false positives. Surviving region-ID classifications also do not
prove the earliest runtime origin of an assembled or modified region.

No private or sealed inputs were used in the replay or offline scorer. The
result cannot be compared directly with the earlier 446-of-491 aggregate.

- Scorer: `tools/Audit-SyntheticOcrFalsePositives.py`
- Aggregate: `artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/v46-probe-current-main-replay-20261030-v9-attribution/false-positive-attribution.json`
- Aggregate SHA-256: `fe90d35629f0fe8598740ff5ccb374bf90a87fa4713b6883463bf947c2429621`
