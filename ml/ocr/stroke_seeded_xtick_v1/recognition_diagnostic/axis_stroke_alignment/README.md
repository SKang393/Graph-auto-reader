# Consumed probe axis stroke alignment

This diagnostic regenerates each source panel image in memory from the renderer recipe pinned in the already-consumed synthetic P2 probe manifest. It checks each full source raster's raw pixel SHA-256 before starting any detector call, and checks regenerated probe crop PNG hashes against the manifest. No full-panel image files are written.

The axis input uses the production `ProductionRasterFrameDecoder`, then `AxisGeometryDetector` and `OpenCvLineCandidateProvider` with defaults. No OCR model or training is involved. Each renderer panel box is cropped from the verified full source raster; detected panel-local X ticks are translated back into full-source original pixel coordinates. The returned usable-stroke filter mirrors the production `CreateOcrXAxisTickStrokes` criteria.

## Results

The pinned manifest has 16 admitted source groups and 24 panels. All 16 reconstructed full-source raster hashes matched their manifest `source_pixel_sha256`; all 359 admitted crop image hashes also matched. The renderer regenerated 422 source tick geometries. Only 181 had visible rendered strokes; 241 were invisible and were excluded from detector recall matching. The matching denominator is therefore 181 visible strokes, not 422.

The earlier offset-alignment v1/v2 preliminary reports counted all generated stroke geometry, including invisible strokes, in the truth denominator. They are superseded by v4 and must not be used as recall estimates. A v3 preflight failed before detector invocation because the renderer stores ticks within panel records rather than its empty top-level tick list; the lookup was corrected and v4 completed.

All 24 panels completed without detector failures. The axis detector emitted 37 usable X tick strokes. Unique nearest-center matching found 25/181 visible truths, leaving 156 unmatched truths and 12 unmatched detections. No truth had multiple detections within the matching tolerance. The tolerance is diagnostic: `max(3 px, half the annotated stroke length)` per truth. Matched center error was 1.769 px mean, 1.558 px median, 2.696 px p90, and 3.192 px maximum. Endpoint RMS line error was 1.352 px mean, 1.203 px median, 1.938 px p90, and 2.329 px maximum.

Of 25 matched cases, 23 were positive-label crops, 2 were excluded/ambiguous rows, and 0 were background-only rows. For the 23 matched positives with validated actual renderer glyph masks, 2 detected centerlines intersected a mask, covering 3 pixels of 311 detector-line pixels inside the crop canvases. Masks were checked after detection and were not used for detector inputs or matching. The mask index hash is `db101ed7657f5f2d4e2522df46736b94a672e3c4afbbaf33de5871cb23def2d4`.

This is a consumed synthetic development diagnostic, not held-out acceptance or production approval. The 63 background crop candidates previously excluded as byte duplicates of training crops are regenerated from the full source recipes; source panels remain bound to their disjoint source-pixel hashes. The reported matches use an explicit geometric tolerance and are diagnostic, not a gate. Glyph overlap is measured only for matched positives with available verified masks, not all detected strokes.

## Reproduction

Build using project-local .NET state, then run through the project Python launcher. All panel rasters stay in process memory:

```powershell
$state='E:\Graph-auto-reader\tools\gpu-training\.state\axis-stroke-alignment-v1'
$env:DOTNET_CLI_HOME="$state\dotnet"
$env:NUGET_PACKAGES="$state\nuget"
$env:TEMP="$state\tmp"
$env:TMP=$env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
dotnet build ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment/AxisStrokeAlignment.csproj --configuration Release
& '.\tools\gpu-training\Run-GpuPython.ps1' -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment/run_axis_alignment.py','E:\Graph-auto-reader','E:\Graph-auto-reader\ml\ocr\stroke_seeded_xtick_v1\recognition_diagnostic\axis_stroke_alignment\bin\Release\net10.0-windows\AxisStrokeAlignment.dll','E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-axis-stroke-alignment-v4\report.json')
$env:AXIS_STROKE_ALIGNMENT_REPORT='E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-axis-stroke-alignment-v4\report.json'
& '.\tools\gpu-training\Run-GpuPython.ps1' -PythonArguments @('-m','unittest','discover','-s','ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment','-p','test_alignment.py','-v')
& '.\tools\gpu-training\Run-GpuPython.ps1' -PythonArguments @('-m','unittest','discover','-s','ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment','-p','test_report.py','-v')
```

The machine-readable report records the exact per-source raster hashes, per-panel results, and matched-pair evidence.

## Source identities

| Source | SHA-256 |
|---|---|
| Pinned consumed probe manifest | `127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba` |
| Probe renderer/materializer | `98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d` |
| Diagnostic C# runner | `c51b792097f2016d8ccf60098865d352fa957b35ec121ceece099f944aafe313` |
| Diagnostic Python runner | `b64c353ce919143e83e4bea380f7f36b11175a51547b30a137d42f2402efb3de` |
| Production raster decoder | `16050d8672bc75f0ed742a102b85334c11a387ad4b894a434bc77126c3bf8c89` |
| Production axis detector | `8c5988200bf6a9ee45834cec05db5f6df5f8af8f6355b3a11eaf5bdb5413aed2` |
| Production OpenCV line provider | `72a1341bbbdf11b8ce195a93422131bde5bdcf814027d06b1fbe97798428dd88` |
| Axis geometry contracts | `45a84dcdb52b12926a05cffe8d3283dd7ef41752fcad05ad42e5017f80cb740f` |
| Production XAxisTickStrokes mapping | `5ab000a82b7c3352bf7034cec2863ccfa725a7074fd36e9594e13212be6eac36` |
| Final v4 report | `7a9aa9b5dc6b73de11eac01f856984b55ed537d4614250836022c73973c77de2` |
