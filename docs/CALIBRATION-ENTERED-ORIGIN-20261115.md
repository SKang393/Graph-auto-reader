<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Calibration review refinements

## Measured-label grouping

The [saved development diagnosis](CALIBRATION-SOURCE-TRACE-20261115.md) led to
an owned reproduction with three separate 3 x 8 glyph blocks and measured
X ticks `[31, 41, 51, 61, 71]`. The unlabeled intermediate ticks prevented the
old adjacent-tick rule from separating the three labels: expected three crops,
actual one. The failing-before test took 17.6976095 seconds including compilation.
This proves the owned defect, not its exact cause in the observed private row.

The detector now also recognizes two uniquely supported label lanes separated
by unlabeled minor ticks. Existing full-height support, wide-gap and crossing-ink
guards remain. It does not assign a numeric label to an unlabeled tick or split
a wide decimal merely because multiple ticks lie beneath it.

The focused OCR filter passed **54 tests, 0 failed, 0 skipped** in 12.4434938
seconds including compilation. It covers the failing control, wide decimals,
crossing ink, nonidentity coordinates, prior X/Y grouping, rotation and crop
guards. An intermediate compile stopped on the obsolete `DataTestMethod`
attribute; replacing it with `TestMethod` corrected that test-only issue.
Final TRX SHA-256:
`83e59308c13852972a7bb1876f4fd17373664ec129b12091e9fdb85958e491a8`.

## Entered first-visible session

Two development panels previously failed only the positive first-anchor range
rule. Simply picking a later positive tick would misuse the persisted
`first_visible_session` assertion if earlier observations are visible. That
approach was not implemented; the existing contract and schema are unchanged.

The new **Fit for entered first session** action uses an explicit positive
integer entered by the user. All included literal tick pairs still pass the
existing association, monotonicity and all-inlier checks. The fitted printed-X
transform maps that supplied value to an original-pixel position within the
observed tick, plot and image bounds. It never defaults the origin to session 1,
extrapolates beyond the evidence or changes the raw OCR rows. Automatic drafts
without the explicit input retain their previous behavior.

Calculated anchors keep fractional pixel coordinates and a visible review
warning. Edits invalidate the tick proof and require fresh review; stale image
or row selection cannot apply the cached fit. After explicit confirmation, the
existing three-anchor manual path saves a truthful user-confirmed origin. No
separate transform, OCR-exact provenance or new project schema is persisted.

The focused App filter passed **92 tests, 0 failed, 0 skipped**, 6 seconds in
tests / 120.0152966 seconds including compilation. Its owned 0/5/10 control
retained all six X/Y pairs, mapped entered session 1 to pixel 31.8, required
confirmation, then applied, saved, reopened and exported a session-1/Y-5 point.
Out-of-span input, insufficient positive range, an earlier outlier and changed
selection remained blocked. TRX SHA-256:
`6dd8408aa826b92173230c412e35c1acb43ed1e85ed30655c44d69968944a4e9`.

The integrated App suite passed **1067 tests, 0 failed, 0 skipped**, 56 seconds
in tests / 60.3725744 seconds wall time using the reviewed OpenCV runtime and
`--no-build`. Independent source review found no material issue. These are
headless/component checks, not visual acceptance of a packaged native window.

```text
dotnet test tests/GraphReader.Ocr.Tests/GraphReader.Ocr.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false --filter "FullyQualifiedName~AxisLabelGroupingReproductionTests|FullyQualifiedName~MeasuredYTickGroupingTests|FullyQualifiedName~OwnedDigitOrientationReproductionTests|FullyQualifiedName~GraphReader.Ocr.Tests.TextRegionDetectorTests|FullyQualifiedName~GraphReader.Ocr.Tests.CropAndMaskTests"
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter "FullyQualifiedName~CalibrationTickProposalTests|FullyQualifiedName~CalibrationTickViewModelTests|FullyQualifiedName~ResourcesAndLocalizationTests|FullyQualifiedName~ShellContractTests"
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:NuGetAudit=false --logger "trx;LogFileName=integrated-entered-origin.trx" --results-directory artifacts/minor-tick-anchor-check --verbosity minimal
```

## Bounded native candidate check

Source inspection suggested that separate line-support sets might describe the
same geometry. One owned discriminator used the existing black, gray and
two-plot controls plus a fixed two-pixel-thick gray plot, with one OpenCV thread
and the native worker's 128-pair limit. Black, gray and thick plots each produced
one ranked candidate and one distinct rounded public geometry; the two-plot
control produced two distinct candidates. No provider or detector failure
occurred. This did not reproduce duplicate geometry, so no production candidate
gate or detector threshold changed. It does not explain the five private native
rejections by itself.

An initial build went to the older shared output. After correcting the output
location, MSBuild replaced the reviewed native DLL with the NuGet copy, making
that run void for the intended runtime. The corrected check restaged and
verified reviewed OpenCV before and after execution; it exited 0 with empty
stderr in **0.5715232 seconds**. Its report SHA-256 is
`036bbf19c68d09b4a30b47e5863a7233202edb94896411b63c9b403aaef34509`;
probe DLL `566850aa1626a9df0c9d749420343225e3e7d0cce6f63712133008b3ce85daa6`.
No private payload was read in any of these owned attempts.

## Current status and retention

One actual Rust/CPU OCR run then processed an owned 640 x 480 graph with printed
X labels 0/5/10 and Y labels 10/5/0. Inference received the original image and
detected geometry/ticks, not the known plot or expected labels. Rust detected
X ticks 140/340/540 and Y ticks 80/230/380. The default draft remained incomplete
with only `INVALID_RANGE`; explicitly selecting first session 1 produced a
complete review draft at calculated X pixel `180.00000000000003`, retaining all
six paired labels. Nothing was applied. The separate component tests above,
not this worker run, cover save/reopen/export.

The single run exited 0 with empty stderr in **6.2619844 seconds** wall time:
geometry 120.6328 ms, OCR call 4995.727 ms, fit 84.8492 ms, pipeline 5414.425 ms.
The worker reported 3866.1066 ms internally, including 2308.9877 ms cold
initialization, 1271.1433 ms recognition and 61.3102 ms component detection.
This one cold run is not a performance comparison. The CPU budget was verified
at 0.4992 logical core, BelowNormal priority, one worker and one inference
session. Persistent caching remained disabled.

| Owned worker identity | SHA-256 |
| --- | --- |
| Generated PNG, 4092 bytes | `966cbe6d3b3eb7f09f6e8a35ddcc7daac7086886284608a637ae61567dbafdc8` |
| Request | `6d5741e8cc833ce0ad634d4fd80858855a2eee0136bb58eff6fb0b1377a88514` |
| App DLL | `499f931028157b201ce0603516cb701f25d675c6fa573268ca427d0547a6dd6a` |
| OCR DLL | `f1c5fbf8caf1895d7f58c2dcf101e4889ff0ade3185daeac7da64d2861dae7a8` |
| Probe DLL | `b3a45513b45b6c703b3b334a3bea66dfde617079cb31306d3293a86d621e5f29` |
| Result | `bcdc760bec6f13bb678f6ec5b75dd5582deafb4df47fb466124c4673bd0c1fb0` |

The retained OCR model remains `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`;
reviewed OpenCV was verified after compilation and before execution. The source
runtime and original owned input hashes stayed unchanged during execution.

```text
New-TickCalibrationFixture.ps1 -OutputPath <owned-zero-origin.png> -ZeroOriginXLabels
GraphReader.CalibrationOcrProbe.exe --owned-entered-session-worker-check <owned-request.json> <source-App.exe> <retained-452-ocr-assets> <source-Rust.exe>
```

**PASS for the scoped source, owned integration and actual worker checks.**
There is no new private evaluation, package, production approval, dependency or
model. The last real development availability result remains 0/12 complete
automatic drafts; an explicit origin action is assistance, not automatic origin
detection. Native visual acceptance of this new action remains unverified.

After source/outcome commit `7f0b537d` reached `origin/main`, guarded cleanup at
2026-11-15T07:40:12.4473303Z removed **251 files / 147,295,759 bytes**:
nine finished files from `artifacts/minor-tick-anchor-check`, all six files from
`artifacts/native-candidate-owned-check`, and all 236 files from the superseded
`artifacts/hardware-final-build`. The last directory had no image/model/project
assets, current source/script references or active consumers. Canonical scope
and reparse checks preceded removal. The 4092-byte owned PNG remains temporarily
for the native check below. Active source compiler dependencies, original inputs,
shared user Data and the sole 0.4.52 package remain intact.

## Isolated native check, waiting at image import

The unpackaged source app launched through Computer Use from its existing
`src/GraphReader.App/bin/Release/net10.0-windows/win-x64` output. App DLL SHA-256
remained `499f931028157b201ce0603516cb701f25d675c6fa573268ca427d0547a6dd6a`;
reviewed OpenCV remained
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`.
The window and Open dialog were observed with matching native accessibility
and screenshots. This is startup/import-dialog evidence only.

The source output had no `Data` directory before launch and created its own
portable `Data` afterward. The shared preview Data override was absent. Five
temporary hardlinks reuse the retained package's OCR assets without a second
model payload; each hash matched its source. These links and the isolated Data
remain active until this source window closes and its check is recorded.

Computer Use file-name edits repeatedly returned
`element 91 is not available in cached app state for GraphReader.App.exe`.
Refreshing the unique returned window and one clean JavaScript-tool reset did
not repair that boundary. Keyboard/coordinate focus attempts did not produce
a verifiable focus change. No image was imported or calibration applied. The
maintainer was asked to select the owned PNG in the already-open dialog;
[REV-011](REVIEW_QUEUE.md) tracks the waiting native phase. Independent geometry
work continues. No package was created, and this attempt does not establish
visual acceptance of the new fit action.
