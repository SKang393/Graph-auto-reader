<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Exploratory synthetic X-tick dataset

The fixed `real_range` dataset contains four training scenes with 47 visible
X-tick labels and six validation scenes with 30. Its renderer, font,
degradation, template, and marker-family keys are disjoint between the two
splits; the image and font-byte hashes are also disjoint. The generation and
summary validation are in `ml/ocr/synthetic_tick_family_v1`.

The validator checks the fixed scene schedule, all generated artifact hashes,
split contents, exact file inventory, and visible tick text/geometry against
its source scene. Two same-seed generations produced identical manifest bytes.
Marker-artifact candidates are not asserted to be OCR negatives. Independence
from historical benchmarks has not been established, and this small dataset
does not authorize training or demonstrate model accuracy.

- Manifest: `artifacts/synthetic-tick-family-v1-20261030/manifest.json`
- Manifest SHA-256: `ba4d08a88d50946fcb8a7380d96210ed9671dde1b57644770c92d87581ec6729`
- Validation: 18 focused synthetic tests passed; no training or private/sealed reads.
