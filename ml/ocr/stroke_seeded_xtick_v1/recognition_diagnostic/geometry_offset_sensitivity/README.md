# Geometry offset sensitivity diagnostic

This is an inference-only perturbation experiment on the already-consumed synthetic P2 probe. It applies a one-pixel-wide, white Bresenham centerline along the annotated stroke geometry, with perpendicular offsets of 0 and ±1/±2 probe pixels. Every variant starts from the original image. The actual rendered glyph masks are consulted only after OCR, to quantify overlap; they never select erased pixels.

The result is not evidence of where a production detector would place a stroke, and it is not held-out acceptance. Stroke annotations do not preserve rendered width or antialiasing. The probe was used in prior development diagnostics. No training or production behavior change occurred.

## Results

The CPU ONNX provider processed 488 rows per variant: 165 ground-truth positive boxes, 165 predicted positive boxes, and all 158 forced background top-one boxes, without deduplication. The original pass reproduced the pinned prior report exactly. The 158 backgrounds remained empty for every variant.

| Input boxes | Original exact | Offset 0 exact | Offset -1 exact | Offset +1 exact | Offset -2 exact | Offset +2 exact |
|---|---:|---:|---:|---:|---:|---:|
| Ground truth | 32/165 | 67/165 | 45/165 | 61/165 | 32/165 | 41/165 |
| Detector top one | 23/165 | 37/165 | 27/165 | 44/165 | 23/165 | 32/165 |

Offset zero is the exact annotated centerline perturbation. It yielded one newly wrong output for ground-truth boxes and two for predicted boxes. Offsets -1, +1, +2 yielded respectively 0, 4, and 1 newly wrong predicted-box outputs. No exact output became wrong in the ground-truth or predicted roles. These changes demonstrate sensitivity to the chosen geometric line; they do not demonstrate actual stroke removal.

The five erasure variants removed zero pixels from the reconstructed glyph masks: 90,889 nonzero target-mask pixels across 165 cases, with no mask intersections for any tested offset. The masks' target bounds matched annotation target bounds to at most one pixel. Background crops returned no text across the 158-row set. Detector threshold was 0.25; cropper vertical margin was 0.25. ONNX provider was `Cpu` (142 recognizer calls); elapsed inference was 4,896.3889 ms.

## Reproduction

From the repository root, with project-local .NET state configured as in the project GPU training launcher environment:

```powershell
dotnet build ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/geometry_offset_sensitivity/GeometryOffsetSensitivity.csproj --configuration Release
dotnet run --no-build --configuration Release --project ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/geometry_offset_sensitivity/GeometryOffsetSensitivity.csproj -- 'E:\Graph-auto-reader' 'E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-p2-v5-recognition-probe-v1\predictions.json' 'E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-p2-v5-ground-truth-comparison-v2\report.json' 'E:\Graph-auto-reader\tools\gpu-training\.state\stroke-geometry-offset-v1\glyph-mask-reconstruction' 'E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-p2-v5-geometry-offset-sensitivity-v1\report.json'
```

Test the emitted report:

```powershell
$env:STROKE_OFFSET_REPORT='E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-p2-v5-geometry-offset-sensitivity-v1\report.json'
& '.\tools\gpu-training\Run-GpuPython.ps1' -PythonArguments @('-m','unittest','discover','-s','ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/geometry_offset_sensitivity','-p','test_report.py','-v')
```

The test reads only the report, not protected or unrelated datasets. Report identity and source hashes are recorded in `report.json`.
