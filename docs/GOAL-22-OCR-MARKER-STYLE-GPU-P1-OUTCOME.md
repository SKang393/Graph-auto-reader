<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR marker-style GPU P1 outcome

The fixed synthetic P1 run is **void before sealed evaluation** because trained
ONNX parity exceeded the predeclared absolute tolerance. Its later open-dev
score is diagnostic only: export parity remains failed, acceptance is blocked,
and the result does not authorize a model, release, or production activation.
No private or sealed inputs were read and no optimizer retry was performed.

The RX 6600 XT run passed its fixed schedule assertion: 240 epochs and 10,320
optimizer steps. The launcher reported 416.814 seconds. The nine new train
panels passed initializer parity before optimization. The original runner did
not record the selected epoch or loss history; neither can be reconstructed
from its checkpoint.

For the nine synthetic train panels, trained ONNX versus GPU head had maximum
absolute difference `1.093745231628418e-5` and RMSE `4.639222623485714e-8`
over 3,538,944 pixels. No pixel changed the production `> 0.30` decision.
The original absolute-only `1e-5` bound failed. A later, independently fixed
PyTorch float32 comparison (`atol=1e-5`, `rtol=1.3e-6`) also failed: one pixel
in one panel exceeded its bound. The saved export's nine trainable and four
frozen batch-normalization constants matched the selected checkpoint. These are
plumbing observations, not accuracy or approval results. The float32 comparison
defaults are documented by [PyTorch testing](https://docs.pytorch.org/docs/main/testing.html).

## Open-dev diagnostic

The existing frozen candidate was evaluated on the fixed historical open-dev
split: three sources, nine panels, 183 truth regions and 183 predictions. Raw
and successfully recognized geometry both yielded 146 true positives, 37 false
positives and 37 false negatives; precision and recall were each
`0.7978142076502732` (79.7814%), below the shared 95% bars. This diagnostic is
not a regression delta against a different 21-source dataset.
This evaluator's raw and recognized geometry scores are not a complete composed
Review-workflow result. The existing layout/tick-lane evaluator can exercise
that composition, but no additional replay was selected for this failed export.

The runtime completed all 37 requested train/dev panels without omitted failed
panels or region failures. It took 37.053 seconds wall time (34.793 seconds in
the runtime report); scoring took 23.560 seconds. Preparation used
`gpu_head_evaluation.py prepare-diagnostic`, the frozen C# runtime used
`--evaluate-official-head-candidate`, and the existing scorer was invoked through
`gpu_head_evaluation.py score`. The model SHA-256 remained
`f063fc9dbe874f1de68da5aa51168392aa9d8b9d29a3e3af259b3bd8614102eb`.
The diagnostic score report SHA-256 is
`b6be8b541eb13ca16a7ebaaf248a3d34c039ea3f763a304b44690b68c1cc8bbd`;
the historical worktree retained it at
`artifacts/goal22-runs/ocr-marker-style-gpu-diagnostic-20261111/open-dev-diagnostic-score.json`.
A bounded inspection of three unmatched predictions and one matched control
checked the actual checksum-bound pixels and reproduced their panel-to-source
transforms. One heading was split into two correctly located word boxes (IoU
0.411 and 0.439 against the full phrase); one participant label retained only
its numeric suffix (IoU 0.107); one hollow circle was a true nontext detection
read as `o` (IoU zero). The matched printed `1` control had IoU 0.544. The
participant crop's 313-pixel vertical offset was correct. These examples show
that 37 unmatched predictions are not 37 established nontext detections. They
do not identify a general coordinate, crop, or preprocessing defect. No
deterministic fix or optimizer retry was selected from this inspection.

## Evidence and verification

The original run used candidate config SHA-256
`b78a88c8b7b28c0fdb1a5a1b49783decfc77991134b21fefa0dd242bb14c8c36` and
source bundle SHA-256
`469866eb243b2fed096c02036651566f8287af592c52b24f359b8eaeea761b19`.
The void record and diagnostic-only receipt bind the original checkpoint, ONNX,
source snapshot, configuration, and launch log. The receipt SHA-256 is
`f5ed3e539776742a04cc87dbfeb2b746a7ed29c62ca72b12e922010401623a2a`.
The maintained ledger remains the canonical source for candidate status.
The presealed attempt did not consume P1 or retire its revision. Repairing
plumbing can reuse its authorization; a different hypothesis would require its
own recorded revision. The committed configuration preserves the actual run's
original source binding, so it is not a runnable authorization for the later
persistence fix without an explicit binding update.

The runner now atomically records a checksum-bound selected checkpoint and
optimization result, including selected epoch and loss history, before export.
The existing successful training-stage schema and presealed void behavior remain
unchanged. The combined GPU adapter, bridge, candidate and evaluation tests
passed 19 tests in 9.708 seconds. The corresponding focused command is:

```powershell
& 'E:\Graph-auto-reader\tools\gpu-training\Run-GpuPython.ps1' -PythonArguments @('-m','unittest','ml.ocr.official_bakeoff.tests.test_gpu_head_training','ml.ocr.official_bakeoff.tests.test_gpu_head_bridge','ml.ocr.official_bakeoff.tests.test_gpu_head_candidate','ml.ocr.official_bakeoff.tests.test_gpu_head_evaluation','-v')
```

The historical training command below ran from the GPU worktree at base commit
`6d9af5a56b741a89fc7d5683090d52fe1405d074`, with the recorded immutable
source snapshot. It is a record of the failed attempt, not authorization to
rerun the later runner against stale bindings:

```powershell
& 'E:\Graph-auto-reader\tools\gpu-training\Run-GpuPython.ps1' -PythonArguments @(
  '-m', 'ml.ocr.official_bakeoff.gpu_head_candidate',
  '--config-sha256', 'b78a88c8b7b28c0fdb1a5a1b49783decfc77991134b21fefa0dd242bb14c8c36',
  '--historical-root', 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20260908',
  '--input-root', 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-calibration-review-20261111',
  '--old-prefix', 'C:\Users\Sungwoo\Desktop\Graph-auto-reader\',
  '--relocated-root', 'E:\Graph-auto-reader',
  '--output', 'E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-ocr-gpu-20261111\artifacts\marker-style-gpu-p1')
```

The runtime command was `dotnet <diagnostic>/runtime/GraphReader.SyntheticRuntimeEvidence.dll
--evaluate-official-head-candidate <diagnostic>/evaluation-request.json
44740f6cbf48d64759a283e3c8ef991121c2d8082df9bcc7b453cf65a83114a9
<diagnostic>/candidate.json
8637742fc2f5a4d1a51f68732106fedee450a13c25cf848c2d867b5cb8745208
<diagnostic>/evaluation`, where `<diagnostic>` was the historical worktree's
`artifacts/goal22-runs/ocr-marker-style-gpu-diagnostic-20261111` directory.
The runtime report hash was
`18a8ecfb719aa862fefd0d919dc5b5310d06576d72b352c26492af3e9450202d`.
Scoring used `gpu_head_evaluation score` with that report and preparation hash
`c200a7dc5906dcbd1f38b345ef971385aa334c96e38834d26bfb45451a920dd4`.

## License, disposition and retention

The code retains Apache-2.0 headers. It reuses the existing reviewed
PP-OCR-derived parent, manifests and notices; no new dependency or external
weights were imported. The parent SHA-256 is
`d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb`.
No model-store entry, production approval, version increment or package was
created for this outcome.

After this outcome and the canonical ledger reach `origin/main`, retire the
failed P1 checkpoint/ONNX, diagnostic predictions/runtime copies, sampled
screenshots, raw logs and attempt snapshots. The bounded investigation has
finished and no further candidate consumer is scheduled. Preserve the existing
historical runtime models and authenticated base training inputs. The newly
generated nine-source tensors, masks and graph copies can also be retired once
the manual-preview QA consumer releases its selected synthetic image. Their
tracked generator, capture/target commands and input identities are recorded in
[the capture outcome](OCR-MARKER-STYLE-COVERAGE-20261111.md).
