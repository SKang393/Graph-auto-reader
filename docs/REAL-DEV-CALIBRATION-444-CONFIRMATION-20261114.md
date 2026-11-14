<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Real-dev calibration confirmation, 0.4.44

## Scope and safeguards

This was a fixed confirmation of the delivered 0.4.44 package from source
`8c9d73289fb144fd8d155886d3d6a06e880b9a38`, selected on owned controls before
the run. It used all 120 items in the same frozen real-dev split as the
0.4.43 confirmation. The 51 sealed items were excluded. The run emitted an
aggregate only: no per-case output, image writes or persistent cache. It made
zero optimizer steps, performed no model or threshold selection, read no sealed
payloads, used no manual truth for inference, and applied no calibration.
Manual truth remains imperfect and does not include tick-transcription truth.

The run used the packaged App and Rust worker, not the subsequently edited
working-tree Rust candidate.

## Result

The run exited 0 with empty stderr. All 120 items were attempted and bound to
hashed payloads and embedded images. Geometry and OCR returned for 36; 296
numeric readings were observed. There were 36 incomplete pairings and **zero
complete proposals**. All 120 items were failed or incomplete. Normalized
residual counts are zero with null values because no complete proposal could be
scored; this is not a zero-error result.

| Measure | Result |
| --- | ---: |
| Process wall time | 163.8694966 s |
| Total pipeline wall | 162,846.7444 ms |
| Case total median / p95 / max | 647.6951 / 4,463.7028 / 7,041.9621 ms |
| Reused OCR median (35 requests) | 2,307.5288 ms |
| Cold OCR (1 request) | 3,675.0841 ms |
| Peak sampled parent / working set / combined | 90,157,056 / 149,831,680 / 516,071,424 bytes |

The case-total figure includes 84 cases that failed early in geometry and did
not call OCR. The reused OCR median is a separate measurement, not an overall
run speedup. The 0.4.43 run took 122.5673699 s overall with a reused OCR median
of 2,704.621 ms. The 0.4.44 run was slower overall; these medians do not imply
otherwise. Memory values are sampled observations, not a hard ceiling.

Reason counts match 0.4.43: `AXIS_AMBIGUOUS` 25, `AXIS_NOT_FOUND` 57,
`AXIS_NO_INTERSECTION` 2, `TICK_AMBIGUOUS_ASSOCIATION` 2,
`TICK_INCONSISTENT_SPACING` 2, `TICK_INSUFFICIENT_PAIRS` 32,
`TICK_INVALID_NUMERIC_LABEL` 1, `TICK_INVALID_RANGE` 2,
`TICK_MISSING_ENDPOINT` 17, `TICK_NONMONOTONIC` 1, and
`TICK_UNPAIRED_LABEL` 23. Reasons can overlap. No residual values were
available because no complete proposal was returned.

## Identity and invocation

The package ZIP SHA-256 is
`4bf422d3df3bef6d54a11df9e58b9c3d8ee7ed2de735ef29a5434efe7b2d9c01`.
The App executable SHA-256 is
`310f4ea2e409fc0e2e88e42774613cd8218d8debf4d10eb14ed4f638e6098510`; App DLL
SHA-256 is `8b08f23607a9b36f34ee89dc0cbb983f409467cd7bb42c0f1224424ec51c8dab`;
Rust worker SHA-256 is
`04d6cca4689c9d93976915c889a4177f67322c79801fb406be2f1ae84ea1ba3a`.
OCR reader and manifest SHA-256 values are
`7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` and
`17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a`.
Probe adapter SHA-256 is
`a2a8726b8da05004cc055b917a196cf3059f2d97a748459235ed5a96b5c3d14c`.
The aggregate report SHA-256 is
`9cfdbd7cbd6f7a40685bd81375fafb22cb2f59eff224729ad4dcd42e6e10b049`.
The run spanned 2026-11-14T07:07:12.2077320Z to 07:09:56.0873865Z. Assignment
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`, selected
inventory `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`
and combined first-observed source digest
`bbe77dd52fc4a08dd2e2a039de234677bbf0e9267d6b812864c8e87e16e5931f`
match the prior fixed confirmation. These are not preregistered payload hashes.

The probe-only build completed in 6.76 s with zero warnings/errors. Before
payload access, 21 packaged libraries were restaged and hash-checked, and 14
managed paths were checked. The invocation was:

```powershell
$probe = 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\hardware-final-build\GraphReader.CalibrationOcrProbe.exe'
$build = 'E:\Graph-auto-reader\artifacts\dev-portable\builds\0.4.44-20261114T065904903Z-8c9d7328'
& $probe --real-dev-calibration-check 'E:\Graph-auto-reader\data\manual data' (Join-Path $build 'GraphReader.App.exe') (Join-Path $build 'review-assistance\ocr') (Join-Path $build 'native\graphreader-engine.exe') --explicit-opt-in
```

The diagnostic adapter passed two affine residual checks and privacy checks.
Its missing-helper check blocked before selection with zero payload reads; the
positive-helper check also passed. The separate CPU-budget self-test verified
independently capped parent/child processes, 0.4992 logical core each, BelowNormal
priority and child exit. This does not establish calibration
accuracy or production readiness. Known dev exposure and imperfect manual
truth remain; no independent accuracy result or real tick-transcription truth
is available. The run is diagnostic only and does not authorize production.

The under-1-MB `artifacts/real-dev-calibration-444-check` scratch contains
aggregate and metadata only. Retire it after
this record is pushed and active readers finish. Preserve the original corpus
and current packaged/runtime inputs.
