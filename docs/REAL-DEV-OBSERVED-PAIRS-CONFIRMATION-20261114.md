<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Fixed real-dev confirmation after observed-ink and paired-anchor repairs

This run confirms the deterministic repairs committed in `f3bbe7ad` and
`acdc645e2f110ad34da942f3de71638f3bfdb41e`, after their owned tests and actual
OCR/export workflows passed. It uses frozen **source-build** binaries with
version 0.4.48, not the unchanged delivered 0.4.48 package. The existing OCR
model, fixed 120-project assignment, input identities, CPU limits and
aggregate-only reporting remain unchanged. No model, threshold or candidate
was selected using these results. No private image, truth row, per-case
prediction or persistent cache was written; no sealed payload was read.

All 120 payloads were hashed and bound to their embedded images. Geometry and
OCR returned for 37/120, yielding 300 numeric readings. **Complete measured
proposals: 0/120; complete estimated review drafts: 0/120.** Review availability
was four X-only and two Y-only drafts, with neither axis for 31/37 OCR results.
The prior fixed confirmation had three X-only and two Y-only drafts. This is
one additional partial draft, not an accuracy or click-savings measurement.
The owned ink-continuity repair did not increase this set's geometry count.
Automatic calibration readiness remains failed.

The run started `2026-11-14T19:09:21.7586249Z`, ended
`2026-11-14T19:11:41.0772118Z`, and exited 0 with empty stderr.

| Timing | Result |
| --- | ---: |
| Process wall | 139.3059413 s |
| Pipeline total | 138,308.6882 ms |
| Case median / p95 / maximum | 201.5157 / 4,298.6768 / 7,290.783 ms |
| Geometry median / p95 | 50.4135 / 268.6013 ms |
| Cold OCR, one call | 3,891.9444 ms |
| Reused OCR, 36 calls: median / p95 / maximum | 2,865.03475 / 5,787.7279 / 7,134.038 ms |

Case timing includes early geometry failures. These are single-run timings,
not a controlled speed comparison. Parent preprocessing and each child are
independently limited to 0.4992 logical core with BelowNormal priority; they
are not a joint CPU budget. Sampled combined memory peaked at 544,063,488
bytes, not a hard memory ceiling.

Measured-proposal reasons were AXIS_AMBIGUOUS 80, AXIS_NOT_FOUND 1,
AXIS_NO_INTERSECTION 2, TICK_AMBIGUOUS_ASSOCIATION 2,
TICK_INCONSISTENT_SPACING 2, TICK_INSUFFICIENT_PAIRS 32,
TICK_INVALID_NUMERIC_LABEL 12, TICK_INVALID_RANGE 2 and TICK_UNPAIRED_LABEL 23.
Review-draft incomplete reasons were ambiguous association 4, inconsistent
spacing 8, insufficient pairs 29, invalid numeric label 12, invalid range 3,
nonmonotonic sequence 6, unpaired label 7 and unobserved zero 2. Reason counts
overlap. There are zero residual samples; residual statistics are null.
Known development-set exposure, imperfect manual truth and unavailable tick
transcription truth remain limitations.

## Identities and execution

| Component | SHA-256 |
| --- | --- |
| Source App EXE | `45551d71a4e79d0e67d8a5c15df2e41af9b798d45dda8dedc1f60921c46a84b4` |
| Source App DLL | `939a76b3f0ce08ea76b63006c97dc2bfe073570adc6772c1cecbfd5385f3fffd` |
| Local Rust worker | `46e6dcf885760e4ce61c956ef93802b53e2dd51ddb0118ac0c84691e3dda94a0` |
| Probe DLL | `247a3c6e8ebc646872e62571cdd12cf2e79744eb440a191930900f5e047ac461` |
| Aggregate report, 6,650 bytes | `41009d5bba5ec3babb373fa5d67185299d8c33ec285c6103f283675a87fb8e4f` |

The adapter pins all 21 runtime files before selecting private payloads.
Assignment, selected-inventory and locked-source digest are respectively
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`,
`03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`
and `bbe77dd52fc4a08dd2e2a039de234677bbf0e9267d6b812864c8e87e16e5931f`.
The OCR model/manifest retain their existing 7839f12b/17d44e8e identities.

The probe-only build passed with zero warnings/errors in 7.615 s. Its copy
step restored the NuGet OpenCV DLL; a preflight caught that mismatch before
any private read. The reviewed c96f91b3 runtime was restored at both existing
output locations. App hashes remained frozen. The aggregate/privacy self-test
then passed with zero private reads, including capability failure before
selection, forbidden-output rejection and two affine residual checks.

From the active worktree, with the exact pinned source binaries:

```powershell
$bin = Join-Path $PWD 'artifacts/hardware-final-build'
$assets = 'E:/Graph-auto-reader/artifacts/dev-portable/builds/0.4.48-20261114T183158946Z-b669e1e5/review-assistance/ocr'
& "$bin/GraphReader.CalibrationOcrProbe.exe" --real-dev-calibration-check 'E:/Graph-auto-reader/data/manual data' "$bin/GraphReader.App.exe" $assets "$PWD/artifacts/native-engine/release/graphreader-engine.exe" --explicit-opt-in
```

The 10 MB temporary output allowance was not approached. Retire
`artifacts/real-dev-observed-pairs-check` after this adapter/outcome is pushed
and its active consumer finishes. Preserve private originals, the current
package and runtime dependencies. This result does not authorize production
activation, a new model or a public release.
