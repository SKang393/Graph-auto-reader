<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Delivered calibration assistance: fixed real-dev diagnostic

This checks the already delivered 0.4.42 calibration assistance against all
120 frozen real-dev Engauge projects. The denominator was fixed before the
run. It does not select models, thresholds or candidates, train weights,
apply a calibration, or approve production. The 51 sealed project payloads
are excluded by the existing frozen selector before any payload is opened.
The [manual-data authorization](GOAL-22-MANUAL-DATA-AUTHORIZATION-20261030.md)
continues to govern the corpus.

## Fixed runtime and method

The application is the verified
[0.4.42 package](HOSTED-INTERNAL-PREVIEW-20261113.md#hosted-delivery-0442),
built from `0558b3fad288a623d49f6e898646083a365f8fd1`. The diagnostic source is
based on `0cfecd315adf1793a80477d44f23ab6ddc7a17a9` and committed with this
outcome. No application rebuild is used for this measurement. The delivered
EXE, Rust worker, OCR model/manifest/notices, 21 supporting libraries and the
14 actual loaded managed-library paths are hash checked. Independent review
matched all 21 library pins directly against the verified ZIP.

The existing locked Engauge parser binds each embedded original image to its
project. Rust receives pixels and detects geometry; the persistent CPU OCR
worker reads labels, and the existing pairing cross-check produces a review
proposal. Manual calibration anchors are evaluator-only, used after inference.
Continuous residuals measure absolute graph-coordinate differences divided by
the manual anchor range. There is no residual pass threshold or per-case output.

Output contains aggregate coverage, failure reasons, residual summaries,
stage timings, sampled memory and one combined input digest. A failed or
unsupported case remains in the 120-project denominator. One project is held
at a time; no source image, dataset, prediction archive or persistent cache
is written. Output is capped at 1 MB. Deadlines are 45 seconds per case,
900 seconds overall and a 905-second process watchdog.

## Harness correction before valid measurement

The initial probe-only build passed in 11.04 seconds with zero warnings and
errors. The owned residual/output self-test passed in 0.501753 seconds, without
private payload reads. Its two affine checks and forbidden-output rejection
passed. Initial adapter SHA-256:
`af99c8370b1fb66d1375e479b7d45c6e4418ffb544a41956522d6ee6a8ac4638`.

The first real-dev attempt was stopped after **123.837194 seconds**, exit -1,
with zero-byte stdout and stderr. It is **void due to diagnostic plumbing**,
not an application accuracy or performance result. The harness parent job
contained independently capped worker jobs. Windows calculates nested CPU
quotas relative to the parent allocation, multiplying the intended limits;
see [Microsoft's CPU rate documentation](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information).
The intended approximately half-core child limit was therefore much smaller.
Only the verified diagnostic parent and its child were stopped; a follow-up
process check found no residual child. No calibration metrics were produced,
and the number of attempted private payloads was not recovered.

The correction is confined to diagnostic job ownership. Real-dev mode now
sets and queries the parent's `SILENT_BREAKAWAY_OK` flag; other modes retain
their existing behavior. Parent preprocessing stays capped, while packaged
children retain their independent caps. The build passed in 12.35 seconds with
zero warnings/errors. The owned child-budget check passed in 0.833747 seconds:
both caps were 0.4992 logical cores (12 logical processors, rate 416), the live
child was outside the parent's job, both priorities were BelowNormal, and the
child exited. The owned aggregate self-test passed again in 0.241456 seconds.
Neither self-test read private payloads. Corrected adapter SHA-256:
`b892e70e59ff9155aefb3f624accefff9c2e9cfd7b45538d7134875f87385f5d`.
Independent source review found no material issue. No application, model,
threshold, corpus membership or denominator changed for the retry.

The diagnostic-only build uses `BuildProjectReferences=false`, then restages
and hash-checks the delivered managed/native libraries. Verification commands:

```powershell
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false -p:BuildProjectReferences=false -p:OutDir=<isolated-probe-output>
GraphReader.CalibrationOcrProbe.exe --cpu-budget-self-test
GraphReader.CalibrationOcrProbe.exe --real-dev-calibration-self-test
GraphReader.CalibrationOcrProbe.exe --real-dev-calibration-check <absolute-corpus-root> <verified-0.4.42-App.exe> <verified-ocr-assets> <verified-Rust-worker> --explicit-opt-in
```

## Completed fixed run

The corrected run completed from 2026-11-14T04:39:04.656795Z to
04:42:33.752628Z, exit 0, in **209.084011 seconds** process wall time. Its
reported pipeline wall time was 208.378597 seconds. All 120 selected project
payloads were hashed and all 120 embedded originals were bound successfully.
No sealed payload was read, no optimizer ran and no calibration was applied.

| Aggregate outcome | Count |
| --- | ---: |
| Attempted / fixed denominator | 120 / 120 |
| Geometry returned | 36 |
| OCR responses returned | 36 |
| Numeric readings offered for review | 298 |
| Complete calibration proposals | 0 |
| Incomplete tick pairings | 36 |
| Complete proposals with evaluable anchor residuals | 0 |
| Axis not found / ambiguous / no intersection | 57 / 25 / 2 |

Pairing reasons overlap: insufficient pairs 32, unpaired label 23, missing
endpoint 17, ambiguous association 2, inconsistent spacing 2, invalid range 2,
invalid numeric label 1, and nonmonotonic sequence 1. No timeout, unsupported
dimension, parser failure or runtime identity failure was reported. Residual
statistics are undefined because no complete proposal reached that evaluator.
Numeric reading counts are not transcription accuracy or recall.

| Stage | Count | Median ms | p95 ms | Maximum ms |
| --- | ---: | ---: | ---: | ---: |
| Whole case | 120 | 222.843 | 6,895.559 | 12,006.840 |
| Decode | 120 | 9.593 | 64.572 | 256.782 |
| Rust geometry | 120 | 58.935 | 308.660 | 1,065.398 |
| First OCR request | 1 | 5,259.541 | 5,259.541 | 5,259.541 |
| Reused OCR request | 35 | 4,132.389 | 10,313.074 | 11,832.852 |
| Recognition | 36 | 3,880.037 | 9,935.590 | 11,306.387 |
| Tick pairing | 36 | 0.295 | 66.964 | 131.338 |

The low whole-case median includes 84 early geometry failures and must not be
described as successful end-to-end extraction speed. OCR initialized once in
2,152.556 ms and reused that session for 35 further requests. Recognition is
the dominant measured cost when the geometry stage succeeds.

Peak sampled parent private memory was 102,617,088 bytes; parent peak working
set was 149,757,952 bytes. Peak sampled combined parent/worker private memory
was 1,218,859,008 bytes. These samples are not a hard memory ceiling. The
completed scratch tree had 11 files totaling 9,393 bytes, including both owned
self-tests and the void record. No image or dataset copy was created. A
post-run process check found no remaining child of the diagnostic parent.

| Identity | SHA-256 |
| --- | --- |
| Frozen assignment | `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a` |
| Selected metadata inventory | `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d` |
| Combined first-observed payload/image digest | `bbe77dd52fc4a08dd2e2a039de234677bbf0e9267d6b812864c8e87e16e5931f` |
| 5,609-byte aggregate report | `3eb2ee0c6502a8c26e02c71eb24c52fe117b8359c8878435f316d12e848c3eb6` |

The report has no per-case identifiers, labels, coordinates or predictions;
stderr was empty. The payload digest is complete for this run, not a claim
that the original contents were preregistered.

## Development consequence

The harness execution passed; useful complete automatic calibration remains
unestablished. This is a workflow coverage result, not a new compound model
acceptance gate. Do not report the application as automatically calibrated
because an owned control passed. The next engineering work must diagnose
geometry coverage and recognition cost, confirm input-property gaps without
private-answer tuning, and reproduce repairs on owned/synthetic images. Do not
relax scientific cross-checks, select a competing model on these answers, or
launch training to hide the failure. Partial label review and manual calibration
remain available while this defect is addressed.

## Interpretation and retention

These development images have known prior exposure and imperfect manual
calibration. The corpus has no tick-transcription annotations. Coverage and
anchor consistency are not independent OCR accuracy, silent-corruption proof,
native UI acceptance, installer acceptance or production approval. Proposed
values still require visible review and explicit acceptance in the application.

Only concise aggregate outcomes are eligible for Git. The bounded scratch
directory was `artifacts/real-dev-calibration-check`. After source and outcome
reached `origin/main` at `11771f6e0d70972951fd511b640f1a799c8ee009`, guarded
cleanup removed its 12 files totaling 10,572 path bytes. That includes one
additional compiler log created after the 9,393-byte run-time inventory above.
The target was confirmed absent and the current package/shared Data present.
Retain the single verified 0.4.42 package, current runtime assets, original
private data and the separately active native QA dependencies.
