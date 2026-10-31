# Axis ranker public dev scorer

This utility prepares an exploratory comparison between the first candidate in
the production observer order and a train-only 17-feature linear checkpoint.
It consumes a complete, truth-free production candidate export for the fixed
32-source public synthetic dev corpus, then reads the dev annotations offline.
It does not invoke the detector, alter features, construct a model runtime, or
run an optimizer. No evaluation was run while preparing this utility.

The expected split has 32 sources and 48 panels: 30 detected sources, 2
full-image fallback sources, and no failed sources. All 48 panels remain in the
report denominator. Full-image fallback panels remain unscored; only unique,
successful detected-panel to plot matches receive corner scores. The
association and target calculation reuse
`candidate_ranker_foundation_v1.labels`, including its center-in-crop match and
mean four-corner Euclidean error normalized by plot width and height
independently. Pixel error is separately reported as the unnormalized mean
Euclidean distance across the same four ordered corners.

The checkpoint, training report, config, and finalization receipt are pinned in
`protocol.json`. The checkpoint is explicitly unapproved. The output is a
diagnostic, not a gate, model approval, or production admission.

Before any future evaluation, independently review the dev-mode production
exporter path and its fixed public input identity. Then generate a fresh
source-only dev export, prepare an evaluation
binding that pins that export report SHA, and then run:

The fixed dev admission has a split schema: its `files` map pins 12 source
files, while `runtime_files` aggregates several runtime bundles. The exporter
and scorer bind the exact 73-file `axis-roles-panelization-build-v2/` subset
from `runtime_files`, and separately verify every path and hash in `files`.
This is intentionally different from the train admission, whose unified
`files` map remains validated by the existing train path.

```powershell
python -m ml.axis.candidate_ranker_dev_eval_v1.evaluator `
  --binding artifacts/goal22-runs/axis-ranker-dev-eval-v1/input-binding.json `
  --binding-sha256 <reviewed-binding-sha256> `
  --output artifacts/goal22-runs/axis-ranker-dev-eval-v1/report.json
```

The evaluator requires a fresh output under `artifacts/goal22-runs` and hashes
all bound inputs before parsing. It also verifies the complete scorer source
bundle, the exporter input/runtime assembly and source closure, the pinned
panelizer runtime receipt, and exact production OpenCV/axis geometry defaults.
Each panel's crop bounds, original-pixel coordinate declaration, source/panel
hashes, and source↔panel translation matrices are checked before annotations
are loaded. Exported success/failure counts must cover all 48 panels; failed
panels remain explicitly unscored in the output. Training and dev data never
enter the same feature-generation stage. No private or sealed data is accepted.

The fixed v3 annotations identify plot rows with `panel_id`, while the
foundation association helper expects an `id` field. The scorer adapts that
annotation `panel_id` into its local plot ID and still joins plots to current
runtime panels only by plot-center-in-crop geometry. Annotation IDs are not
treated as current runtime panel IDs. Preflight verified 46 unique detected
geometry matches and left the two full-image fallback sources unscored.

Production normalized feature rows contain only `rank` and `features`; they do
not contain a candidate identity digest. The scorer therefore binds each row
to its ordered snapshot using the exporter's zero-based snapshot rank and
one-based normalized rank, then recomputes and compares all 17 values. It
rejects rank/order changes, unknown row fields, or feature-vector changes.

Focused tests include in-memory unit fixtures and one integration check over
the pinned public synthetic dev manifest, v5 truth-free export, GPU-training
receipt, checkpoint, finalization and annotations. That check calls the scorer
in memory, asserts only denominator/scope invariants, and never writes scored
results. It is exploratory development evidence, not model acceptance.

```powershell
python -m unittest ml.axis.candidate_ranker_dev_eval_v1.test_evaluator
```
