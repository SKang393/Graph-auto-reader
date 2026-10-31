# V5 component rejection attribution

This diagnostic uses the same 165 threshold-selected positive crops, frozen P2
boxes, V5 model, and production cropper as the prior consumed-probe comparison.
It invokes the exact private `LocalOnnxComponentTextRecognizer.EncodeCrop`
method through a reflection-generated delegate, captures the real adapter ONNX
logits through an in-memory inference cache, then follows the production
reject/confidence/grammar decision order. The diagnostic does not change
production code, thresholds, or model behavior.

## Result

Report: `artifacts/stroke-seeded-xtick-v1-p2-v5-component-rejection-v1/report.json`.

Across 165 predicted crops, production adapter outcomes partitioned as:

- 82 component geometry or glyph-count rejections. Production combines tall
  component and over-maximum-glyph causes in one `Rejected` flag.
- 12 model reject-class decisions.
- 11 confidence-below-threshold decisions at `0.65`.
- 7 numeric-grammar rejections.
- 23 exact recognized strings and 30 wrong recognized strings.
- 0 explicit failure codes.

The four rejection categories total the 112 empty outputs from the paired
comparison. Captured logits and the exact private preprocessing signature
produced zero decision mismatches against the actual adapter's 165 results.
The inference provider was `CPUExecutionProvider`; it processed one packed
batch containing 146 encoded glyph features. This is a consumed synthetic
diagnostic, not acceptance evidence or model approval.

The model adapter does not expose individual height-versus-count rejection
causes or the reject-class/confidence probability values. The result reports
the first applicable production decision branch only. No case-level text or
logits are retained in the report.

## Reproduction

From the project root, build with project-local .NET/NuGet state, then run:

```powershell
$state='E:\Graph-auto-reader\tools\gpu-training\.state\recognition-diagnostic'
$env:DOTNET_CLI_HOME="$state\dotnet"
$env:NUGET_PACKAGES="$state\nuget"
$env:TEMP="$state\tmp"
$env:TMP="$state\tmp"
dotnet build ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/component_rejection_diagnostic/ComponentRejectionDiagnostic.csproj --configuration Release
dotnet run --no-build --configuration Release --project ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/component_rejection_diagnostic/ComponentRejectionDiagnostic.csproj -- E:\Graph-auto-reader artifacts\stroke-seeded-xtick-v1-p2-v5-recognition-probe-v1\predictions.json artifacts\stroke-seeded-xtick-v1-p2-v5-component-rejection-v1\report.json
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('-m','unittest','discover','-s','ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/component_rejection_diagnostic','-p','test_report.py','-v')
```
