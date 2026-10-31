# V5 component rejection detail

This non-release diagnostic recomputes the V5 adapter's private crop encoder on
the same 165 positive rows already used by the consumed P2/V5 synthetic probe.
It invokes `FilterForeground`, `ActiveIntervals`, and `EncodeGlyph` from the
production assembly, then asserts per crop that the tall-component predicate
or over-maximum-glyph predicate reproduces `EncodeCrop.Rejected`. It also maps
each annotation's recorded renderer stroke segment through its pinned affine
transform and checks geometric intersection with the padded predicted crop.
That intersection is geometric evidence only, not proof that a plot line caused
the rejection.

## Inputs

- Probe manifest SHA-256: `127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba`
- Probe predictions SHA-256: `235d39476cf3ef498d0c026f05e4c9eb626fe43c853d0af49ab6d0bef2d54097`
- V5 ONNX SHA-256: `9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84`

## Commands

Run from `E:\Graph-auto-reader`, with .NET/NuGet/temp state project-local:

```powershell
$state='E:\Graph-auto-reader\tools\gpu-training\.state\recognition-diagnostic'
$env:DOTNET_CLI_HOME="$state\dotnet"
$env:NUGET_PACKAGES="$state\nuget"
$env:TEMP="$state\tmp"
$env:TMP="$state\tmp"
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
dotnet build ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/component_rejection_detail/ComponentRejectionDetail.csproj --configuration Release
dotnet run --no-build --configuration Release --project ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/component_rejection_detail/ComponentRejectionDetail.csproj -- 'E:\Graph-auto-reader' 'E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-p2-v5-recognition-probe-v1\predictions.json' 'E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-p2-v5-component-rejection-detail-v1\report.json'
& '.\tools\gpu-training\Run-GpuPython.ps1' -PythonArguments @('-m','unittest','discover','-s','ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/component_rejection_detail','-p','test_report.py','-v')
```

The generated report is aggregate-only. The 82 adapter rejections partition as
82 tall-only, 0 glyph-count-only, and 0 with both predicates; there are 0 parity
mismatches. Across all 165 crops, 156 recorded renderer-stroke segments
geometrically intersect padded extraction; none intersect the annotated target
glyph box. Among the 82 rejected crops, 74 intersect padded extraction. This
supports crop contamination as a plausible contributor, while the 8 tall-only
rejections without that geometric intersection show that it is not a complete
explanation. The adapter threshold is component height ratio `>= 0.75`; its
maximum glyph count is 8. Rejected crops have median observed component height
ratio 0.8125.

This is a train/probe synthetic diagnostic already consumed for model
development. It is not held-out acceptance, uses no training or ONNX inference,
and does not change production behavior.
