# Relaxed-width recognizer confirmation

## Problem

Check whether the frozen P3/P5 stroke-validity proposals can be confirmed by the
existing numeric recognizer, with the official PP-OCRv5 path recorded as a
separate comparator. This is a consumed synthetic development diagnostic, not
acceptance or production activation.

## Implementation

The P3/P5 threshold remained `0.5`. The numeric rule was fixed from the
synthetic-only V5 selection report: confidence at least `0.65` and grammar
`^-?\d+(?:\.\d+)?%?$`. The report states the sealed public archive was not
opened. The official comparator used the documented V8 `OfficialMinimumConfidence`
of `0.0` and the same graph-number grammar, without adding a confidence gate.

Each selected proposal was recognized from its original proposal-centered
`256x96` crop using the production `OcrCropBatcher` and both exact recognizer
paths. This crop is not a text-region box, so the result measures compatibility
with the available seed crop, not the models' best text-box performance.

## Files changed

- `protocol.json`
- `evaluate_confirmation.py`
- `Confirmation.csproj`
- `Program.cs`
- `test_evaluate_confirmation.py`
- This result record

Generated score images, row scores, recognitions, failure receipt, and metrics
are under ignored `artifacts/stroke-seeded-xtick-v1-relaxed-width-numeric-confirmation-v1/`.

## Tests and commands

- Python compile and `pyflakes`: passed.
- Four focused Python tests: passed.
- C# project cropper self-test: passed, including `128x32` numeric and `320x48`
  official crops plus preserved source metadata.
- Truth-blind P3/P5 score pass: 433 eligible rows on RX 6600 XT, 9 overlap rows
  quarantined, 116 union-selected rows, 100 unique crop hashes.
- C# recognizer run: both readers completed on CPU for 116 proposal rows and
  100 unique crops.
- The first report assembly failed after recognition with `KeyError:
  'proposal_id'` because default `System.Text.Json` record properties were
  PascalCase. The failure receipt was preserved. A parser-only correction was
  tested and the aggregate report completed without rerunning inference.

```powershell
& .\tools\gpu-training\venv\Scripts\python.exe -m unittest ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.relaxed_width_numeric_confirmation_v1.test_evaluate_confirmation
$priorityProcess = Get-Process -Id $PID
$originalPriority = $priorityProcess.PriorityClass
try {
    $priorityProcess.PriorityClass = 'BelowNormal'
    & .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/relaxed_width_numeric_confirmation_v1/evaluate_confirmation.py', '--score')
} finally { $priorityProcess.PriorityClass = $originalPriority }
dotnet run --project ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/relaxed_width_numeric_confirmation_v1/Confirmation.csproj -- --self-test
dotnet run --project ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/relaxed_width_numeric_confirmation_v1/Confirmation.csproj --no-restore -- E:\Graph-auto-reader E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-relaxed-width-numeric-confirmation-v1\score_rows.json E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-relaxed-width-numeric-confirmation-v1\recognition.json
& .\tools\gpu-training\venv\Scripts\python.exe ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/relaxed_width_numeric_confirmation_v1/evaluate_confirmation.py --finalize
```

## Metrics and timing

The visible-tick denominator is `181`. Duplicated proposal rows were retained;
crop recognition was memoized by exact image hash.

| Stage | P3 | P5 |
|---|---:|---:|
| Classifier-selected proposals | 99 | 105 |
| Classifier matches / false proposals | 78 / 21 | 82 / 23 |
| Numeric V5 confirmations | 0 | 0 |
| Official grammar confirmations | 38 | 40 |
| Official confirmations matched / unmatched | 30 / 8 | 32 / 8 |
| Official precision / recall | 0.7895 / 0.1657 | 0.8000 / 0.1768 |
| Official false proposals on no-visible panels | 0 | 0 |

Among classifier-matched proposals before confirmation, official recognition
was exact/wrong/empty `27/47/4` for P3 and `29/49/4` for P5. Among grammar
confirmed official proposals, it was exact/wrong `27/3` and `29/3`. Numeric V5
returned no text and no runtime failures on any P3/P5 selected proposal crop.
That is an observed incompatibility with this whole-seed crop, not evidence that
V5 fails on correctly localized text boxes.

P3/P5 scoring used AMD Radeon RX 6600 XT, `cuda:0`, HIP `7.16.26385`, with CPU
fallback disabled. The recognition pass used `CPUExecutionProvider` per the
existing V5/official runtime policy. No optimizer ran. Wall-clock durations
were not persisted by the score and recognition runners.

## License review

No new dependency, model payload, or copied source was introduced. The
diagnostic uses the existing V5 model and the existing locally audited
PP-OCRv5 and ambiguity payloads by exact hash. This work does not create a
redistribution package or production model-store entry.

## Known limitations

- Proposal crops are centered on candidate strokes, not text boxes. Numeric V5
  emitted zero text in this crop geometry, so numeric-confirmation recall was
  zero. Do not interpret this as a test of V5 on correctly localized label text.
- Official reader results remain from an already-consumed procedural probe and
  cannot establish real-graph or untouched acceptance performance.
- Only aggregate matched/false counts are used for comparison; the report is
  not an OCR production gate.
- The score receipt binds the execution-time source digest, but that exact
  pre-correction Python source snapshot was not retained. The C# recognition
  output pins its runner/source and assembly hashes.

## Integration notes

Keep this isolated from production. No threshold was tuned on the probe and no
model was promoted. The stronger official comparator only supports building a
future text-region crop contract from training evidence before evaluating a
fresh diagnostic.

## Acceptance status

Exploratory result only. Numeric V5 did not produce a usable confirmation from
the current whole-seed crops. No production activation or acceptance claim.

## Bound hashes

| Artifact | SHA-256 |
|---|---|
| Paired P3/P5 score rows | `30be7b889ac536cabdaba0b7155b590c08c6d5d2f0505cf1c34a7496725f21fe` |
| Recognizer outputs | `2698b2aa848d35c246c09244bcda5f0f74bf1b5c29862c10ece3569a70c7c70c` |
| Aggregate result | `58ccb0a1aac710913c5e9451a4e60fb9080f0a2f5ab51a0e11e3c5f36ed48e38` |
| Preserved assembly-failure receipt | `2da3f50e5a1463e0e743ebf4ec19a4e1d9fdd765cdc3a3be17a8364622f0f403` |
| Score config | `a67c4e566da28e862e833d5c87b6cb095345714932baf9a8715334e9e34c93fe` |
| Protocol | `1c57440cd5cee7ca3afff0f732b377cad1bd56eaa6d12b9892d4070ac716762a` |
| Synthetic V5 training selection report | `e5095b63221b7a745e48ead61011a7794e5da54e0fc24fd641d785bc09257260` |
| V5 numeric model | `9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84` |
| Official recognizer | `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` |
| Official alphabet | `27e91d0582f40168aa218303c76e184bc78fa7a5d105aad0cfbad8458b441067` |
| Ambiguity model | `b8e2773ca3966469081875fc36b3981ef4eb458356d8dfdae2be2722602f0096` |
| Relaxed-width paired result record | `2eff12a2ed5d0c8cfb64944adf99c19ddc04c117814a7376987d5be0ff3642e8` |

## Required source hashes

| Source | SHA-256 |
|---|---|
| Python runner at score execution (receipt value) | `c0a4eebb681f4065b53dbd1d22766b144d42b3bfc6cd7cfe3b2a445fd1fc38ac` |
| Current Python runner after parser correction | `b6430a8fa8ea9fb27a99543ef82f46f2e7350a3c391aa6660df1733b2d6be0e0` |
| C# program | `8d878df2df6f9e70b6d6922a0ba4f21c5d9b19d6a3160e75fc591b7197ba8535` |
| C# project | `d69f515b72e669328a9f7b0de4593e9ae3d423f7e008de4863dd0a74525a7adb` |
| Python test | `883f21ff5ed8532db7e53edeaf8e37e211ed8defb52557b712e75448a0b9bcf7` |
| `LocalOnnxComponentTextRecognizer.cs` | `2d5678fcdb1aa2025a9a0fd5ec131b94cf81c7327bb37dead8eddc20ed442b6b` |
| `OcrV8OfficialTextRecognizer.cs` | `a46c7e11de18a98ccbf371eb5fe418420b89b06553b21335357814c980bb3104` |
| `OcrCropBatcher.cs` | `25a2d4f701d6cea04dc82651f1129efed8d650c53e36a13ef8763444f6e542ef` |
| GraphReader.Ocr assembly | `0b6ac8f36e84dc7e828082cebd324867e5b9ac7057d3649e4fc54ea65e05e067` |
| GraphReader.Inference assembly | `eec5b74b56a7ea67b77c31c125131569a4a92fc192b2d9d9807f58f7715e0809` |
| Diagnostic runner assembly | `5de36dd1533af515fd8990ef6f621df1f61cec6ebaf978186fabef9832bf8d8a` |
