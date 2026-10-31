# V5 vertical crop margin sweep

This diagnostic changes only `OcrCropBatcherOptions.VerticalContentPaddingRatio`
for the production V5 recognizer crop path. It evaluates `0.25` (production
baseline), `0.50`, and `0.75` on the same consumed P2 synthetic probe, with the
same frozen P2 detector boxes, V5 ONNX model, crop dimensions, all other crop
settings, and recognition thresholds.

For each margin it recognizes 165 positive ground-truth boxes, the 165 positive
predicted boxes admitted at detector score `>= 0.25`, and all 158 background-only
top-one boxes. Backgrounds are intentionally forced through OCR even though no
background prediction reaches `0.25`, to measure recognizer false text
propensity separately from detector-admitted background false positives. All
rows are scored without deduplicating; background records contain 107 unique
image hashes and 51 repeated rows, with no identical-image conflicting text
labels.

Structural tall rejection is measured by invoking the exact adapter private
`EncodeCrop`, `FilterForeground`, `ActiveIntervals`, and `EncodeGlyph` methods
on the cropper output and asserting the component predicates reproduce the
adapter flag. Recognition results come from the real
`LocalOnnxComponentTextRecognizer`. Both positive-box `0.25` outcome sets
match the existing report's 165-row baseline exactly.

## Results

| Margin | GT boxes exact / wrong / empty | GT tall reject | Predicted exact / wrong / empty | Predicted tall reject | Background text outputs |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 0.25 | 32 / 32 / 101 | 68 / 165 | 23 / 30 / 112 | 82 / 165 | 0 / 158 |
| 0.50 | 14 / 17 / 134 | 89 / 165 | 13 / 21 / 131 | 61 / 165 | 0 / 158 |
| 0.75 | 6 / 5 / 154 | 3 / 165 | 4 / 5 / 156 | 22 / 165 | 0 / 158 |

For all three margins, all 158 background forced crops return no alternative and
all are structurally rejected as too tall; none of their detector scores reach
0.25. Therefore they produce no OCR text here, while the detector already
admits zero background crops at the primary score threshold. The largest margin
substantially reduces tall rejects for positives but also reduces returned text
and exact recognition. At margin `0.50`, predicted-box tall rejects decrease
from 82 to 61 while exact outputs fall from 23 to 13. These synthetic results
do not support changing the production margin and do not establish acceptance.

## Pinned identities

- Prediction rows SHA-256: `235d39476cf3ef498d0c026f05e4c9eb626fe43c853d0af49ab6d0bef2d54097`
- Existing baseline report SHA-256: `8bb9ee29ab4ac0ee0f09572fabd367624d82cc11d5a33d42a044752d56bd018e`
- Probe manifest SHA-256: `127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba`
- P2 detector checkpoint SHA-256: `9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351`
- V5 recognizer ONNX SHA-256: `9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84`
- V5 protocol SHA-256: `3c4a169eb1b51e6895152c00e0ef2ff664e8154abc911a21cbcba17e0346adfa`
- Provider: `CPUExecutionProvider`; inference only, with 73 actual provider execution calls across the run.

## Reproduction

Run from `E:\Graph-auto-reader`; .NET, NuGet, and temporary state stay inside
the project. The Python report checks use the project's GPU environment but do
not train or require GPU inference.

```powershell
$state='E:\Graph-auto-reader\tools\gpu-training\.state\recognition-diagnostic'
$env:DOTNET_CLI_HOME="$state\dotnet"
$env:NUGET_PACKAGES="$state\nuget"
$env:TEMP="$state\tmp"
$env:TMP="$state\tmp"
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
dotnet build ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/margin_sweep/MarginSweep.csproj --configuration Release
dotnet run --no-build --configuration Release --project ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/margin_sweep/MarginSweep.csproj -- 'E:\Graph-auto-reader' 'E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-p2-v5-recognition-probe-v1\predictions.json' 'E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-p2-v5-ground-truth-comparison-v2\report.json' 'E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-p2-v5-vertical-margin-sweep-v1\report.json'
& '.\tools\gpu-training\Run-GpuPython.ps1' -PythonArguments @('-m','unittest','discover','-s','ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/margin_sweep','-p','test_report.py','-v')
```

No training was performed. No dev, sealed, or private data was read. Production
defaults and model files were not changed. This is not a held-out acceptance
result.
