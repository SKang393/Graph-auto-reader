# Synthetic V5 reject glyph corpus v1

This isolated package produces a small exploratory corpus for the V5 component
recognizer's class 13 (`reject`). It reads only this package's fixed ASCII
phrase catalog, the project-local public Noto font package, and the pinned V5
component extraction sources. It does not read graph images, annotation files,
private or sealed inputs, test data, or any model output.

The train and validation phrase IDs and font IDs are disjoint. Training uses
Noto Sans and Noto Serif; validation uses Noto Sans Mono. These fonts share one
upstream project, so independence from prior benchmark visuals is unproven.
Phrases are fixed package-generated ASCII words. Each phrase is rendered alone
onto a 128 by 32 grayscale canvas. We do not infer character alignment: every
component from an eligible phrase is class 13 because every non-space character
is outside the V5 alphabet `0123456789.-%`. Mixed strings are rejected. Empty
and contaminated crops are excluded with reasons. V5 returns before model
execution when a crop has more than eight components or any component reaches
the structural height threshold; all components from those crops are excluded
from class-13 supervision.

Each generated manifest records phrase, font, license, runtime source, raster,
and encoded tensor hashes. Tensor bytes are exact little-endian float32 output
from the pinned V5 `isolate_glyphs` extractor. The manifest records Python,
Pillow, and NumPy versions; cross-host byte-identical rendering is not claimed.

Run focused tests from the repository root:

```powershell
python -m pytest ml/ocr/synthetic_reject_glyph_v1/test_generate.py -q
```

To materialize a fresh dataset under `artifacts/`:

```powershell
python -m ml.ocr.synthetic_reject_glyph_v1.generate --output artifacts/synthetic-reject-glyph-v1-review
```

Generation is data preparation only. `training_approved` and
`production_approved` remain false, and the numeric foundation has not admitted
this separate schema. Do not use it for training until its class-13 labels,
split design, and schema are independently reviewed and formally integrated.
