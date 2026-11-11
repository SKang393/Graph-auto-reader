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

This is source-pixel preparation, not trainer-ready tensors or an accuracy
improvement. Existing capture profiles accept six supplemental panels or the
historical 37 panels. An explicit nine-panel profile and actual captured
crop/tensor geometry are the next consumer; existing profiles must remain
unchanged. Model-grid targets must use those actual transforms.

Historical preflight/truth/tensor/scale-manifest hashes are respectively
`23afd6d3163c84998586b54e72ef28d46cf80c561ff2a63449a005b48188cc53`,
`60a474a7e417c5f4b3622a7920da18d70d2b6c8677a644f13137d1c08662e1d3`,
`15e48b27ce3b8f5f53311abbef7235c35b3e8e361c8baddb09a4d3a7e7d5938e`,
and `5748dcf05dca6083948d0054382c5e3c369f52db007c6ff2a3a5c86ae0ef90b5`.
Their existing `resume-20260908` inputs remain needed for the capture/adaptation
consumer. Task-owned `artifacts/marker-style-*-tests-20261111` test outputs are
disposable after this record is pushed. Regenerate them with the commands above;
do not commit generated images, annotations, tensors or weights.
