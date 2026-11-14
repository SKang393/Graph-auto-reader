<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Manual panel crop workflow

## Implementation

Manual Preview adds a two-corner crop preview and an explicit **Create panel**
action. The selection is made in original panel pixels. A crop must be 32 to
4096 pixels on each side and at most 4,194,304 pixels total. Creating it adds a
new uncalibrated tab with no copied points; it does not resample or replace the
parent image. The crop's source checksum, parent identity, rectangle, and child
image checksum are retained so nested raster crops and PDF panel crops can be
reopened with their source lineage intact. A parent with a retained crop cannot
be closed. If crop creation completes after the user switches tabs, the child
is added to the visible tab list without changing the user's selection.

Oversized panels now direct marker assistance users to **Crop panel**. This
addresses the existing 4096-side and 4,194,304-pixel assistance limits without
changing those limits.

## Validation

The initial focused App run reported 83 passed and 3 failed of 86. The three
failures were test assumptions about JSON property order and the service save
dirty flag; the assertions were corrected. Only the three affected crop tests
were rerun, and all three passed in 978 ms (3.338 s command elapsed). The 86
tests were not rerun as a final all-pass suite.

Subsequent hosted package validation passed all 944 App tests, and source CI
passed 2,626 .NET tests across 13 projects with 30 skipped and no failures.
The [0.4.42 delivery record](HOSTED-INTERNAL-PREVIEW-20261113.md#hosted-delivery-0442)
also records actual packaged crop-to-export/reopen verification.

The final App/probe build completed with zero warnings and zero errors. The
App/probe build step took 29.101 s; App test compilation took 14.869 s. Initial
compiler/analyzer findings (CS8604, CA1822 and test analyzers) were corrected
before this final build.

An owned 2560 x 2200 periodic grayscale image was cropped to 2048 x 2048 at
`(128,64)`. Crop wall time was 43.9778 ms and process CPU time was 46.875 ms on
a 12-logical-processor host. The STA dispatcher heartbeat ran three times with
a maximum gap of 16.1523 ms. The complete headless probe took 1.542846 s and
wrote 20,408 bytes. The crop result matched selected source pixels, the parent
remained unchanged, and the saved project reopened with two tabs. The crop path
has no hard CPU cap.

Additional service checks covered two nested raster crops (41.955 ms) and two
fake-PDF crops (3.474 ms). The fake PDF path is fixture evidence, not native PDF
UI acceptance.

| Evidence | SHA-256 |
| --- | --- |
| Owned source image | `1a372f163d8c1b5a735afa719d0e9856aaa9a2adbc07b62ecdc6e9cef9808012` |
| Cropped child image | `38ad20871940401e36146cf43320864fac8cdcb8b2742d7c2cc1b25412f0fb38` |
| App DLL | `cee989b1365ad3ebcdccd77909be5d278a0b78ddc46c0363dba2870b09e45963` |
| Probe DLL | `310f323c8f6b20c99ba58a4afeb4289f584a2eafe99029bd32eb8030753e929a` |
| Probe result JSON | `b6ca9e75b2a43fda5159704354d05ca6465f533990a41f5012fb71388e6825e5` |

The App, probe, and result identities above bind the first crop measurement.
The diagnostic-only integration variant below subsequently rebuilt the probe;
the App DLL stayed unchanged.
`artifacts/panel-crop-check` remains retained for package verification; retire its raw
outputs after the integration record is pushed and the consumer finishes, per
[`ARTIFACT-RETENTION.md`](ARTIFACT-RETENTION.md).

## Reproduction

Reuse the checkout's existing .NET/NuGet environment and isolated compiler
output. Restore the ledger-pinned, reviewed OpenCV native DLL after compilation.

```powershell
$out = Join-Path (Get-Location).Path 'artifacts/hardware-final-build/'
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false "-p:OutDir=$out"
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false -p:BuildProjectReferences=false "-p:OutDir=$out"
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-build --no-restore -p:BuildProjectReferences=false "-p:OutDir=$out" --filter 'FullyQualifiedName~NestedRasterAndPdfCropsPreserveParentsAndReopenExportTrace|FullyQualifiedName~CropRequiresExplicitCreateAndAddsUncalibratedChildWithoutChangingParent'
& "$out/GraphReader.CalibrationOcrProbe.exe" --panel-crop-check <absolute-empty-output-root>
& "$out/GraphReader.CalibrationOcrProbe.exe" --marker-workflow-check <owned-request.json> <absolute-App-exe> <absolute-assets> <absolute-Rust-worker> <absolute-empty-output-root> prepared-crop
```

The exact initial 86-case filter was `FullyQualifiedName~PanelCrop|FullyQualifiedName~MarkerExemplar|FullyQualifiedName~RasterPanelWorkspaceTests|FullyQualifiedName~RasterPanelRestoreTests|FullyQualifiedName~RasterPanelExportProvenanceTests|FullyQualifiedName~ResourcesAndLocalizationTests|FullyQualifiedName~MainWindowCloseTests`.
The three-case command above is the corrected rerun. The crop probe creates its
own source. The integrated probe uses `New-TickCalibrationFixture.ps1` and the
request format documented in the diagnostic tool README.

## Actual cropped assistance integration

The fixed 640 x 480 tick/marker control was embedded at `(64,64)` in a
768 x 608 owned image, then cropped through the actual workspace service.
Rust proposed axes, the existing licensed OCR reader supplied the six observed
ticks, and simulated explicit review applied the calibration. A placed marker
example found the other two centers. CSV export and project reopen preserved
all three points, the untouched source tab, and exactly one source offset for
each saved point and calibration anchor. CSV X values remain explicitly
`observation_order`; this check does not change export semantics.

The process exited 0 in 5.537031 s; pipeline time was 4.844591 s, including
36.0054 ms crop, 2344.9596 ms OCR and 113.1217 ms marker work. The marker worker
reported one OpenCV thread, BelowNormal priority and a verified 0.4992-core CPU
budget. No model or threshold changed. Diagnostic-only compilation took
15.286 s with no warnings or errors.

| Integration evidence | SHA-256 |
| --- | --- |
| Tick control | `c1750cab62d79c96b75310cdb5419edcb91a1e99218b248894a26b731aa43dda` |
| Generated source | `1a7da07ea21ee78e62dbbcb55aac780797aa5ff5b340c9fd6c2e5f3928aa9808` |
| Child panel | `e981c6c798a178cc8f8baeb89cee985bbd3b438611cfff3a1b5178e205726625` |
| Integration probe DLL | `aad51cb4c24408eea8037598769e074edf3b527ccb14a18c5be68c5bcfd2916e` |
| Result JSON | `5355205036910758675aa0447b50dce0e47c5eded5e8b1660d0d28bc1b63a6ba` |
| CSV | `a5cab504b91b88ae3c3f6ff976e298cc3d75e47139102400dc64c0e335635302` |

## Limits

This outcome is based on `000b6118f44059c2914c95028b53e073f4d4bb3f`
plus the crop implementation committed with this record. Final independent
production-diff review reported no material finding. Version 0.4.42 is delivered
and retained, with the packaged workflow and hosted CI recorded in the
[delivery record](HOSTED-INTERNAL-PREVIEW-20261113.md#hosted-delivery-0442).
Packaging does not imply native QA or production approval.

No GUI acceptance was performed. The maintainer authorized resuming native
checks, but the next window-list call immediately reported a physical Escape
stop; no native inputs followed. REV-011 remains waiting. No private corpus or
new model/runtime dependency was used. The integration uses the already
reviewed OCR reader and native workers. Crop encoding has no hard CPU cap;
the short periodic-image measurement is not a universal responsiveness claim.
