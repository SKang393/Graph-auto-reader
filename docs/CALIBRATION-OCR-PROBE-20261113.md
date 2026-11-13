<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recognition-only calibration probe, 2026-11-13

## Decision

Reuse the existing licensed English reader for review assistance. One bounded
owned-image call succeeded without the full automatic detection pipeline.
Cold session creation was the largest measured stage, so the next implementation
must retain a worker/session instead of loading the reader for each click.
This is a capability and latency observation, not an accuracy gate or model
production approval. Cactus remains deferred for the separately recorded
[response-contract failure](CACTUS-WINDOWS-PROBE-20261113.md).

## Reproducible scope

The source base was `d698e218d7ef15205a350708ef9643ea24cb6b8c`, with the five
new `tools/GraphReader.CalibrationOcrProbe` source files frozen for this run and
committed with this outcome. The tool uses existing component detection, role
geometry, crop batching, manifest-defined reader settings and strict numeric
parsing. It does not run detector ONNX, enhancement, markers or semantics.

One explicit owned six-panel synthetic PNG was read, SHA-256
`75edb079ccfa8a3a2806bef467ec2dd10d13905bee762fd48ee9be21361be98d`.
The 1200 x 1700 original was cropped in memory to `(0,0,1200,313)`, with
plot bounds `(105,96,1095,260)` relative to that crop. Output polygons map to
original source pixels. The previously manually entered 10/1/24 labels were
neither inputs nor targets. No image/model copy was written.

The retained PP-OCRv5 English reader is pinned to SHA-256
`7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`,
manifest `17259b3260c0d39b292be70b36e68d53a067992de3afeaf0809d4d981de4327a`.
The exact manifest supplies BGR NCHW preprocessing, height 48, variable width
320 to 4096, normalization, dictionary, CTC decoding and spacing behavior.
The source is the currently retained reader under
`artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/ocr-v43-extended/candidate-v1`.

CPU only, one intra-op thread, one inter-op thread and one queue worker were
used. Before OCR, the process applied and queried a Windows Job hard cap of
416/10000 across 12 logical processors, equivalent to at most 0.4992 logical
cores of aggregate execution time, and verified BelowNormal priority. This
does not guarantee an individual core never briefly reaches 100 percent.
There was one session, no persistent result cache, no optimizer, no private
data and no sealed-split read. Cooperative/hard/parent deadlines were
170/175/180 seconds. Candidate and response limits were 128 and 1 MB.

## Commands and results

```powershell
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Release --no-restore -m:1 -nr:false -p:BuildProjectReferences=false -p:NuGetAudit=false -p:UseSharedCompilation=false --verbosity minimal
& $probeExe --self-test
& $probeExe --request $explicitOwnedRequest
```

Restore completed in 3.293 s. An initial analyzer error, CA1826 on an
IReadOnlyList FirstOrDefault call, was corrected to Count/index access before
inference. The final tool-only build passed with zero warnings/errors in
5.298 s. Model-free self-tests passed in 1.192 s, including crop geometry,
source offsets, manifest crop dimensions, nonnumeric/failure behavior and the
queried CPU limit. An independent source review found no concrete blocker.

Exactly one inference process ran and exited 0. Its parent measured 5.042 s
wall time and 2.078 s process CPU time. Internal total was 4.119 s.

| Stage | Milliseconds |
| --- | ---: |
| Decode | 125.975 |
| Components and candidate selection | 47.997 |
| Crop preparation | 65.623 |
| Cold ONNX session initialization | 2343.763 |
| First recognition after initialization | 1356.048 |
| Reader-reported inference within recognition | 1063.935 |

This first recognition is not a warm steady-state measurement. Identity
validation and other host work are included in total, outside the named stages.
73 components yielded 9 candidates in one batch: 7 numeric literals,
2 nonnumeric strings and 0 recognition failures. Recognized numeric text was
`10, 4, 1, 7, 13, 19, 24`; the other text was `Participant 01` and `Outcome`.
Every proposal had `requires_review=true`. No calibration or exported values
were produced. In particular, these candidates do not establish a complete
Y-axis tick sequence or zero baseline. Missing evidence must remain visible.

ORT emitted 13 unused-initializer warnings, with no runtime failure. There was
no retry, threshold change, candidate comparison or accuracy denominator.
One image does not establish general latency or recognition quality. The
different workload and runtime preclude a direct Cactus speedup claim.

## Identity and license

| Item | SHA-256 |
| --- | --- |
| Probe DLL | `c822cc5db2ba860e0b4887a90676e7e53a2e21ea1d175d680ff871cac5e29bf8` |
| Loaded ONNX Runtime DLL | `e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40` |
| Request | `729a8639bdd88dc684ca6c82bd47fb048d82a14a26bd2f72073f55c332ce6465` |
| Result | `e60082bab5214d1c26b28635b60c0a2b9e2466c3128dc94e2c9feb2a0e5d7492` |
| Immutable identity receipt | `26fdbac2e60691644cd9f78522dac11d83698aa8ff89f9e003ee780acd1be673` |

Reader Apache-2.0 license SHA-256:
`c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4`.
Reader notice SHA-256:
`8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729`.
Both were verified before use and after the run. No dependency or model weights
were added to a distribution. Existing ONNX/OpenCV notices remain applicable.

## Retention and next acceptance

The temporary request, 4275-byte stdout, 3087-byte stderr, execution and identity
receipts under `artifacts/calibration-ocr-probe-20261113` were retired after
this record reached main at `286382c0` and consumers finished: five files,
15,009 bytes. Keep the original owned input and
current reader/manifest/license. No new dataset or environment was created.

The previous pushed Rust-assistance outcome's completed TRX, timing and native
QA directories were removed: 30 files, 1,827,891 bytes. Original inputs and
active native compilation output were preserved.

Next acceptance is source-bound, cancellable review assistance in WPF with a
reused session, visible literal labels and no silently applied calibration.
GPU selection and warm/end-to-end timings remain unmeasured. The standalone
probe passed its bounded execution check; the final automatic product remains
incomplete.
