<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR training fixture marker coverage

The [bounded development inspection](GOAL-22-CURRENT-SYNTHETIC-RUN-20261030.md)
found nontext marker and stroke detections. The historical V44/V46 training
annotations contain 500 plotted markers (215 circles, 285 squares) and 30
legend glyphs (15 circles, 15 squares), with no other marker shapes. Their
20-source identities agree across preflight, 28-panel capture, and V46's
112 original/scale windows. Repeated calls selected the first four smoke cases
and restarted the style catalog, so additional seeds did not add other shapes.

This is a coverage defect, not proof of the model's failure mechanism. The
separate rejected 64-scene topology-head experiment used filled circles only;
its recipe was not repeated. Nontext pixels already had background targets,
so no label-mask bug or new loss function is claimed.

## Implemented fixture

The generator now accepts an optional style offset, defaulting to the previous
zero value. The new entry point schedules all 27 existing shape/fill styles
across nine train-only sources with seed `20261112`:

```powershell
python -m ml.ocr.official_bakeoff.train_marker_style_coverage --output artifacts/ocr-marker-style-train-v1
```

It writes an annotation-free runtime raster manifest, generator references,
and separate source-pixel OCR truth. The shared truth-row reader validates
source, text, region and truth identities plus original-pixel bounds. The
historical 20-source/709-truth population checks remain unchanged; they do not
accept this supplemental population. Review caught missing row identities in
the first implementation, which were fixed before integration.

The generated fixture checks 132 eligible symbol centers. Tests additionally
check actual rendered diamond, star, asterisk and cross ink away from text:
target zero, valid supervision mask. No runtime algorithm, threshold, model,
dependency or licensed asset changed. Optimizer steps, inference runs, and
private/sealed reads were all zero for that generator-verification batch; its
sealed-run budget was zero. The later CPU capture below runs fixed-model
inference on these train sources only.

## Verification and next consumer

`python -B -m pytest ml/ocr/official_bakeoff/tests/test_train_marker_style_coverage.py ml/ocr/official_bakeoff/tests/test_text_extent_head_inputs.py ml/synthetic/tests/test_dataset.py -q -p no:cacheprovider --basetemp artifacts/marker-style-consumer-tests-20261111`
passed 32 tests in 42.51 seconds. After strengthening identifier rejection
assertions, the eight affected cases passed in 5.39 seconds. Diff checks passed.
The nine-source test fixture has input-manifest SHA-256
`1ee37ea0378058d15dc16e65b757d7b1f95279e60e44db3813fd341eaf3f8a14`
and corrected source-truth SHA-256
`0ab8d513e8ddf4ff65775092bb2e05f36b5addd74970b0601b0f7d497c927f2b`.

This fixture alone is source-pixel preparation, not an accuracy improvement.
The new nine-panel capture consumer and its completed CPU execution are
described below.

Historical preflight/truth/tensor/scale-manifest hashes are respectively
`23afd6d3163c84998586b54e72ef28d46cf80c561ff2a63449a005b48188cc53`,
`60a474a7e417c5f4b3622a7920da18d70d2b6c8677a644f13137d1c08662e1d3`,
`15e48b27ce3b8f5f53311abbef7235c35b3e8e361c8baddb09a4d3a7e7d5938e`,
and `5748dcf05dca6083948d0054382c5e3c369f52db007c6ff2a3a5c86ae0ef90b5`.
Their existing `resume-20260908` inputs remain needed for the capture/adaptation
consumer. Task-owned `artifacts/marker-style-*-tests-20261111` test outputs are
disposable after this record is pushed. Regenerate them with the commands above;
do not commit generated images, annotations, tensors or weights.

## Explicit capture and target consumer

`marker_style_head_inputs.py prepare` authenticates the fixture, actual runtime
report and candidate, then emits the nine-source training capture request.
The runtime tool's `--capture-marker-style-official-head-tensors` profile checks
the real PNG/Gray8/BGR images and stores actual detector-input tensors.
`marker_style_head_inputs.py targets` re-authenticates those outputs and uses
the recorded crop projection and tensor dimensions for inverse DB targets and
valid masks. Truth is not supplied to inference. Missing or clipped source
text is rejected rather than assigned invented geometry.

The historical six- and 37-panel profiles and 20-source/709-truth reader are
unchanged. The nine-profile uses its own exact candidate pin:
`5b1bb9b10915a1b590135cffd6d5bc89cc311c3d2c48e9dd374c3196ebf4943d`.
It is a path-only copy of candidate
`154d40615bd54f5744e20bb5b4482b86e05f0e3d709c8ac8d794e8d3371a4e38`:
native, two model, two manifest and two license paths now reference the seven
verified matching files inside the current primary E: checkout. All other
fields and payload hashes match. The old C: project was not inspected.

The new profile alone permits its exact canonical candidate detector-manifest
path outside the nested worktree, with candidate-path equality and the existing
manifest hash required. Historical profiles retain their repository-relative
path restrictions. The ignored candidate and path-only comparison record at
`artifacts/marker-style-capture-binding-20261111` are active capture inputs.
The comparison record hash is
`1221aab3b63242a0e4a4774b2a40c85b888d4327c959af7764e1e8bada6fa74d`.

The initial consumer/legacy-reader suite passed 34 tests in 27.07 seconds.
After the verified external-manifest path repair, the affected 21 Python tests
passed in 25.78 seconds; the serialized Release tool build had zero warnings
or errors in 7.58 seconds. Its profile selftest passed in 1.17 seconds without
loading a model, covering profile, count, candidate, source and manifest-path
rejections. Real candidate dependency/descriptor authentication also passed.
Independent review found no actionable correctness defect before the path
repair; focused regressions cover the repaired path boundary. The selftest is
included in Windows CI. No optimizer, actual capture, private/sealed read or
model approval occurred in this source batch.

After the fixture outcome reached `origin/main`, the three completed coverage,
ink and consumer-test directories were retired: 221 files, 17,430,904 bytes,
plus seven verified pytest links inside those directories. Capture-test outputs
remain disposable after their source/outcome is pushed and active checks end;
the candidate binding and historical source/model inputs remain active.

## Actual nine-source CPU capture and targets

The run completed at clean, pushed source
`6d9af5a56b741a89fc7d5683090d52fe1405d074` in
`E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-calibration-review-20261111`.
No matching capture process or prior output directory existed. The restricted
process query required approved host access; the subsequent query found no
duplicate work. No tracked source changed or tool rebuild occurred during
runtime inference, tensor capture, or target generation.

The fixture has nine train sources, 27 explicit styles and 224 source-pixel
text truths. Fixed-candidate CPU inference completed all nine sources and nine
panels with zero failures. The authenticated runtime crops retained all 224
truths fully, with zero outside, partial or overlapping truths. Model-free
production preprocessing then captured nine real `1x3x384x1024` input tensors.
The adapter wrote nine inverse DB target maps and nine valid supervision masks
on those actual grids. No default crop or transform was supplied.

The final file check rehashed and reshaped all 27 tensor/target/mask files and
checked finite values, binary targets and all-one masks. The target maps contain
40,103 positive pixels. All 132 eligible nontext centers across the 27 styles
remain target zero with mask one on the captured grids. Separate actual rendered
ink checks away from text passed for diamond (11), star (11), asterisk (11) and
cross (10). These are supervision/plumbing checks, not recognition accuracy.
Optimizer steps and dev/private/sealed reads were zero. No weights, thresholds,
production approval or release state changed.

### Commands and measured duration

Commands ran from that worktree with SDK `10.0.302`,
`DOTNET_CLI_HOME=E:\Graph-auto-reader\tools\dotnet-home`,
`NUGET_PACKAGES=E:\Graph-auto-reader\tools\nuget-packages`, and
`PYTHONDONTWRITEBYTECODE=1`. Paths below identify the actual run, not new
directories to overwrite:

```powershell
$taskBase = Join-Path (Get-Location) 'artifacts/marker-style-nine-source-20261111'
$taskCandidate = Join-Path (Get-Location) 'artifacts/marker-style-capture-binding-20261111/candidate.json'
$taskCandidateSha = '5b1bb9b10915a1b590135cffd6d5bc89cc311c3d2c48e9dd374c3196ebf4943d'
dotnet build tools/GraphReader.SyntheticRuntimeEvidence/GraphReader.SyntheticRuntimeEvidence.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false --verbosity minimal
```

The compiler output at
`tools/GraphReader.SyntheticRuntimeEvidence/bin/Release/net10.0-windows/win-x64`
was copied to `$taskBase/source-bin`; the capture source was copied to
`$taskBase/source/OfficialHeadTensorCapture.cs`. This active diagnostic snapshot
contains 51 binary/runtime files, 88,914,285 bytes, and is not an application
package. The executing snapshot and its source identity remain available to
the downstream consumer.

```powershell
$taskTool = Join-Path $taskBase 'source-bin/GraphReader.SyntheticRuntimeEvidence.dll'
python -B -m ml.ocr.official_bakeoff.train_marker_style_coverage --output (Join-Path $taskBase 'fixture') --seed 20261112
dotnet $taskTool (Join-Path $taskBase 'fixture/input-manifest.json') $taskCandidate $taskCandidateSha (Join-Path $taskBase 'runtime')
```

The request used the existing API's explicit capture-source override so future
tracked-source edits cannot invalidate this run's source binding. The inline
Python preparation called the same reader used by the `prepare` CLI:

```python
from hashlib import sha256
from pathlib import Path
from ml.ocr.official_bakeoff.marker_style_head_inputs import prepare_capture_request
from ml.synthetic.io import canonical_json_bytes
root = Path.cwd()
base = root / "artifacts/marker-style-nine-source-20261111"
digest = lambda path: sha256(path.read_bytes()).hexdigest()
fixture = base / "fixture/generator.json"
report = base / "runtime/report.json"
candidate = root / "artifacts/marker-style-capture-binding-20261111/candidate.json"
prepared = prepare_capture_request(
    fixture, digest(fixture), report, digest(report), candidate,
    "5b1bb9b10915a1b590135cffd6d5bc89cc311c3d2c48e9dd374c3196ebf4943d",
    repository_root=root, capture_binary_root=base / "source-bin",
    capture_source_path=base / "source/OfficialHeadTensorCapture.cs")
with (base / "request.json").open("xb") as stream:
    stream.write(canonical_json_bytes(prepared.request))
```

```powershell
$taskRequest = Join-Path $taskBase 'request.json'
$taskRequestSha = (Get-FileHash -LiteralPath $taskRequest -Algorithm SHA256).Hash.ToLowerInvariant()
dotnet $taskTool --capture-marker-style-official-head-tensors $taskRequest $taskRequestSha (Join-Path $taskBase 'capture')
$taskReport = Join-Path $taskBase 'capture/report.json'
$taskCaptureSha = (Get-FileHash -LiteralPath $taskReport -Algorithm SHA256).Hash.ToLowerInvariant()
python -B -m ml.ocr.official_bakeoff.marker_style_head_inputs targets --request $taskRequest --request-sha256 $taskRequestSha --report $taskReport --report-sha256 $taskCaptureSha --output (Join-Path $taskBase 'targets')
```

| Step | Measured seconds | Result |
| --- | ---: | --- |
| Serialized Release build | 52.949 | Zero warnings/errors; compiler-reported 52.35 s |
| Fixture command | 7.976 | Nine sources, 224 truths, 27 styles |
| Fixed-candidate CPU runtime | 15.371 | Nine sources/panels complete; runtime-reported 14.696 s |
| Authenticated preparation function | 1.003 | 224 full projected truths |
| Model-free tensor capture command | 1.623 | Nine captured panels |
| Model-grid target command | 7.062 | Nine targets and nine masks |
| Final saved-map verification function | 0.336 | All file/grid/background checks passed |

### Input and output identities

All paths in the next table are relative to
`artifacts/marker-style-nine-source-20261111`. The runtime/capture reports bind
actual source images, panel PNGs, Gray8/BGR identities, crop matrices and tensor
bytes. The snapshot manifest binds all 51 executing runtime files.

| File | SHA-256 |
| --- | --- |
| `source-snapshot.json` | `d5d90061666ebd367f71b6caab81f054f99ae3e9308c678b67ce96a1abfe1b16` |
| `fixture/generator.json` | `45a57c1993d30a265da5fe4e4347449102bf6e1b16a132408fdef3241c8040bd` |
| `fixture/input-manifest.json` | `1ee37ea0378058d15dc16e65b757d7b1f95279e60e44db3813fd341eaf3f8a14` |
| `fixture/train-text-truth.json` | `0ab8d513e8ddf4ff65775092bb2e05f36b5addd74970b0601b0f7d497c927f2b` |
| `request.json` | `3b844be84617b5b3cd362bf7be6ceadc4d9db81aa4449a2c526b8d8b3a93a049` |
| `runtime/report.json` | `3a3b401fa54a18ced67425d4044e66aaebb803292012f9c35670c914592ad41b` |
| `capture/report.json` | `dee957b49af517f5da3cd0fc358fe41205bddb73a7eb86e4e1ecd5dc6fce70b6` |
| `targets/manifest.json` | `d3c1417e5a210a9e23cf199973008642ec2d551d01b4313eb73a42e5775cdf48` |
| `aggregate-outcome.json` | `360cfa2edba872838dde9e8a252dcbae5c4fcf77ce1f54217b74e439ae808a08` |
| `source/OfficialHeadTensorCapture.cs` | `d040e55b289838e7b1d47da648ec2b678a6141dc6be861d6287278321914eaec` |
| `source-bin/GraphReader.SyntheticRuntimeEvidence.dll` | `3f7315ea27e0bb9f3aed646c3b9d9e6b6773b4e7f6573d08fe8632933b0e6fae` |
| `source-bin/GraphReader.App.dll` | `2b514958a8664c5a9529eaba8c9fba1939e2e595c7dba687719617560d8ee009` |
| `source-bin/GraphReader.Ocr.dll` | `8e39ab65de68148b9b22d6c2c56911c16ff12cc9ad67aaa9db92d7bc1d797c23` |
| `source-bin/GraphReader.Inference.dll` | `4de9431c264228909dbcd8eb8ee5e5b8dd9adac1f06625574674d4137f2de9b6` |

The exact relocated candidate and comparison-record hashes are recorded above.
Its seven inputs remain the following checksum-verified files under the primary
`E:\Graph-auto-reader` checkout; no payload was modified or copied from C:.

| Primary-checkout input | SHA-256 |
| --- | --- |
| `artifacts/goal19-opencv-source/evidence-repro-pass2-final-a/bin/OpenCvSharpExtern.dll` | `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31` |
| `ml/ocr/official_bakeoff/runs/conversion/PP-OCRv5_mobile_det.onnx` | `d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb` |
| `ml/ocr/official_bakeoff/runs/conversion/en_PP-OCRv5_mobile_rec.onnx` | `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` |
| `artifacts/goal22-worktrees/resume-20260908/artifacts/synthetic-runtime-evidence/official-metadata-source-c96/detector.json` | `2ddb1599df210da50bf097383b1f691ee6e1bed27ba9c98b6695a6b06e7e8d51` |
| `artifacts/goal22-worktrees/resume-20260908/artifacts/synthetic-runtime-evidence/official-metadata-source-c96/recognizer.json` | `17259b3260c0d39b292be70b36e68d53a067992de3afeaf0809d4d981de4327a` |
| `LICENSES/PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt` | `c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4` |
| `LICENSES/PaddlePaddle-PP-OCRv5-Models-Notice.txt` | `8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729` |

### Active consumer and cleanup boundary

The actual GPU bridge, fixed optimizer run and bounded open-dev investigation
are complete. [The P1 outcome](GOAL-22-OCR-MARKER-STYLE-GPU-P1-OUTCOME.md)
records the presealed parity failure and diagnostic score. After its record is
pushed, retire `artifacts/marker-style-nine-source-20261111` and
`artifacts/marker-style-capture-binding-20261111`, including their authenticated
fixture, candidate, source snapshot, tensors and targets. Manual-preview QA may
retain its one selected synthetic image until its saved-project check finishes.
Until those consumers finish and their outcomes are pushed, keep the required
inputs. Historical V44/V46 inputs remain
unchanged for their explicit compatibility/bridge consumers. These historical
truths or splits were not evaluated by the nine-source run.

The five closed unit-capture directories below were inventoried, with no runtime
references found. Terra confirmed that its bridge does not consume them. They
contained 582 files, 293,167,982 bytes, plus 20 pytest links whose targets resolved
inside the same respective directories. After outcome commit `67bd1c4d` reached
`origin/main` through `b53bc76d`, the links were removed non-recursively and each
directory was rechecked for reparse points before deletion. All five are now
absent. The actual nine-source capture and its active inputs were preserved.

| Directory under `artifacts` | Files | Bytes |
| --- | ---: | ---: |
| `marker-style-capture-tests-20261111` | 105 | 1,888,129 |
| `marker-style-capture-fixed-tests-20261111` | 81 | 72,655,536 |
| `marker-style-capture-verified-tests-20261111` | 149 | 72,913,336 |
| `marker-style-capture-rebound-tests-20261111` | 152 | 72,914,705 |
| `marker-style-capture-external-metadata-tests-20261111` | 95 | 72,796,276 |

The completed CPU capture and supervision preparation passed. Training-input
bridge completion, any separately authorized optimizer work, model accuracy,
and production approval are not established by this run.
