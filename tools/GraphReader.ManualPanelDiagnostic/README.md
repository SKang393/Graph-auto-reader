<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Manual panel and axis diagnostic

This diagnostic runs the application's `ProductionRasterPanelizer`, followed
by `AxisGeometryDetector` with the reviewed OpenCV binary, on the five pinned,
already exposed internal training images from the OCR holdout. It does not
read transcriptions, train a model, modify the application, or establish
release acceptance. Image-specific geometry remains in ignored project-local
artifacts. The normal importer catches a genuine no-panel result and retains
the original image as a review panel; this diagnostic reports the lower-level
panelizer result directly.

Build serially, copy the checksum-verified reviewed `OpenCvSharpExtern.dll`
into the diagnostic's `runtimes/win-x64/native` output, and run with a new
output name. The source and input checks deliberately prevent use on another
split. For example:

```powershell
dotnet build tools/GraphReader.ManualPanelDiagnostic/GraphReader.ManualPanelDiagnostic.csproj -c Release --no-restore -m:1
dotnet tools/GraphReader.ManualPanelDiagnostic/bin/Release/net10.0-windows/GraphReader.RealAcceptance.Ocr.dll panel-diagnostic-example
```

The output name is create-only under `artifacts/manual-axis-ocr-diagnostic-v1`.
The reviewed native binary SHA-256 is
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`.
The private diagnostic outputs remain until the active axis investigation no
longer consumes them. They are not packaging inputs.
