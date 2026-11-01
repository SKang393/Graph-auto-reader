<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Manual panel and axis diagnostic

This diagnostic runs the application's `ProductionRasterPanelizer`, followed
by `AxisGeometryDetector` with the reviewed OpenCV binary, on the eight pinned
build images, five exposed internal holdout images, seven other pinned
training-source groups, the 20 corresponding unmodified training images, or the
pinned 16-image development capture. The `original` profile selects those 20
unmodified images from the checksum-verified training-source receipt. It does not
read transcriptions, train a model, modify the application, or establish
release acceptance. Image-specific geometry remains in ignored project-local
artifacts. The normal importer catches a genuine no-panel result and retains
the original image as a review panel; this diagnostic reports the lower-level
panelizer result directly and separately analyzes a full-image fallback using
the same source bytes. This reproduces the fallback geometry, not the entire
application import or review workflow.

Build serially, copy the checksum-verified reviewed `OpenCvSharpExtern.dll`
into the diagnostic's `runtimes/win-x64/native` output, and run with a new
output name. The source and input checks deliberately prevent use on another
split. For example:

```powershell
dotnet build tools/GraphReader.ManualPanelDiagnostic/GraphReader.ManualPanelDiagnostic.csproj -c Release --no-restore -m:1
dotnet tools/GraphReader.ManualPanelDiagnostic/bin/Release/net10.0-windows/GraphReader.RealAcceptance.Ocr.dll build panel-diagnostic-example
```

Use `original` in place of `build` for the pinned unmodified-image profile.

The output name is create-only under `artifacts/manual-axis-ocr-diagnostic-v1`.
The reviewed native binary SHA-256 is
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`.
An optional final `wide` argument changes the diagnostic Hough minimum line
length from 12 to 40 pixels. Because the provider also derives its local ink
bridge limit from that setting, this changes both behaviors and is exploratory;
the application continues to use its normal provider options.
The private diagnostic outputs remain until the active axis investigation no
longer consumes them. They are not packaging inputs.
The follow-up connectivity measurement is recorded in
[CONNECTIVITY_RESULT.md](CONNECTIVITY_RESULT.md).
The study-partitioned development review is recorded in
[DEV_CONNECTIVITY_RESULT.md](DEV_CONNECTIVITY_RESULT.md).
