<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Internal preview 0.4.57 verification

This integrated ManualPreview preserves independently observed ticks during
explicit tilted-axis label review, fixes rectification pacing, hashes inference
tensors in compatible chunks and bounds idle tensor retention. See the
[partial-axis review](AFFINE-REVIEW-PARTIAL-TICKS-20261116.md),
[pacing](AFFINE-RECTIFIER-PACING-20261116.md) and
[inference resource](INFERENCE-RESOURCE-EFFICIENCY-20261116.md) outcomes.
Missing values and unvalidated calibrations still require visible review; this
does not enable production Auto Detect or change model thresholds.

## Source and hosted build

Source `4db78d87bcd24eac4e92dc96e7201f0ab2f615f2` passed
[Windows CI 43806598211](https://github.com/SKang393/Graph-auto-reader/actions/runs/43806598211),
job `133098695231`, in **507.387 s**: **3,220 .NET passes**, **30 expected skips**,
zero failures, **80 Rust tests**, **68 packaging checks** and **9 localization
controls**. All 1,325 App tests ran. Release jobs were skipped and production
readiness remained false.

One [hosted build 43807630454](https://github.com/SKang393/Graph-auto-reader/actions/runs/43807630454),
job `133101991819`, completed in **292.922 s**. It passed **1,489 tests**, with
**nine skips** and zero failures: App 1,323/2 skipped, Domain 23, Integration
143/7 skipped. The two App skips were the supplied-box PNG worker controls,
because that workflow did not set `GRAPHREADER_RUST_WORKER`. Both controls were
subsequently run once against this downloaded package's Rust executable using
the already built test host: **2/2 passed**, zero skips/failures, **484 ms** test
duration / **3.05934 s** wall. No App rebuild or repeated full suite was needed.

```text
GRAPHREADER_RUST_WORKER=<verified package>/native/graphreader-engine.exe
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-build --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter FullyQualifiedName~AxisTickMeasurementServiceManualPartialTests.ActualWorkerRetainsOnlyObservedAxisThroughPngService
```

Those are two owned controls with supplied geometry, not automatic geometry,
OCR accuracy or native UI acceptance. Each checks strict and partial behavior.

## Downloaded identities

Uploaded [artifact 14199710241](https://github.com/SKang393/Graph-auto-reader/actions/runs/43807630454/artifacts/14199710241)
was created at `2026-11-16T05:07:12Z`, expires `2027-02-14T05:02:18Z`, and contains
the **87,637,402-byte** `GraphAutoReader-0.4.57-win-x64-internal-preview.zip`.
The outer download is **87,378,839 bytes**. One download took **8.4961903 s**.
Build directory: `0.4.57-20261116T050648697Z-4db78d87`; build time:
`2026-11-16T05:06:48.697006Z`.

All **477 payload files / 196,388,999 bytes** matched before and after the local
smokes. Archive/path exclusions, exact source/version, self-contained host,
ManualPreview metadata, Rust lineage and reviewed runtime notices passed.
The **36 runtime/model/license files** compared with 0.4.56, including all five
review-OCR files, were unchanged. No dependencies, weights or licenses changed.

| Identity | SHA-256 |
| --- | --- |
| Outer artifact | `fff0b991a425a51ac4a1c18be003a67079275e18e4bcc2a4bba86ab7acdadde1` |
| Inner ZIP | `687b6164fd9343ecfe4fc56ea3951b27520af8c13bfdb0fdcfd6c1670a1acbf1` |
| App EXE | `89b399ca27411af9d34f8eaf9dec4d094a268e4c767f34c32d5cd48836dbcd19` |
| App DLL | `e868a2f4bf78418244cb95eb79f1b30c0266074962d257dd5ab2ddb9de25922b` |
| Build metadata | `976f98ab97ef84836d007eb41a5e05c7729c6fe7c685f791e8ea293562585f9d` |
| Rust worker | `19e24a703907281341b183ffc0c16a6a7ab12bb1c4a2b4e97f41e16a76268347` |
| Staging manifest | `db52e50806e8b4869dcdcef930c0a817f4386f2795770849ab41902309f3696f` |
| Hosted receipt | `3c0f088ed7dddc69afc2d781e40d1b403c4715b4d24b2e55a772c031ac64c4b4` |

FileVersion is `0.4.57.0`; ProductVersion is
`0.4.57+4db78d87bcd24eac4e92dc96e7201f0ab2f615f2`. Reviewed OpenCV remains
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`;
its clean-machine evidence and release approval remain false. The reviewed OCR
model remains `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`.

## Headless checks

Both hosts used hidden children, a 30-second limit per command and isolated
task-owned Data outside the payload. All command stdout/stderr were empty.

| Host and command | Expected / actual exit | Wall ms |
| --- | --- | ---: |
| Desktop `--portable-smoke` | 0 / 0 | 1,046.2221 |
| Desktop `--production-runtime-smoke` | 2 / 2 | 368.5119 |
| Existing Flex5 `--portable-smoke` | 0 / 0 | 733.3164 |
| Existing Flex5 `--production-runtime-smoke` | 2 / 2 | 230.3026 |

Desktop verification ended at `2026-11-16T05:14:36.3362025Z`, preserving all
16 shared Data files. Flex5 received the ZIP once in **4.8170871 s** and verified
all 477 files, source, version and Rust hash. Its check completed at
`2026-11-16T05:20:30.8101522Z`, **14.1339942 s** SSH wall. The first verification
invocation exceeded Windows' command-line limit before remote execution
(0.6634929 s); the same payload was then checked via stdin transport. No ZIP
upload, extraction or smoke was repeated. No OCR/model/private-input inference
or UI was exercised. Exit 2 confirms refusal of the unapproved production
runtime, not a failing portable launch.

These single startup samples establish existing-host headless compatibility.
They do not establish clean-VM, installer, native visual workflow, complete
automatic real-graph or production acceptance. The desktop tool still reports
its physical Escape stop despite renewed authorization and no new Escape.
No new private comparison, sealed evaluation, paid credits or reset was used.

## Activation and retention

The delivery ledger selects 0.4.57 and marks activation and superseded 0.4.56
retirement pending. Push that record before using the
[tested activation helper](INTERNAL-PREVIEW-ACTIVATION-20261116.md). Retain shared
user Data outside build directories. Verify the actual latest launcher, push
the outcome, then separately retire the exact previous keeper after archive,
containment, process-consumer and replacement/Data checks.

After its concise outcome is pushed and consumers finish, retire local staging
`artifacts/hosted-preview-457`, the completed local
`artifacts/flex5-457-check` script, and the isolated Flex5 scope
`%LOCALAPPDATA%/GraphAutoReaderValidation/preview457/calibration-review-20261116`.
The sole local keeper and uploaded artifact preserve the package. Keep original
images, owned native-QA inputs, protected runtime checkpoint and current models.
