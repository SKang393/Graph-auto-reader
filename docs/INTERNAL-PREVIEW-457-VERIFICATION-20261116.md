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

Delivery commit `4126addb1069c1f94142c4901cb8f0b3d1ea6629` was verified on
origin/main and fast-forwarded into the primary checkout before activation.
The reusable helper's read-only preflight passed in **21.6282088 s**. Activation
then succeeded once in **7.9119115 s**, at `2026-11-16T05:31:26.6280886Z`, with
all **477 payload files** and **16 shared Data files** checked and the old keeper
preserved. New pointer SHA-256:
`85930a01639b9af3909e59a3f179d4190cde35ce01c7150e3021968abf79c0a1`.

The actual primary `packaging/Run-Latest-DevPortable.cmd -Wait -PortableSmoke`
selected 0.4.57 and passed: **exit 0 / 3,236.2427 ms**, 279 informational stdout
bytes, empty stderr, all sixteen shared Data files unchanged. The retained
identity contains **478 files / 284,026,401 bytes**, including its ZIP under
`packages/`, with no internal Data directory. No native window was exercised.

After this activation record is pushed, retire the exact old 0.4.56 directory
(**478 files / 284,011,831 bytes**) after a fresh full archive/tree match and
process/path/keeper/Data checks. Local completed 457 staging contains **20 files /
87,486,365 bytes**; the completed Flex5-check script adds **one file / 5,161 bytes**.
Neither contains an active consumer. The temporary activation receipts and
retirement manifest remain until their result has been recorded and pushed.

The same closeout covers the completed owned/source-validation outputs below.
Their source and concise outcomes are already pushed; the packaged-worker
consumer has finished. Fresh exact file hashes, no tracked paths, canonical
containment, no reparse points and no executable/command-line consumers must
still pass immediately before removal.

| Closed local scope | Files | Bytes |
| --- | ---: | ---: |
| `artifacts/affine-source-bridge-check` | 13 | 55,143 |
| `artifacts/inference-key-hash-check` | 20 | 290,388 |
| `artifacts/tensor-pool-bound-check` | 9 | 179,175 |
| `artifacts/affine-review-partial-check` | 32 | 1,983,483 |
| `tests/GraphReader.App.Tests/bin` | 205 | 53,246,287 |
| `tests/GraphReader.App.Tests/obj` | 23 | 4,460,353 |
| `tests/GraphReader.Inference.Tests/bin` | 166 | 43,190,631 |
| `tests/GraphReader.Inference.Tests/obj` | 23 | 694,560 |
| Total | 491 | 104,100,020 |

Their temporary manifest SHA-256 is
`6bea6c3e1877c98a35b208e26d93a0da33db4ca5809d5238800de2c9ea144b0f`.
The probe compiler output is excluded from this scope. Source App Data, original
data, owned native-QA images, current runtime/model files, the protected
`artifacts/axis-correction-affine-check/runtime-pins.json` and unrelated primary
edits are preserved. Artifact hashes in the individual outcome documents remain
historical reproduction records after the indicated raw outputs are retired.

The combined local retirement preflight binds **990 files / 475,603,377 bytes**,
with manifest SHA-256
`6999ac3de35ecc57fdc76e6b00f6ef1c30e41e5946219c86d6f5851a0a41b1b6`.
Its first read-only invocation used the worktree for a tracked-file query on the
primary build path and refused that out-of-repository path before mutation.
Using each target's actual repository corrected that guard; the subsequent
inventory passed with eleven immutable file guards and sixteen shared Data files.
The protected runtime checkpoint is outside every deletion target.

The separate, task-created Flex5 scope contains **480 files / 284,026,407 bytes**:
the verified 477-file payload, its ZIP and two three-byte smoke settings. Its
retirement requires fresh matching payload/ZIP hashes, exact setting hashes,
contained paths without reparse points and no consumers. Preserve the unrelated
older VM, media, user data and all other validation roots.

Activation/retirement scope commit `5261adfd807e1388ae46eafc156ed60b50d7bb4d`
was pushed and integrated before removal. The first requested retirement
performed another read-only inventory because dot-sourcing a helper replaced
the ad-hoc wrapper's generic `Action` parameter. Its output identified `Inspect`,
and no deletion occurred. Using distinct retirement parameter names repaired
the wrapper; all bytes were checked again under manifest
`2822b5e1cab33228ca27e6aff1c3ba0b2c629fa338e14083388398ac85a9417a`.
The inventory content and total scope were unchanged; its timestamp differed.

At `2026-11-16T05:44:58.3896579Z`, the **990 local files / 475,603,377 bytes**
were retired and every target directory was absent. All eleven protected files
and sixteen shared Data files remained equal. The new archive and all 477
payload files matched again, and **only 0.4.57** remained in `builds/`.

The Flex5 scope was independently rechecked against its checksum-bound ZIP,
all 477 payload files and the exact two task-created settings. Canonical ancestry,
reparse and process-consumer checks passed. At `2026-11-16T05:46:32.7041179Z`,
its **480 files / 284,026,407 bytes** were retired and the scope was absent;
SSH wall time was **6.2010622 s**. No other remote directory was changed.
Combined completed-output retirement reclaimed **759,629,784 bytes**.

The small `artifacts/preview457-activation` manifests, wrappers and receipts
are now completion-only output. Retire them after this result is pushed and no
recording consumer remains. The source helper/tests, current package and uploaded
artifact are retained; historical raw-output hashes above are no longer local
file promises.

The initial closeout was pushed in `4eeab5ff`, followed by the owned diagnostic
checkpoint `3f3678eb`. The latter passed full Windows CI 43816512059. At
`2026-11-16T06:27:25.9647379Z`, the remaining activation receipt scope was retired:
**15 files / 343,634 bytes**, with all eleven protected files, sixteen shared
Data files and the 457 keeper preserved. Only its compact preservation baseline
was retained for the active owned follow-up, outside the retired scope.
