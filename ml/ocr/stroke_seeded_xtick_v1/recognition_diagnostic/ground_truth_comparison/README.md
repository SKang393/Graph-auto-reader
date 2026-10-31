# V5 on ground-truth and predicted boxes

This paired diagnostic uses the same 165 positive cases from the consumed
synthetic stroke probe. It compares ground-truth target boxes with frozen P2
top-one boxes, using the exact V5 component recognizer and production cropper.
No model was trained or changed.

## Result

Report: `artifacts/stroke-seeded-xtick-v1-p2-v5-ground-truth-comparison-v2/report.json`.

At detector score `0.25`, ground-truth boxes produced 32 exact readings, 32
wrong readings, 101 empty outputs, and zero explicit failures. Predicted boxes
produced 23 exact, 30 wrong, 112 empty outputs, and zero explicit failures. Of
the 133 predicted boxes with IoU >= `0.5`, V5 returned 21 exact, 29 wrong, 83
empty outputs, and zero explicit failures. The 32 below-threshold-IoU boxes
returned 2 exact, 1 wrong, 29 empty, and zero explicit failures.

Failure code/severity and returned text are explicit. An empty without an
alternative or failure is counted as empty. The adapter does not expose
per-crop component counts, and per-crop timing is divided from batch execution;
therefore these data cannot distinguish a crop with no encoded glyphs from a
post-inference reject-class, confidence-threshold, or numeric-grammar outcome.
All 21 submitted batches executed through the captured provider. No explicit
recognizer failure codes occurred.

The ground-truth results still contain 101 empty outputs. Thus,
P2 localization error does not explain most empty outputs. This small, consumed
synthetic diagnostic is not acceptance evidence and does not imply V5 approval.

## Reproduction

From the project root, build with project-local .NET/NuGet state, then run:

```powershell
$state='E:\Graph-auto-reader\tools\gpu-training\.state\recognition-diagnostic'
$env:DOTNET_CLI_HOME="$state\dotnet"
$env:NUGET_PACKAGES="$state\nuget"
$env:TEMP="$state\tmp"
$env:TMP="$state\tmp"
dotnet build ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ground_truth_comparison/GroundTruthComparison.csproj --configuration Release
dotnet run --no-build --configuration Release --project ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ground_truth_comparison/GroundTruthComparison.csproj -- E:\Graph-auto-reader artifacts\stroke-seeded-xtick-v1-p2-v5-recognition-probe-v1\predictions.json artifacts\stroke-seeded-xtick-v1-p2-v5-ground-truth-comparison-v2\report.json
```

The frozen detector boxes were generated separately by the project GPU Python
launcher on the RX 6600 XT. V5 recognizer inference used `CPUExecutionProvider`
because DirectML crashed in native ONNX Runtime during the earlier diagnostic.
