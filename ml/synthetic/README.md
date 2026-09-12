# Synthetic SCD Graph Generator

This package creates deterministic, declarative single-case design graph scenes
and perfect original-pixel annotations. It uses no published figures, private
images, downloaded assets, or bundled font files.

    python -m ml.synthetic.generate --preset smoke --seed 393
    python -m ml.synthetic.generate --preset real_range --seed 393
    python -m pytest ml/synthetic/tests -q

The default output is written below ml/synthetic/datasets/, which is ignored by
the repository. Each case contains its scene declaration, PNG image, binary
marker mask, annotation JSON, and source graph CSV. Dataset-level output
contains deterministic seed and split manifests, a contact sheet, and a sanity
report.

## Split policy

Train, validation, and test membership is assigned by renderer, system-font,
degradation, chart-template, and marker-style families. Families are disjoint
across splits. This prevents superficially different images from the same
rendering recipe leaking into held-out evaluation.

## Fonts and dependencies

No font binaries are included. The renderer resolves only fonts already
installed on the host, or a user-supplied font path. The selected font family
and file name are recorded in annotations. Generation fails clearly when no
eligible installed font can be found.

- Pillow: MIT-CMU license, runtime raster drawing and PNG encoding.
- jsonschema: MIT license, scene-schema validation.
- pytest: MIT license, test-only.

Generated scenes are original project output and contain no copied study data.

## Publication-range profile

The `real_range` preset is a deterministic, synthetic-only coverage matrix for
compact publication graphs. It includes the measured aggregate resolution,
text-height, marker-size, stroke-width, text-density, RGB8 PNG, JPEG-roundtrip,
and production 960-long-side/128-stride preprocessing envelope. Its
`distribution-report.json` is a fail-closed aggregate gate. No private image,
text, case identity, or model output is used by the preset.

## OCR coverage before sealing

`ocr_sealed_coverage.validate_ocr_sealed_coverage` validates unsealed synthetic
annotations and returns only aggregate text, character and role denominators.
It uses the full OCR scorer's visible, nonblank, rendered-text selection and
requires original-pixel boxes within the source canvas. Both source text and
region identities must be unique within each source. Condition labels count as
phase headings, matching the frozen V2 scorer. Invalid Unicode, roles and boxes
fail closed. No model inference, file reading or candidate selection occurs.

Run `python -m pytest ml/synthetic/tests/test_ocr_sealed_coverage.py -q`.
This helper does not register a reserve or authorize a sealed read. OCR-specific
protocol, registry dispatch and aggregate worker integration remain required;
existing marker-scoped reserves are not reclassified as OCR evidence.
