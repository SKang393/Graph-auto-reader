<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Real-dev calibration confirmation, 0.4.45

## Scope and safeguards

The fixed candidate was the immutable 0.4.45 package from source
`4e721496c434c209cbf64a4cbc953940dd735e1e`, selected before confirmation.
The run covered the same frozen 120-item real-dev split and source identity
digest as the prior confirmation. It read no sealed payloads, took no optimizer
steps, performed no model or threshold selection, emitted aggregate output
only, emitted no per-case output, wrote no images, and used no persistent cache
or manual truth for inference. It performed no candidate comparison. No
calibration was applied. Known dev exposure and imperfect manual truth remain;
no tick transcription truth is available.

## Result

The run ran from 2026-11-14T07:51:49Z to 07:54:29Z and exited 0 with empty
stderr. All 120 payloads were hashed and embedded
images bound. Geometry and OCR returned for 36 items; 296 numeric readings were
observed. **Measured proposals: 0/120 complete. Estimated-position review
drafts: 0/120 complete.** There were no residual scores to report; the residual
sample count is zero with null statistics, which is not a zero-error result.

| Timing | Result |
| --- | ---: |
| Process wall | 160.3594746 s |
| Pipeline total | 159,647.5308 ms |
| Case total median / p95 / maximum | 636.0526 / 4,210.2918 / 6,320.9216 ms |
| Reused OCR, 35 calls: median / p95 / maximum | 2,314.1968 / 4,460.9217 / 5,705.4912 ms |
| Cold OCR, 1 call | 3,349.5895 ms |

Case-total timings include 84 early geometry failures; they are not full OCR
latencies for all 120 inputs.

Reason counts overlap. Measured proposals: `AXIS_AMBIGUOUS` 25,
`AXIS_NOT_FOUND` 57, `AXIS_NO_INTERSECTION` 2,
`TICK_AMBIGUOUS_ASSOCIATION` 2, `TICK_INCONSISTENT_SPACING` 2,
`TICK_INSUFFICIENT_PAIRS` 32, `TICK_INVALID_NUMERIC_LABEL` 1,
`TICK_INVALID_RANGE` 2, `TICK_MISSING_ENDPOINT` 17, `TICK_NONMONOTONIC` 1,
`TICK_UNPAIRED_LABEL` 23.

Estimated review drafts: `TICK_AMBIGUOUS_ASSOCIATION` 5,
`TICK_INCONSISTENT_SPACING` 7, `TICK_INSUFFICIENT_PAIRS` 30,
`TICK_INVALID_NUMERIC_LABEL` 1, `TICK_INVALID_RANGE` 3,
`TICK_MISSING_ENDPOINT` 17, `TICK_NONMONOTONIC` 6,
`TICK_UNPAIRED_LABEL` 10, `TICK_ZERO_NOT_OBSERVED` 2. These counts are not
mutually exclusive and describe the 36 OCR responses with incomplete review
drafts, not only drafts containing estimated positions. Both paths had zero
complete/scored proposals.

Peak sampled parent memory was 89,292,800 bytes, peak parent working set
141,598,720 bytes, and peak sampled combined memory 523,476,992 bytes. These
are sampled values, not a hard memory ceiling. Parent preprocessing and child
workers were independently capped at 0.4992 logical core each, with
BelowNormal priority; this is not a combined 0.4992-core cap.

The measured reason counts, complete-proposal count, and geometry/OCR coverage
are unchanged from 0.4.44. The estimated-draft path also produced no complete
proposal. These results show no coverage improvement from this candidate; they
do not establish an accuracy improvement or justify a threshold change.

## Identity and reproducibility

Package SHA-256:
`79f3c3a1ee9cd57ead9282a86e3b8f62206e4e0175c755543de5fc929ec3fe9a`.
App executable SHA-256:
`ef969ee4a3fcf08b0e2715c2339efb58a698a8fa9549aece4596ac9639a4ed88`;
App DLL `0117e966cc3a07dfa8124712c77355810f1cfa9e6cfbce7e09bf0c3274cbce7d`;
Rust worker `71455f26d241aec90bf2b88b4267e272028a286dd2b8e7fbc5f86ab8d27fdfa0`.
OCR reader and manifest are `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`
and `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a`.
The complete 21-file runtime identity list is pinned in the
[diagnostic adapter](../tools/GraphReader.CalibrationOcrProbe/RealDevCalibrationCheck.cs)
at this outcome commit; package identity is recorded in the
[build ledger](BUILD_LEDGER.json). Probe SHA-256 is
`be8f204558e80712cff7b3dd322752aacba61dc6102421f11ed0e589d92958b2`;
diagnostic adapter SHA-256 is
`48f794067f1fb4973a52b663434d1d020b2b15eeed89b108ef39965eab1aefb0`.
Aggregate report SHA-256 is
`4197dee049db60e95439bee82919e5eb9df071047cc5a3f0e90a2ec96aa70acd`.
Assignment SHA-256 is `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`,
selected inventory SHA-256 `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`,
and source identity digest `bbe77dd52fc4a08dd2e2a039de234677bbf0e9267d6b812864c8e87e16e5931f`.

```powershell
$probe = 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\hardware-final-build\GraphReader.CalibrationOcrProbe.exe'
$build = 'E:\Graph-auto-reader\artifacts\dev-portable\builds\0.4.45-20261114T074545114Z-4e721496'
& $probe --real-dev-calibration-check 'E:\Graph-auto-reader\data\manual data' (Join-Path $build 'GraphReader.App.exe') (Join-Path $build 'review-assistance\ocr') (Join-Path $build 'native\graphreader-engine.exe') --explicit-opt-in
```

Two owned adapter self-tests passed with zero private reads. They checked
affine residual behavior and privacy, including a missing-helper block before
selection. This diagnostic is not an independent accuracy evaluation or
production approval.

The under-100-KB `artifacts/real-dev-calibration-445-check` scratch contained
aggregate and metadata only. It was retired in the 0.4.45 cleanup after record
`c20d0577ed26fd73f1f80633f6896d1033716651` was pushed and active readers
finished. Cleanup retired 536 files totaling 371,073,104 path bytes. The 0.4.45
build/ZIP was the sole retained package at that checkpoint; it was superseded
and retired after the 0.4.46 delivery record `05dd44bb` reached main.
Shared `Data`, original corpus,
current models/runtime, native QA data and active OCR reproduction were
preserved.
