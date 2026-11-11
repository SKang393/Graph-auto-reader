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
private/sealed reads are all zero; the sealed-run budget for this batch is zero.

## Verification and next consumer

`python -B -m pytest ml/ocr/official_bakeoff/tests/test_train_marker_style_coverage.py ml/ocr/official_bakeoff/tests/test_text_extent_head_inputs.py ml/synthetic/tests/test_dataset.py -q -p no:cacheprovider --basetemp artifacts/marker-style-consumer-tests-20261111`
passed 32 tests in 42.51 seconds. After strengthening identifier rejection
assertions, the eight affected cases passed in 5.39 seconds. Diff checks passed.
The nine-source test fixture has input-manifest SHA-256
`1ee37ea0378058d15dc16e65b757d7b1f95279e60e44db3813fd341eaf3f8a14`
and corrected source-truth SHA-256
`0ab8d513e8ddf4ff65775092bb2e05f36b5addd74970b0601b0f7d497c927f2b`.

This fixture alone is source-pixel preparation, not an accuracy improvement.
The new nine-panel capture consumer is described below; actual inference and
target generation remain the next execution step.

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
