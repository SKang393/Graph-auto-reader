<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# V46 scale coverage for the adapted DB detector

This revision tests the scale gap recorded in
[`GOAL-22-OCR-SCALE-COVERAGE-DIAGNOSIS.json`](../../../docs/GOAL-22-OCR-SCALE-COVERAGE-DIAGNOSIS.json).
Its recipe and source/input checksums are fixed in `protocol.json` and
`p1.json`. It is a synthetic-development candidate, not a production model.

## Inputs and training

`inputs.py` authenticates the existing V44 captures, features, training
labels, pretrained trunk, initializer and ONNX payload. It retains all 28
original training inputs and all 709 labels. Only these training pixels are
enlarged by bilinear interpolation at 2x scale, before the frozen trunk.
Three 1024-pixel windows per panel start at 0, 768 and 1024, adding 84 windows.
Partially clipped words are recorded and ignored in the loss. Full positive
targets remain supervised even where a partial-word mask overlaps them.

All nine historical development inputs remain unchanged and are excluded from
optimization and checkpoint selection. The complete current native and CSV
fixtures are separate application comparisons. Their source identities and
truth denominators are registered explicitly; historical and current CSV
development images are not interchangeable.

`training.py` adapts the exact V44 head for 24 AdamW epochs over 112 training
windows, giving 2688 optimizer steps. Learning rate is 0.00003, weight decay
0.0001, batch size one and seed 20260930. The selected checkpoint is the
earliest minimum mean loss over the complete training population. Pretrained
trunk parameters and batch-normalization buffers remain frozen.

## Execution and recovery

The runner requires the canonical unused P1 authorization, authenticated local
artifacts and unchanged source snapshot. Generated tensors and weights are
ignored local artifacts and are not distributed with this module.

Run through `tools/Run-TrainingCpuBudget.ps1` with Python 3.13:

```powershell
./tools/Run-TrainingCpuBudget.ps1 -Program python -ProgramArguments @(
  '-m', 'ml.ocr.scale_coverage_db_head_v46.runner',
  '--output', 'artifacts/goal22-runs/ocr-scale-coverage-v46/P1'
)
```

Use a new output directory. The registered P1 output must not already exist.
`recovery.pt` is replaced atomically after each complete epoch. A void
interrupted run can resume with `--resume` and `--resume-sha256`, preserving
the exact input identity, optimizer, deterministic draw state and selection
history. An interruption does not authorize a different recipe.

Training completion writes `training-stage.json` and the patched ONNX model.
The status remains `trained_parity_pass_pending_application_dev`. Application
evaluation and the outcome ledger update are required before closing P1.
Training alone does not consume a sealed candidate or approve the model.

## Numerical checks and tests

Initializer reconstruction must reproduce the exact V44 ONNX bytes. The
trained graph may change only the declared head constants. Raw probability
parity covers every one of the 112 training and nine development inputs.
The numerical tolerance references the existing full-model conversion
contract, 0.0001. The stricter historical 0.00001 result and all probability
0.3 pixel-decision differences are reported separately. These are numerical
checks, not accuracy thresholds. Application settings remain fixed.

```powershell
./tools/Run-TrainingCpuBudget.ps1 -Program python -ProgramArguments @(
  '-m', 'pytest', 'ml/ocr/scale_coverage_db_head_v46/tests', '-q'
)
```

Tests cover crop-coordinate projection, data separation, boundary-word masks,
byte identity, immutable arrays, deterministic training, frozen buffers,
exact interrupted-run recovery and refusal of changed inputs or recipes.
The central evidence policy and acceptance bars are unchanged. This module
neither reads private/sealed corpora nor activates or packages a model.
