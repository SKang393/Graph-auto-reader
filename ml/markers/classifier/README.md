# Marker shape, fill, artifact, and embedding classifier

This directory contains an original, deterministic PyTorch training toolchain
for classifying fixed 32 by 32 marker-centered ink-probability patches. It uses
only procedural project data. It does not use private figures, external data,
pretrained weights, downloaded weights, or earlier application source.

## Output contract

`CompactMarkerClassifier` has one compact spatial encoder and four independent
training outputs:

- `shape_logits`: circle, square, triangle-up, triangle-down, diamond, star,
  asterisk, cross, or other;
- `fill_logits`: filled, open, or unknown;
- `artifact_logit`: probability evidence for a non-marker crop;
- `embedding`: a 12-value L2-normalized compact identity vector.

Shape and fill are never collapsed into one class. The embedding receives a
supervised metric loss for matching the same shape/fill symbol through
degradation.

The C# inference adapter accepts one named ONNX output, so `export.py` wraps the
frozen model without changing its weights and concatenates the four training
heads into `classification_heads`, shaped `[N,25]`. Runtime column order is
exactly nine temperature-scaled shape logits, three temperature-scaled fill
logits, one artifact logit, and twelve embedding values. Temperatures are read
from the selected checkpoint, so the C# decoder's ordinary softmax produces the
validated calibrated probabilities. The batch dimension is dynamic and the
spatial input remains fixed at `[N,1,32,32]`. Export verification checks both
whole-tensor parity and exact correspondence between every packed slice and its
documented runtime transform.

## Fixed data and experiment protocol

Train, validation, and held-out families and templates are disjoint. The
procedural corpus covers all required shape/fill combinations, mixed-series
edge context, line-contact deformation, minority probes, and text, axis, tick,
divider, arrow, bracket, intersection, and legend artifacts. Dataset records
include exact tensor hashes. Generated patches, checkpoints, ONNX files, and
reports remain ignored.

`ACCEPTANCE_GATE.md` preregisters a session-local 0.90 macro-F1 gate separately
for shape and fill. It explicitly does not claim maintainer agreement. Three
validation-only model experiments are recorded in `EXPERIMENT_COMPARISON.md`.
The final held-out command creates an exclusive seal and refuses a rerun. A
failure is preserved without test-set tuning.

## Commands

### Original-raster diagnostics

`runtime_patches.py` prepares original-image luminance and marker patches using
the application's integer alpha/color conversion and bilinear ink sampling.
It covers the identity-transform, single-channel route. Enhanced transforms and
isolated legend bounds are outside this helper's scope. The historical
`dataset.py` is unchanged.

`runtime_diagnostic.py` uses this helper and the owned full-scene marker renderer
to produce 2,160 balanced native/fine-raster pairs. Every shape sees the same
sizes, strokes, subpixel positions and line/neighbor contexts. This unsealed
diagnostic imports no external images, starts no training and loads no model.
The fine-raster member is a controlled reference, not the historical training
pipeline or a proposed runtime change. Degraded fill and visually identical
cross/asterisk fill labels are left unscored. Call `diagnostic_cases()` and
`diagnostic_pair(case)` to obtain recipes and `[1,32,32]` ink tensors. Results
diagnose coverage; they cannot grant production approval.

### Historical training and export entry points

```powershell
python -m pytest ml/markers/classifier/tests -q
python -m ml.markers.classifier.train --output ml/markers/classifier/artifacts/session11-final-e3
python -m ml.markers.classifier.export --checkpoint ml/markers/classifier/artifacts/session11-final-e3/marker-classifier.pt --output ml/markers/classifier/artifacts/session11-runtime-packed/marker-classifier-packed.onnx --report ml/markers/classifier/artifacts/session11-runtime-packed/onnx-parity.json
python -m ml.markers.classifier.benchmark --checkpoint ml/markers/classifier/artifacts/session11-final-e3/marker-classifier.pt --onnx ml/markers/classifier/artifacts/session11-final-e3/marker-classifier.onnx --output ml/markers/classifier/artifacts/session11-final-e3/benchmark.json
```

Production repair revision status:

The `marker-classifier-production-repair-v1` budget is exhausted. Its retained
CLI refuses before creating output. A future authorized repair requires a new
preregistered revision and cannot reuse `P1`, `P2`, or `P3`.

The 2026-08-04 three-candidate budget passed validation and all confirmation
classification metrics, but failed direct packed ONNX parity at the strict
`1e-5` gate. The candidate remains rejected and the manifest remains
fail-closed. That historical confirmation was a same-family repeat and is not
generalization evidence. The disjoint v2 confirmation was then evaluated once
and also failed packed ONNX parity at `1.1444091796875e-05` against the
`1e-05` limit.

`marker-classifier-production-runtime-repair-v2` is a separate three-candidate
defect revision. P1 retained the exact checkpoint with zero optimizer steps and
changed only the transport contract from high-magnitude logits to calibrated
probabilities. Its 140-case fixed selection gate passed shape F1 `1.0`, fill F1
`0.9907389542735867`, artifact F1 `1.0`, minority F1 `1.0`, and full-split CPU
ONNX parity `2.0265579223632812e-06` against `1e-5`. New public-v3 and disjoint
confirmation-v3 procedural families, manifests, evaluator source hashes, and
the same strict gates were frozen before execution. The once-only public-v3
gate passed 140 cases with shape F1 `0.9907246376811594`, fill F1
`0.9440313111545988`, artifact and minority F1 `1.0`, and CPU ONNX parity
`1.7285346984863281e-06`. The once-only disjoint confirmation-v3 gate also
passed 140 cases with shape F1 `0.9813519813519813`, fill F1
`0.9440559440559441`, artifact and minority F1 `1.0`, and CPU ONNX parity
`1.0728836059570312e-06`. No gate replay is authorized. Production probability
decoding, exact provider execution, model-store discovery, and packaging
discovery were then evaluated together. The exact payload passed
the C# probability decoder on CPU and DirectML with maximum provider difference
`3.5762786865234375e-07`; its checksum-bound notice and benchmark evidence
resolved from the strict production store; and the packaging audit discovered
one eligible exact model file without a classifier blocker. The classifier is
approved for production discovery and packaging. Marker-center, OCR,
end-to-end workflow, distribution, and clean-machine release gates remain
mandatory.

The benchmark command is intentionally single-use for one output directory.
The checksum-bound selected weights and direct reports remain ignored local
artifacts. The canonical manifest, notice, gate seals, runtime contract, and
tests are tracked.
