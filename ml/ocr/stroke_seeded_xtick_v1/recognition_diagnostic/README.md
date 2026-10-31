# P2 top-one boxes with the V5 numeric recognizer

This is an isolated diagnostic of the already-consumed synthetic stroke probe.
It connects the frozen P2 top-one predicted box to the project `OcrCropBatcher`
and the existing `LocalOnnxComponentTextRecognizer`. It does not train or
promote either model.

## Frozen inputs

- Detector checkpoint: `artifacts/stroke-seeded-xtick-v1-p2-hard-negative-fit-v1/checkpoint.pt`, SHA-256 `9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351`.
- Consumed probe manifest: `artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3/manifest.json`, SHA-256 `127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba`.
- V5 recognizer: `ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx`, SHA-256 `9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84`.
- The V5 model remains unapproved and has no production manifest/store entry.

The detector stage uses the project GPU Python launcher and checks the RX 6600
XT binding. The recognizer stage calls the production OCR cropper and adapter.
DirectML crashed in native ONNX Runtime during this probe; the final recorded
run pins CPU inference and records 11 CPU provider batches.

## Crop and scoring contract

Each predicted normalized box is mapped to the source crop's 256x96 original
pixel frame. `OcrCropBatcher` receives an identity `OcrFrameTransform` and the
predicted original-pixel polygon, then applies the production numeric crop
settings: 128x32 grayscale, aspect-preserving padding, 12px horizontal padding,
1px vertical padding, 0.25 vertical content margin, and white padding. The
runner checks the cropper-retained polygon and output dimensions.

Detection matches require IoU >= 0.5 against the hashed annotation target. A
positive localization miss counts as both a detection false positive (for the
wrong selected box) and a positive false negative. Background false positives
are counted separately. Exact text is compared only for matched detections;
wrong text and review/withheld outcomes have separate counts.

## Result

See the ignored, project-local `artifacts/stroke-seeded-xtick-v1-p2-v5-recognition-probe-v1/recognition_report_v3.json`.
At score 0.25: 133/165 matched detections, 32 false positives, 32 misses, zero
background-only false positives. Of 133 matched crops, V5 returned exact text
for 21, wrong text for 29, and withheld/reviewed 83; exact accuracy among the
50 non-withheld outputs was 0.42. At score 0.5, matched detections and
recognition outcomes were unchanged; false positives fell to 30 and misses
remained 32. V5 emitted text on 53 of 165 selected crops overall; 112 were
withheld, and there were no runtime errors.

The probe is consumed exploratory synthetic data, not untouched acceptance.
These results do not establish performance on real graphs or on cases excluded
as ambiguous. They show that localization alone does not solve OCR: the
recognizer withheld many predicted crops and made substantial numeric errors.

## Reproduction

From `E:\Graph-auto-reader`:

```powershell
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/recognition_gpu_export.py')
$state='E:\Graph-auto-reader\tools\gpu-training\.state\recognition-diagnostic'
$env:DOTNET_CLI_HOME="$state\dotnet"
$env:NUGET_PACKAGES="$state\nuget"
$env:TEMP="$state\tmp"
$env:TMP="$state\tmp"
dotnet build ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/StrokeSeededRecognitionDiagnostic.csproj --configuration Release
dotnet run --no-build --configuration Release --project ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/StrokeSeededRecognitionDiagnostic.csproj -- E:\Graph-auto-reader artifacts\stroke-seeded-xtick-v1-p2-v5-recognition-probe-v1\predictions.json artifacts\stroke-seeded-xtick-v1-p2-v5-recognition-probe-v1\recognition_report_v3.json
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('-m','unittest','discover','-s','ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic','-p','test_report.py','-v')
```
