<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Real-dev calibration confirmation, 0.4.47

## Scope and result

The fixed candidate was the immutable 0.4.47 package from source
`76204ba4231d3c05236c03fe7adae6b29fd51677`, selected before confirmation.
The run used the same frozen 120-item real-dev assignment, inventory and source
identity digest as the 0.4.46 confirmation. All 120 payloads were hashed and
embedded images bound. It read no sealed payloads, took no optimizer steps, did
no model/threshold selection or candidate comparison, emitted aggregate output
only, wrote no images, and did not use manual truth for inference. No
calibration was applied.

The run started at `2026-11-14T17:06:59.5299527Z`, finished at
`2026-11-14T17:10:18.0884324Z`, exited 0, and wrote empty stderr. Geometry and
OCR returned for 37/120 items, with 300 numeric readings. **Complete measured
proposals: 0/120. Complete estimated review drafts: 0/120.** Review availability
was evaluated for all 37 OCR responses: 3 X-only and 2 Y-only drafts (5/120
overall); 32/37 had neither axis. This availability is not an accuracy or click
savings claim. There are no residual samples; residual statistics are null,
not zero-error measurements.

| Timing | Result |
| --- | ---: |
| Process wall | 198.5531469 s |
| Pipeline total | 197,241.8522 ms |
| Case total median / p95 / maximum | 707.7296 / 4,783.21 / 12,402.4966 ms |
| Reused OCR, 36 calls: median / p95 / maximum | 2,506.4185 / 7,047.7994 / 11,532.5698 ms |
| Cold OCR, 1 call | 4,088.5218 ms |

Case totals include early geometry failures; they are not full OCR latencies for
all 120 inputs. Reason counts overlap. Measured reasons include
`AXIS_AMBIGUOUS` 80, `AXIS_NOT_FOUND` 1, and `AXIS_NO_INTERSECTION` 2, alongside
`TICK_AMBIGUOUS_ASSOCIATION` 2, `TICK_INCONSISTENT_SPACING` 2,
`TICK_INSUFFICIENT_PAIRS` 32, `TICK_INVALID_NUMERIC_LABEL` 12,
`TICK_INVALID_RANGE` 2, `TICK_MISSING_ENDPOINT` 18, and
`TICK_UNPAIRED_LABEL` 23. The 80 ambiguity results reflect fallback handling;
they do not prove 80 multipanel images or broad axis-recovery capability.

Estimated-draft incomplete reasons: `TICK_AMBIGUOUS_ASSOCIATION` 4,
`TICK_INCONSISTENT_SPACING` 8, `TICK_INSUFFICIENT_PAIRS` 29,
`TICK_INVALID_NUMERIC_LABEL` 12, `TICK_INVALID_RANGE` 3,
`TICK_MISSING_ENDPOINT` 18, `TICK_NONMONOTONIC` 6,
`TICK_UNPAIRED_LABEL` 7, and `TICK_ZERO_NOT_OBSERVED` 2. These counts are
nonexclusive. Neither proposal path produced a complete/scored result.

Peak sampled parent memory was 109,293,568 bytes; peak parent working set was
165,437,440 bytes; peak sampled combined memory was 531,005,440 bytes. These
samples are not a hard memory ceiling. Parent preprocessing and child workers
were independently capped at 0.4992 logical core each, not jointly. Known
development-set exposure and imperfect manual truth remain; tick transcription
truth is unavailable. This run establishes no accuracy improvement and does
not justify threshold changes.

## Identity and reproduction

| Component | SHA-256 |
| --- | --- |
| Portable package | `8cdb6e9e523f0c157cc5114d7ed699bbbf765c7a26df068800a3b80803f4a319` |
| App executable | `a2824381f3b9d19caad083b19f031e45dd50f8f02bdda41a01eb38433177d12f` |
| App DLL | `5fa629e7a01b40ec58fb4ab136dd21b0f5b3ff5cc61e286b1c45a7642317d039` |
| Rust worker | `44258191be06bae2fa9536ebd84cca69631d7732fd3237bc6b001d71440a673d` |
| OCR reader | `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` |
| OCR manifest | `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a` |
| Probe DLL | `f1fbd49041d2ea2d1496d1348a0c7c43f435057485dce1d97eed6ed5a2913b79` |
| Diagnostic adapter | `e63db212d0ec0af492da7b4c9fe83889e6c93fc4e673f557413324b26d180c18` |
| Aggregate report | `2049a6c710279f5d11b537c0461608ccd6dc4d6aed72d0b3cb8fe12a7557f21b` |

The complete 21-file runtime identity list is pinned in the
[diagnostic adapter](../tools/GraphReader.CalibrationOcrProbe/RealDevCalibrationCheck.cs)
at this outcome commit. Assignment SHA-256 is
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`, selected
inventory SHA-256 is
`03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`, and
source identity digest is
`bbe77dd52fc4a08dd2e2a039de234677bbf0e9267d6b812864c8e87e16e5931f`.

```powershell
$probe = 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\hardware-final-build\GraphReader.CalibrationOcrProbe.exe'
$build = 'E:\Graph-auto-reader\artifacts\dev-portable\builds\0.4.47-20261114T093850726Z-76204ba4'
& $probe --real-dev-calibration-check 'E:\Graph-auto-reader\data\manual data' (Join-Path $build 'GraphReader.App.exe') (Join-Path $build 'review-assistance\ocr') (Join-Path $build 'native\graphreader-engine.exe') --explicit-opt-in
```

The 6,650-byte aggregate and metadata in
`artifacts/real-dev-calibration-447-check` contain no per-case output or images.
Retire this scratch after this record is pushed and active consumers finish.
Preserve the original corpus and current package. This diagnostic is not an
independent accuracy evaluation or production approval.
