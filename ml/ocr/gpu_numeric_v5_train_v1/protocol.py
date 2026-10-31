# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fail-closed GPU and input contracts for V5 numeric/reject training."""
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PACKAGE = Path(__file__).resolve().parent
DEVICE = "cuda:0"
EXPECTED_GPU_NAME = "AMD Radeon RX 6600 XT"
EXPECTED_GPU_ARCH = "gfx1032"
CLASS_COUNT = 14
REJECT_CLASS_INDEX = 13
PERCENT_CLASS_INDEX = 12
CANVAS_SHAPE = (32, 128)
ENCODED_SHAPE = (1, 24, 26)
MAX_RUNTIME_COMPONENT_COUNT = 8
STRUCTURAL_REJECT_MINIMUM_HEIGHT_RATIO = 0.75
ALPHABET = "0123456789.-%"
POSITIVE_ALPHABET = "0123456789.-"

V5_SOURCE_HASHES = {
    "ml/ocr/component_ensemble_v5/model.py": "9374791d53ba6524818955310036eff8d2feebdc7103e807b3f1029d5e2fda36",
    "ml/ocr/component_ensemble_v5/protocol.py": "ae5d140f0bb3cc0fb822de574a49d2c1e944f1efe4ae1475d9c5e503a0c0b6ac",
    "ml/ocr/component_ensemble_v5/dataset.py": "f2aa415255fe7d322acdfb41a87e60fe9d6fb5920df8c431851b6836a30e93d9",
    "ml/ocr/component_ensemble_v5/pipeline.py": "36f699b35a3a402fe344b655e0a8705c478ecb9537e123d498e3eaf2c00f2284",
    "ml/ocr/component_geometric_v4/dataset.py": "a1d316c728ce5b95828cde171ed5e167757cd10c1a18251e89f19c7543fc2f1c",
}

POSITIVE_SCHEMA = "graphreader.synthetic-tick-corpus.v3"
REJECT_SCHEMA = "graphreader.synthetic-reject-glyph.v1"
POSITIVE_V5_ADAPTER_SCHEMA = "graphreader.v5-component-crop-adapter.v1"

# These package-local pins are updated only when the blocked-by-default package
# is deliberately versioned. They authenticate fixed inputs, not approval.
RECIPE_SHA256 = "54ad341f92e19ef38d13f0c63cbb400ae31c47047084807cc10b88fd78b28d60"
# `review_receipt_sha256` is excluded from the canonical registration core.
# The receipt binds that core digest, then its byte hash is stored in the
# registration, so neither document hashes the other recursively.
REGISTRATION_RECEIPT_HASH_FIELD = "review_receipt_sha256"
RUN_BUDGET_STEPS = 256
PUBLIC_V3_CONTRACT_MANIFEST = "artifacts/synthetic-tick-corpus-v3-20261031-v1/manifest.json"
PUBLIC_V3_CONTRACT_SHA256 = "7bb461f712307b17987242ec358ecca043bdca963871dee306a7285268c1f2e7"
PUBLIC_REJECT_CONTRACT_MANIFEST = "artifacts/synthetic-reject-glyph-v1-20261031-final2/manifest.json"
PUBLIC_REJECT_CONTRACT_SHA256 = "01c6a01c00307a2bba5a0a571451e8a3223f63dddcc9ecc277810819c2f58cd6"
