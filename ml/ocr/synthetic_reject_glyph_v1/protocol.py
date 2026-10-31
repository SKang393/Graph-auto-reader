# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fixed nonproduction schema and bindings for synthetic V5 reject glyphs."""

from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
PACKAGE = Path(__file__).resolve().parent
SCHEMA = "graphreader.synthetic-reject-glyph.v1"
SAMPLE_KIND = "non_numeric_text_reject"
REJECT_CLASS_INDEX = 13
V5_ALPHABET = "0123456789.-%"
CANVAS_WIDTH = 128
CANVAS_HEIGHT = 32
ENCODED_SHAPE = (1, 24, 26)
STRUCTURAL_REJECT_MINIMUM_HEIGHT_RATIO = 0.75
MAX_RUNTIME_COMPONENT_COUNT = 8

# These hashes bind the actual V5 component extractor and the class definition
# used below. A source change requires a deliberate protocol review.
V5_SOURCE_SHA256 = {
    "ml/ocr/component_ensemble_v5/model.py": "9374791d53ba6524818955310036eff8d2feebdc7103e807b3f1029d5e2fda36",
    "ml/ocr/component_ensemble_v5/protocol.py": "ae5d140f0bb3cc0fb822de574a49d2c1e944f1efe4ae1475d9c5e503a0c0b6ac",
    "ml/ocr/component_ensemble_v5/dataset.py": "f2aa415255fe7d322acdfb41a87e60fe9d6fb5920df8c431851b6836a30e93d9",
    "ml/ocr/component_ensemble_v5/pipeline.py": "36f699b35a3a402fe344b655e0a8705c478ecb9537e123d498e3eaf2c00f2284",
    "ml/ocr/component_geometric_v4/dataset.py": "a1d316c728ce5b95828cde171ed5e167757cd10c1a18251e89f19c7543fc2f1c",
}

SPLIT_FAMILIES = {
    "train": {
        "id": "public-clean-noto-proportional-train-v1",
        "font_ids": ("sans", "serif"),
    },
    "validation": {
        "id": "public-clean-noto-mono-validation-v1",
        "font_ids": ("mono",),
    },
}

FONT_MANIFEST_SHA256 = "0eb134a6e980e3042360b6477bf2ce178921d7dc23b3ded9ba12ee661b9470a2"
FONT_VERIFIER_SHA256 = "b2094803615a56ed472a38f0f741ddbbe938758f9629f39b96c2d834a853f76d"
PHRASE_CATALOG = PACKAGE / "phrases.json"
