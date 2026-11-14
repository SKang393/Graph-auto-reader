<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Real-dev calibration confirmation, 0.4.46

## Scope and result

The fixed candidate was the immutable 0.4.46 package from clean source
`42b0d9e20fa1029000e6c902df7898bfc0300335`, selected before confirmation.
This used the same frozen 120-item real-dev assignment, inventory and source
identity digest as the 0.4.45 confirmation. All 120 payloads were hashed and
their embedded images bound. The run read no sealed payloads, took no optimizer
steps, did no model/threshold selection or candidate comparison, emitted
aggregate output only, wrote no images, and did not use manual truth for
inference. No calibration was applied.

The run from `2026-11-14T08:48:07.080337Z` to
`2026-11-14T08:50:51.759758Z` exited 0 with empty stderr. Geometry and OCR
returned for 36/120 items, with 281 numeric readings. **Complete measured
proposals: 0/120. Complete estimated review drafts: 0/120.** The
review-availability diagnostic evaluated all 36 OCR responses with none
unevaluated: 3 X-only and 2 Y-only drafts, 5/120 overall (5/36 evaluated), and
31/36 with neither axis. This availability does not claim accuracy or click
savings. There are no residual samples: residual statistics are null, not
zero-error measurements.

| Timing | Result |
| --- | ---: |
| Process wall | 164.6690171 s |
| Pipeline total | 163,942.1904 ms |
| Case total median / p95 / maximum | 656.18705 / 4,372.1668 / 6,547.7433 ms |
| Reused OCR, 35 calls: median / p95 / maximum | 2,293.9197 / 4,434.4813 / 5,898.4215 ms |
| Cold OCR, 1 call | 3,315.6812 ms |

Case totals include early geometry failures; they are not full OCR latencies for
all 120 inputs. Reason counts overlap. Measured proposal reasons: `AXIS_AMBIGUOUS`
25, `AXIS_NOT_FOUND` 57, `AXIS_NO_INTERSECTION` 2,
`TICK_AMBIGUOUS_ASSOCIATION` 2, `TICK_INCONSISTENT_SPACING` 1,
`TICK_INSUFFICIENT_PAIRS` 32, `TICK_INVALID_NUMERIC_LABEL` 12,
`TICK_INVALID_RANGE` 2, `TICK_MISSING_ENDPOINT` 17, and
`TICK_UNPAIRED_LABEL` 23.

Estimated-draft incomplete reasons: `TICK_AMBIGUOUS_ASSOCIATION` 4,
`TICK_INCONSISTENT_SPACING` 7, `TICK_INSUFFICIENT_PAIRS` 29,
`TICK_INVALID_NUMERIC_LABEL` 12, `TICK_INVALID_RANGE` 3,
`TICK_MISSING_ENDPOINT` 17, `TICK_NONMONOTONIC` 6,
`TICK_UNPAIRED_LABEL` 7, and `TICK_ZERO_NOT_OBSERVED` 2. These counts are
nonexclusive and refer to incomplete review drafts, not only drafts with
estimated positions. Neither proposal path produced a complete/scored result.

Peak sampled parent memory was 89,350,144 bytes; peak parent working set was
150,953,984 bytes; peak sampled combined memory was 522,211,328 bytes. These
samples are not a hard memory ceiling. Parent preprocessing and child workers
were independently capped at 0.4992 logical core each, not jointly.

Known development-set exposure and imperfect manual truth remain; no tick
transcription truth is available. This confirmation establishes no accuracy
improvement and does not justify threshold changes.

## Identity and reproducibility

| Component | SHA-256 |
| --- | --- |
| Portable package | `af6b4a1ebf4ea0f93731ff5c34f86e5c7385b5a8d8fbd94fd7e85af60b2e3e9d` |
| App executable | `3085c125bdaf9b6271363e3593341dc8d7cd2b9761e1be22282f04389c30c5f7` |
| App DLL | `634d5121dc73d1c18282581b2623def039d86089712d124f9fb260a85c194d03` |
| Rust worker | `71455f26d241aec90bf2b88b4267e272028a286dd2b8e7fbc5f86ab8d27fdfa0` |
| OCR reader | `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` |
| OCR manifest | `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a` |
| Probe | `72129810638483b2231d2659af6e81443f2728ea985b36c39782055381bf410a` |
| Diagnostic adapter | `0c5844bf18aedfc71c476869129f8881999e4149b46fc7940782506e01585d40` |
| Aggregate report | `b05d7b314731345d58ae57bfaa16904474871dd8e6e199ffee4b85111562dae9` |

The complete 21-file runtime identity list is pinned in the
[diagnostic adapter](../tools/GraphReader.CalibrationOcrProbe/RealDevCalibrationCheck.cs)
at this outcome commit.
Assignment SHA-256 is
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`, selected
inventory SHA-256 is
`03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`, and
source identity digest is
`bbe77dd52fc4a08dd2e2a039de234677bbf0e9267d6b812864c8e87e16e5931f`.

```powershell
$probe = 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\hardware-final-build\GraphReader.CalibrationOcrProbe.exe'
$build = 'E:\Graph-auto-reader\artifacts\dev-portable\builds\0.4.46-20261114T083718338Z-42b0d9e2'
& $probe --real-dev-calibration-check 'E:\Graph-auto-reader\data\manual data' (Join-Path $build 'GraphReader.App.exe') (Join-Path $build 'review-assistance\ocr') (Join-Path $build 'native\graphreader-engine.exe') --explicit-opt-in
```

Two adapter/CPU-budget self-tests passed with zero private reads. The 6,650-byte
aggregate and metadata under `artifacts/real-dev-calibration-446-check` contain
no per-case output or images. Retire that scratch after this record is pushed
and active readers finish. Preserve the real-dev source data and the retained
0.4.46 package. No installer, public release, production approval, or
independent accuracy claim is implied.
