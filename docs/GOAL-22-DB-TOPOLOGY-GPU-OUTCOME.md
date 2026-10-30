# Goal 22 DB topology GPU outcome

**Disposition:** rejected experimental detector head. The candidate is not promoted, production-approved, or release-eligible. The recorded paired comparison is against the V46 experimental initialization, not the production detector baseline.

## Experiment

- **Hypothesis:** fitting the frozen DB detector trunk's topology head on 64 synthetic train scenes might improve DB region geometry while retaining the V46 runtime contract. This was a bounded experiment, not an acceptance claim.
- **Source commit:** server-crop worktree `HEAD` `764fd63dbe88a56b8f29d997adc26f3fc9a96454`.
- **Data:** 64 synthetic train-only scenes and 96 truth regions. The scene manifest separates 32 single multi-token truth scenes from 32 close two-truth scenes. No DEV, private, or sealed input was used for training.
- **Recipe:** RX 6600 XT via ROCm/HIP (PyTorch/HIP reported the device name `AMD Radeon RX 6600 XT`). Separately, DXGI DirectML enumerated PCI ID `1002:73FF` with that name; PyTorch did not expose a matching PCI ID, so the cross-API identity is inferred, not proven. 256 AdamW steps over 4 epochs, batch size 1, learning rate `3e-5`, weight decay `1e-4`, masked DB dice loss, and fixed final-epoch checkpoint selection. Seed base: `20261029`. Runtime: `64.7211 s`. Final epoch mean loss: `0.1412623`.
- **Initialization:** V46 experimental detector model SHA-256 `497a3527820ce0359496084e5afb1e0d3241aca79e192187f7ab737b27818b7f`; this was not the production detector baseline.
- **Protocol:** `gpu_protocol.json` SHA-256 `eb42855688f5a275d77f7a5600244ec1c615a4a94fb18c0e6c82f11d03def4c3`; preregistration SHA-256 `61b30c063756c39362cde86cdfece66a77b200f18f917aceb4a0517d1783c30b`.
- **Invocation:** the run report does not persist its original argv. The equivalent protocol-pinned launcher invocation is:

  ```powershell
  Set-Location 'E:\Graph-auto-reader\artifacts\goal22-worktrees\server-crop-head-20261029'
  & 'E:\Graph-auto-reader\tools\gpu-training\Run-GpuPython.ps1' -PythonArguments @(
    'ml/ocr/db_head_topology_train_v1/gpu_train.py',
    '--run',
    '--expected-protocol-sha256',
    'eb42855688f5a275d77f7a5600244ec1c615a4a94fb18c0e6c82f11d03def4c3',
    '--output',
    'artifacts/goal22-runs/db-head-topology-gpu-train-20261029-v1'
  )
  ```

## Paired full-OCR result

The original server recognizer, evaluator/runtime, composition, postprocessing, request, licenses, and fixed V3 source split were held constant (23 sources, 37 panels: 20 train sources / 28 train panels and 3 DEV sources / 9 DEV panels). Only the detector candidate changed. The same versioned scorer computed both reports. Counts below are exact truth matches / fixed truth denominator; CER is character edit count / truth character count. Geometry uses IoU minimum 0.5.

| Split and measure | V46 experimental baseline | Topology candidate |
| --- | ---: | ---: |
| Train exact recognition | 398 / 709 (56.1%) | 268 / 709 (37.8%) |
| Train CER | 1425 / 3268 = 0.436047 | 2553 / 3268 = 0.781212 |
| Train role correct | 403 / 709 | 292 / 709 |
| Train geometry FP / FN | 37 / 131 | 326 / 298 |
| Reused DEV exact recognition | 115 / 183 (62.8%) | 90 / 183 (49.2%) |
| Reused DEV CER | 587 / 1019 = 0.576055 | 997 / 1019 = 0.978410 |
| Reused DEV role correct | 107 / 183 | 80 / 183 |
| Reused DEV geometry FP / FN | 37 / 54 | 116 / 91 |

The candidate reduced exact matches by 130 on train and 25 on DEV, raised CER in both splits, and sharply increased geometry false positives and false negatives. The aggregate result rejects this head. It does not identify a causal failure mechanism. The 256 training steps had no DEV-based checkpoint selection.

The paired DEV split is reused diagnostic data: it had already been used by an earlier V44 diagnostic. These scores are not an untouched holdout or production acceptance. The production baseline was not part of this paired comparison, and none of these experimental numbers should be presented as a production-baseline comparison.

## Evidence

- GPU training report: `artifacts/goal22-runs/db-head-topology-gpu-train-20261029-v1/report.json`, SHA-256 `4cca49a973966835914efc8fc4bdda97e3cf2dfcf59cadaca9f227379442b7a7`.
- Final checkpoint: `artifacts/goal22-runs/db-head-topology-gpu-train-20261029-v1/final-head.pt`, SHA-256 `5b6a79a6de546362426ee1008631fafed58a3644cab554b8eb02806d39eea102`.
- Train scene manifest SHA-256 `eebac3158d3765e49bf518d59780b0b0b1e04869840abc296b7c8e57eaeefc62`; preprocessing bridge SHA-256 `1806bcc9ac4dbcc09fae5efe5f731710a07fb54fd4df00f8612641ce5d061229`; target manifest SHA-256 `4b8981e795776109f76018378bae4ba1da399321e777a8d9d2e500b9f060dac5`; feature manifest SHA-256 `2304dda87a4731f7c7fdf208c60b577a74671375a952a0d5f0a3508728595d5e`.
- Experimental export: ONNX SHA-256 `1f04117220cda66343d3bd18023615f27d11825277998910f77ad5cf1e3e100c`; detector manifest SHA-256 `79e0bdeaef3c594987e63e481d7bbd5b430a3e1f41cbeaa42356d0ef6f8fe4bc`; candidate provenance SHA-256 `e6b9ab578982114aa1ef3d001cf556924d815756c5b2e41328ec8bf2a631edb6`.
- Paired-evaluation preparation receipt: `artifacts/goal22-runs/server-rec-head-v46-topology-pair-20261030-v1/pair-preparation-receipt.json`, SHA-256 `48318cc4fb62156c8d7f2ee17136594f1879f8b471125da1615785e75aae539f`.
- V46 baseline score: `E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20260908\artifacts\goal22-runs\server-rec-head-v46-baseline-score-20261030-v1\score.json`, SHA-256 `15a9d9709baa24f6c70ae395168533cf6d2d4a6923ce66d8601f5e93fc3eb991`.
- Topology score: `E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20260908\artifacts\goal22-runs\server-rec-head-v46-topology-score-20261030-v1\score.json`, SHA-256 `832fa7b4848a0cf9ed1dedbf8a52174b3be5197c1541c413ea43f949501be599`.

## Retention and status

The training/export/evaluation scripts remain experimental in this worktree pending maintainer disposition; no release is authorized. Per `docs/ARTIFACT-RETENTION.md`, after this record is pushed and no active consumer remains, retire the generated dataset, tensor/feature/target caches, raw logs, prediction and evaluation dumps, temporary reports, screenshots, exported detector model/manifest, rejected final weights, intermediate checkpoints, and duplicate weights. This includes the completed training run directory `artifacts/goal22-runs/db-head-topology-gpu-train-20261029-v1`, export directory `artifacts/goal22-runs/db-head-topology-experimental-export-20261030-v2`, paired preparation directory `artifacts/goal22-runs/server-rec-head-v46-topology-pair-20261030-v1`, and the two baseline/topology score outputs documented above, plus their completed generated input/cache outputs. Do not remove current product models or source inputs still needed by an active consumer. The scripts remain experimental worktree sources pending disposition. Once generated sources/inputs are retired, no executable reproducibility is promised from this outcome record alone. No files are deleted by this record. Goal 22 is not complete.





