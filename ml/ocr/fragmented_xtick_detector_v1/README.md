# Fragmented numeric X-tick detector preparation V1

This isolated directory prepares a fresh, procedural OCR detector hypothesis:
numeric X-tick labels can be split into multiple glyph components by a small
gap while nearby axis/tick fragments and a marker-like dot must remain
negative. Its train-only materializer renders deterministic grayscale images
and target bounding boxes under a caller-selected fresh directory in this
project's `artifacts/` tree. It does not load a model, train, infer, open a
development or sealed corpus, or alter a shared gate, manifest, or package.

The fresh split namespaces are `train`, `dev`, and `sealed_public_future`.
Their case identities are derived only from this revision's family name, split,
seed, label and gap variant. They do not import V8, V9, V29, V30, archived
fixture, candidate weight, checkpoint, ONNX payload, or tensor bytes. This is
evidence of local procedural identity separation, not proof of global
historical-case non-overlap outside the explicitly excluded inputs.

Run the in-memory checks:

```powershell
python -m unittest ml.ocr.fragmented_xtick_detector_v1.test_prepare
python ml/ocr/fragmented_xtick_detector_v1/prepare.py --preflight
python ml/ocr/fragmented_xtick_detector_v1/materialize.py --output artifacts/fragmented-xtick-detector-v1-train-materialization-v1
```

The printed preflight receipt binds the current source and protocol hashes and
states that no pixels have been rendered by the preflight itself and no weights,
optimizer steps, development evaluation, sealed evaluation, production approval,
or release eligibility exist. The materializer rejects every non-train case,
requires a checksum-bound Noto Sans project font, writes only to a fresh,
non-reparse E-project artifact directory, and emits 96 train images plus
annotations. A later separate review must select an architecture, GPU-only
training recipe, development evidence, frozen public-gate procedure, real
acceptance, model manifest, store entry, and packaging discovery. This package
does not authorize any of them.
