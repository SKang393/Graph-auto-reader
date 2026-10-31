# Geometry-only synthetic tick-centerline erasure

This diagnostic uses the consumed P2 synthetic probe and the frozen V5
recognizer. It erases a one-pixel Bresenham rasterization of the annotated tick
stroke centerline after mapping it through `crop_transform.original_to_crop_pixel`.
Pixels are set to the annotation's neutral outside-panel value, 255. The erase
mask depends only on the source image copy, centerline, affine transform, and
neutral fill value. It does not use text, target boxes, glyph masks, or OCR
results to choose pixels. Original PNGs are not modified.

The annotation does not record rendered stroke width or antialiasing. The
diagnostic therefore tests only centerline removal, not removal of a full-width
rendered tick mark. It should not be used to conclude that a wider stroke has
no effect.

## Mask reconstruction and safety

The probe annotations store glyph-mask SHA-256, nonzero-pixel count, bounds,
and crop transforms, but not mask bytes. The diagnostic regenerates masks from
the exact project-local renderer/materializer and the source recipes already
pinned in the consumed manifest. It verifies all 359 materialized probe image
hashes and each of the 165 positive full-panel mask hashes and pixel counts.
The resulting mask images are used only after the original and erased crops
have both passed through the recognizer. Actual reconstructed crop-mask pixels
are compared to the erasure raster for safety. Maximum crop-mask edge delta
from the annotated target mapping is 1 pixel.

## Results

| Crop role | Baseline exact / wrong / empty | Centerline erased exact / wrong / empty | Tall rejects before / after |
| --- | ---: | ---: | ---: |
| Ground-truth boxes, 165 | 32 / 32 / 101 | 67 / 22 / 76 | 68 / 47 |
| Predicted boxes, 165 | 23 / 30 / 112 | 37 / 30 / 98 | 82 / 72 |
| Forced background proposals, 158 | 0 / 0 / 158 | 0 / 0 / 158 | 158 / 158 |

At the positive boxes, centerline removal gained 35 exact GT-box readings and
14 exact predicted-box readings, with zero exact readings lost. It reduced tall
component rejects by 21 GT boxes and 10 predicted boxes. No background crop
returned text before or after; none of the 158 background top-one detector
scores reached 0.25.

Actual glyph-mask overlap was zero: none of the 165 erased centerlines touched
any reconstructed glyph pixel, among 90,889 nonzero crop-mask pixels. All 165
positive cases therefore fall in the no-overlap stratum; the overlap stratum
has zero cases. This evidence supports a synthetic tick-centerline
contamination effect on this consumed probe, but does not establish that
removing the full rendered stroke is safe or beneficial on other figures.

The 0.25 baseline exactly matches the prior recognition report: GT boxes
32/32/101 and predicted boxes 23/30/112. The report records 47 actual
`CPUExecutionProvider` executions and approximately 1.60 seconds in paired
recognizer batch processing. No training or production changes were made.

## Pinned inputs

- Predictions SHA-256: `235d39476cf3ef498d0c026f05e4c9eb626fe43c853d0af49ab6d0bef2d54097`
- Baseline report SHA-256: `8bb9ee29ab4ac0ee0f09572fabd367624d82cc11d5a33d42a044752d56bd018e`
- Probe manifest SHA-256: `127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba`
- P2 detector checkpoint SHA-256: `9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351`
- V5 recognizer ONNX SHA-256: `9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84`
- V5 protocol SHA-256: `3c4a169eb1b51e6895152c00e0ef2ff664e8154abc911a21cbcba17e0346adfa`

## Reproduction

Run from `E:\Graph-auto-reader`. Temporary masks and .NET state stay inside the
project. The GPU launcher supplies the project's Python environment; the
recognizer is explicitly pinned to CPU inference, and no training is run.

```powershell
& '.\tools\gpu-training\Run-GpuPython.ps1' -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/geometry_only_erase/extract_glyph_masks.py','E:\Graph-auto-reader','E:\Graph-auto-reader\tools\gpu-training\.state\stroke-geometry-erase-v1\glyph-mask-reconstruction-final')
$state='E:\Graph-auto-reader\tools\gpu-training\.state\recognition-diagnostic'
$env:DOTNET_CLI_HOME="$state\dotnet"
$env:NUGET_PACKAGES="$state\nuget"
$env:TEMP="$state\tmp"
$env:TMP="$state\tmp"
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
dotnet build ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/geometry_only_erase/GeometryOnlyErase.csproj --configuration Release
dotnet run --no-build --configuration Release --project ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/geometry_only_erase/GeometryOnlyErase.csproj -- 'E:\Graph-auto-reader' 'E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-p2-v5-recognition-probe-v1\predictions.json' 'E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-p2-v5-ground-truth-comparison-v2\report.json' 'E:\Graph-auto-reader\tools\gpu-training\.state\stroke-geometry-erase-v1\glyph-mask-reconstruction-final' 'E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-p2-v5-geometry-only-erase-v2\report.json'
& '.\tools\gpu-training\Run-GpuPython.ps1' -PythonArguments @('-m','unittest','discover','-s','ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/geometry_only_erase','-p','test_report.py','-v')
```

This is a consumed synthetic development diagnostic, not held-out acceptance.
The earlier v1 report is retained; v2 adds a measured crop-mask-to-target-box
alignment check and records a separate output path.
