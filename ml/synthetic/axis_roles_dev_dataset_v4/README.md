# Axis-role development dataset v4

This package replaces the v1-v3 parent-process render orchestration for new
development material. It exposes one fixed 16-recipe catalog. It does not
define a train, validation, or test split, make a historical-independence
claim, render a reserved family, or approve training.

The parent imports only standard-library modules. It starts one new Python
process per scene, with at most two children active. Each child verifies the
hash-pinned v1-v3 renderer dependencies, renders exactly one recipe, and
returns scene, annotation, overlay, and PNG bytes over stdout. The V1 PIL
canvas hook therefore exists only within a child process. The parent never
imports Pillow or `axis_roles_dev_dataset_v3`.

The child uses V3 only for its materialization and reference-check helpers. It
does not invoke V3 generation, verification, or historical scans, and does not
import V2.

The protocol pins the Noto Sans font bytes, accompanying OFL text, renderer
sources, recipe identities, and overlay definitions. This is source and test
code only until `generate` is deliberately invoked. Generation writes only a
new directory under this project's `artifacts/` tree.

Run focused tests:

```powershell
python -m pytest ml/synthetic/axis_roles_dev_dataset_v4/test_prepare.py -q
```

Future generation requires a fresh output path under `artifacts/`:

```powershell
python -m ml.synthetic.axis_roles_dev_dataset_v4.prepare generate artifacts/axis-roles-dev-v4-<new-id>
```

The generator writes a content-hashed manifest. Verification must use its
exact manifest digest:

```powershell
python -m ml.synthetic.axis_roles_dev_dataset_v4.prepare verify <output-path> <manifest-sha256>
```

Cross-host pixel identity and historical near-duplicate exclusion are not
claimed. The package does not read private, sealed, or reserved datasets.
