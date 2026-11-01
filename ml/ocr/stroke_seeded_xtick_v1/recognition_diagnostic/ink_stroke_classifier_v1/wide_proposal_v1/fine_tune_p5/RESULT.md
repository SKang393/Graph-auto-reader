# P5 train-only hard-negative result

## Training and preflight

P5 completed 20 epochs / 460 optimizer steps using `Run-GpuPython.ps1` on AMD Radeon RX 6600 XT (`cuda:0`, HIP 7.16.26385). CPU fallback was false. The run reused P4's 465 train rows and P3 initialization, source-uniform sampler, architecture, seeds, and AdamW schedule. The sole recipe change was 3x per-example BCE weight on the 14 label-0 crops scored at least 0.5 by frozen P4 on the train split.

The no-step GPU preflight verified 465 rows (89 positive, 376 negative), all 14 hard-negative labels and hashes, and RX 6600 XT optimizer residency after initialization. Final training receipt reports 10/10 model parameters, gradients, and 30 optimizer state tensors on the GPU. Loss changed from 1.5693269 to 0.2647620. Train-fit at 0.5: 67/89 positive detections and 5/376 negative false positives. No exact optimizer wall time was recorded.

## Paired consumed-probe result

This is the already-consumed procedural synthetic development probe, not acceptance. P3, P4, and P5 used the same 357 eligible proposal rows, fixed threshold 0.5, and the full 181 visible-tick denominator. Eight of the 365 source proposal rows overlapped training crops and were quarantined; duplicate rows were otherwise preserved.

| Model | Matched | Proposals | Unmatched | Precision | Recall | False proposals on no-visible panels |
|---|---:|---:|---:|---:|---:|---:|
| P3 | 67 | 78 | 11 | 0.85897 | 0.37017 | 0 across 10 panels |
| P4 | 67 | 82 | 15 | 0.81707 | 0.37017 | 3 across 2 panels |
| P5 | 67 | 75 | 8 | 0.89333 | 0.37017 | 3 across 2 panels |

P5 produced fewer unmatched proposals than P3 or P4 without adding a matched tick. It retained the two no-visible panels with false proposals, so this does not establish generalization or product readiness. All three inference receipts report RX 6600 XT with CPU fallback false.

## Overlap audit and evaluation corrections

All 465 training crop hashes were checked against the 365 proposal rows and P1 holdout crop-hash metadata. Eight probe rows overlapped training crops and were quarantined. Holdout hash overlap and train/probe source ID, source identity, and full-panel raster hash overlaps were zero.

The first paired-evaluation failure was before truth scoring: it looked for `prepare_stack` on the P5 wrapper. The second attempt scored P3 and P4 but failed before P5 model inference because the P5 teacher receipt hash was looked up on the P4 trainer. Both failures are recorded under their separate v1/v2 preflight outputs; no truth geometry was read on those attempts. V3 preflight and paired inference completed, and reproduced P3/P4 saved metrics exactly.

The failure receipts bind the exact failed evaluator/preflight versions:

| Failed attempt | Failure receipt SHA-256 | Evaluator SHA-256 | Preflight config SHA-256 |
|---|---|---|---|
| V1, missing `prepare_stack` | `78e50dd396aef3fe98e059b372563bbbd7df33e5622fc11d3563155256c7df60` | `062740e2ed0978bddf946d26ff2ad7bb2925ac42136addc07fc88acc088ea23e` | `aeb9fb8a4fd4b08e50943f5e7189a9cb923f193cfa983736822c002b2028ddc3` |
| V2, teacher receipt hash looked up on P4 module | `4de7957fe65690d426597134fb6ebba0d54f9a3d1f0baf2d7b3246c92a821085` | `eb0b810125574faea826e6139327720804a7620c8cc857d3edfa82a2dbd03f58` | `f7291874fa115e6a91c3b9567dac903781492104fb4a7377815752a7621786ad` |

Both receipts explicitly record that truth payloads were not read and P5 inference did not complete. The v1/v2 evaluator snapshots and config snapshots are retained locally for audit but are historical scratch copies, not needed in the source commit.

## Bound result hashes

| Artifact | SHA-256 |
|---|---|
| P5 training report | `f527ce75992de86bada23aeb0a9ddfced5c580e85ae64e3561d73ce5db4e85d8` |
| P5 checkpoint | `d34c1b9ea9674f4b7d7a2dd41ca40d872bdc338b5d30092f7cc997c41d3e7445` |
| P5 train-only inference preflight | `cd5e51f4634a60a08e8fb2357c4723209346996cc1b42ab9e2ced8c077cc1f91` |
| P5 training GPU preflight | `bc563971cf4337d90c423e6964a3982650b0e5d3a8041cb7c91871edf3cf970e` |
| P5 training row bindings | `7dbad1042d1476511e20f47f3493db0691ada5092c39cb6f8ad7ad97330fdba2` |
| P5 trainer | `8817ee863eaf825ef6aa3e145ac257ccee195986aaafa0a016e6cc95b76c3c42` |
| P5 training config | `914f5c8f6b52b317eabc0eb2a3a2814196a10214e5706a52edbedf54b368d5e7` |
| P5 preflight runner | `72dfd36e257ba6f5033a31e9166e55eec424eb2acc957ef56e64ec53655c1239` |
| P5 preflight test | `37e8239ff28a42b6afb92b12ec03a60450bd09a1ed9ac64b2972a29f534ddebf` |
| P5 weighted-loss test | `9076c5a9d1956803654ef0e582afaa01831586641b59313b6e2c499c2dd1b658` |
| P5 preflight config | `5f2e4293e86ac483f513226153659fa99939c3f51082b8e6fd21e1829c582a62` |
| P5 paired report | `cf85c09e2461b8e75277860ef4f9e0bf82b07440b0e85e1a74effae71d9fe500` |
| P5 paired preflight | `09a69da5f6e7791f5fc3fad2e24d16645bd6d063583278b5d157aade7e9e9d40` |
| P5 paired overlap audit | `9da9a546d1caf78b08eb2551d9408fc3c737de3adcaa85e42fbd390301a3823e` |
| P5 paired proposal scores | `58baf946e4dcb59856d0820f23aa1ac04c0c3a358c79cd3183e165c40595c7f3` |
| P5 paired evaluator | `1dd3ada4bd9fdd9de33b71c72c604c05b8fe637c8561b822e82834c413f455b4` |
| P5 paired evaluation config | `66143d9ea5a0950332db7db2626a9672af03d558d1f188125f36b46c1802367a` |

## Commands and tests

```powershell
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/p5_train_only_hard_negative_preflight.py')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/train_classifier_p5.py')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/train_classifier_p5.py', '--train')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/evaluate_p5_probe.py', '--preflight')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/evaluate_p5_probe.py')
```

Seven focused unit tests passed across train-only hard-negative admission and weighted-loss row selection. `py_compile` and `pyflakes` passed for the P5 trainer and v3 paired evaluator. Runtime emitted non-fatal MIOpen kernel database warnings.

## Commit scope

Recommended P5 source files to commit:

- `p5_train_only_hard_negative_preflight.py` and `test_p5_train_only_hard_negative_preflight.py`
- `p5.json`, `train_classifier_p5.py`, `test_train_classifier_p5.py`, and `p5_train.json`
- `evaluate_p5_probe.py` and `p5_probe_eval_v3.json`
- `RESULT.md`

`PROTOCOL.md` may be omitted because this result record contains the executed recipe and evidence. The v1/v2 evaluator snapshots and config snapshots are historical scratch copies and may also be omitted; their failure receipts are kept as project-local audit artifacts, not required source. Generated checkpoints and reports remain under `artifacts/` and are not source files.
