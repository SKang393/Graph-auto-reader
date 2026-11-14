<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Real-dev calibration confirmation, 0.4.49

The fixed delivered candidate was source `2aea386bf24300416aa773a4cbf874717cf12752`, package SHA-256 `422eb96e35a7c4d73ae87c13a1a5079450f4d6d42f5ccd161a26c8b2b514d1c6`. The same frozen real-dev assignment contained 120 sources; all 120 payloads were hashed and their embedded images bound. Source identities were first observed and locked, not preregistered content. The set has known development exposure, imperfect manual truth, and no tick-transcription truth.

The shipped Rust-first service returned geometry and OCR for 38/120 sources and 319 numeric readings. It reported 117 Rust-provider results and three eligible native fallback attempts; none of the three returned geometry. **Complete measured proposals: 0/120. Complete estimated review drafts: 0/120.** All 38 OCR responses had incomplete pairings, and no complete proposal was scored. Residual distributions therefore have zero samples and null statistics, not zero error.

The axis funnel counts sources independently at each stage, not cumulative successes or accuracy. Of 38 OCR responses, at least three distinct review-associated X pairs appeared in 16, and Y pairs in 29. Valid review drafts were X-only for four, Y-only for two, both for zero, and neither for 32. Pair association precedes fit validation and can include estimated positions. These counts do not establish correctness or click savings.

Measured-path reason counts, which can overlap: `AXIS_AMBIGUOUS` 79, `AXIS_NATIVE_UNAVAILABLE` 3, `TICK_AMBIGUOUS_ASSOCIATION` 2, `TICK_DUPLICATE_ASSOCIATION` 1, `TICK_INCONSISTENT_SPACING` 2, `TICK_INSUFFICIENT_PAIRS` 32, `TICK_INVALID_NUMERIC_LABEL` 12, `TICK_INVALID_RANGE` 2, and `TICK_UNPAIRED_LABEL` 24. Estimated-review incomplete reasons across all 38 evaluated OCR responses were ambiguous association 3, duplicate association 1, inconsistent spacing 7, insufficient pairs 29, invalid numeric label 12, invalid range 3, nonmonotonic 5, unpaired label 9, and zero not observed 2. These counts also overlap.

The run exited 0 with empty stderr, from `2026-11-14T20:39:32.0747885Z` to `2026-11-14T20:42:31.9531091Z`: wrapper wall 179.868 s and pipeline 179.106 s. Case median/p95/maximum were 298.9/5,398.6/10,778.7 ms; the 37 reused OCR calls had median/p95/maximum 3,544.2/7,295.3/10,579.8 ms. Geometry timing includes decode, Rust and any fallback; decode was not separately measured. Peak sampled parent bytes were 81,645,568, parent working set 136,671,232, and combined sampled bytes 560,549,888. Samples are not a hard memory ceiling. Parent preprocessing used the verified 0.4992-logical-core aggregate cap and below-normal priority; child workers were independently capped, not jointly.

No sealed payloads were read, no optimizer steps or candidate/model selection occurred, no images or per-case results were written, and manual truth was not supplied to inference. This is a fixed development diagnostic, not independent accuracy evidence, a comparative gain claim, or production approval. No calibration was applied.

| Identity | SHA-256 |
| --- | --- |
| App executable | `f95dc00c01166c67656b8bfd165c7b6d92ca15bf489b874e85cdd36824b2f2fa` |
| App DLL | `7fe1cee8c1b1f37a0e457d7b90af9e204ac5c3bdaf39b2e33272516cb4ee00c5` |
| Rust worker | `e98e3ff2d8edd77141b85c096fd63d6cd670db82c9f745e6d8bac2c6daf1f4cc` |
| OCR reader | `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` |
| OCR manifest | `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a` |
| Reviewed OpenCV native runtime | `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31` |
| Diagnostic adapter | `b655c09f936a191c8707225667911c13ae22e8aae79519a95af22152b7376867` |
| Aggregate report | `0a53948da30d116bedb1121d61ce7c15480aa93409ace942be3f96524dd840f7` |
| Assignment | `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a` |
| Selected inventory | `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d` |
| Complete source identity digest | `bbe77dd52fc4a08dd2e2a039de234677bbf0e9267d6b812864c8e87e16e5931f` |

The complete runtime library identity list is pinned in [RealDevCalibrationCheck.cs](../tools/GraphReader.CalibrationOcrProbe/RealDevCalibrationCheck.cs) at this outcome. The probe-only build passed in 8.05 seconds with zero warnings/errors. Its four owned axis-funnel controls and two affine checks passed before private reads. The 21 delivered runtime identities were verified at 24 active x64 destinations after building the probe. Unused ARM64 destinations were not overwritten with x64 libraries.

From the active worktree, after loading those exact delivered libraries:

```powershell
$probe = Join-Path $PWD 'artifacts/hardware-final-build/GraphReader.CalibrationOcrProbe.exe'
$build = 'E:/Graph-auto-reader/artifacts/dev-portable/builds/0.4.49-20261114T202631548Z-2aea386b'
& $probe --real-dev-calibration-check 'E:/Graph-auto-reader/data/manual data' "$build/GraphReader.App.exe" "$build/review-assistance/ocr" "$build/native/graphreader-engine.exe" --explicit-opt-in
```

The aggregate and run metadata under `artifacts/hosted-preview-449` are disposable after this record is pushed and their active consumers finish; preserve the original corpus and current package.
