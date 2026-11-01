# P4 training and paired probe result

## Training

P4 completed 20 epochs / 460 optimizer steps on the AMD Radeon RX 6600 XT only. The project launcher reported `cuda:0`, HIP 7.16.26385, and `cpu_fallback: false`. Train loss changed from 1.4881791 to 0.2541699. The trainer does not record exact in-process wall time. Start-to-report-file-write timestamps span about 16.1 seconds, which is only a rough run-time proxy.

P4 train-fit at 0.5: 68/89 positives detected, 14/376 negative false positives, 362 true negatives. P3 train-fit was 62/81 positives and 15/365 negative false positives. These sets differ by the 19 P4 additions (8 positive and 11 negative), so the train-fit comparison is descriptive.

## Paired consumed-probe result

This is already-consumed procedural development evidence, not acceptance. Both models were scored at threshold 0.5 on the same 357 eligible proposals, with the full 181 visible-tick denominator and duplicate proposal rows preserved.

| Candidate | Matches | Proposals | Unmatched | Precision | Recall | False proposals on 10 no-visible panels |
|---|---:|---:|---:|---:|---:|---:|
| Frozen P3 | 67 | 78 | 11 | 0.85897 | 0.37017 | 0 |
| P4 | 67 | 82 | 15 | 0.81707 | 0.37017 | 3, across 2 panels |

P3 parity against its previous report passed. P4 added four proposals without adding matches. It was not promoted.

## Input audit and preflight corrections

The fresh audit checked all 465 P4 training crop hashes against all 365 probe proposal rows and P1 holdout hash metadata. Eight probe rows overlapped P4 training crop hashes and were quarantined; the 357 remaining rows were shared by P3 and P4. Holdout hash overlap, source ID overlap, source identity overlap, and full-panel pixel hash overlap were all zero. All 365 proposal rows are retained in the score file with eligibility flags.

The first preflight failed closed because its expected P4 preflight hash had an extra trailing `1`. The actual hash is `0f2d943793bd084658b42021d717ab4f90d0cb4526682ef6c22589153d6376db`; the P4 report and checkpoint hashes matched. After correcting that, v1 preflight passed. Before inference, the evaluator's output-directory lifecycle issue was corrected by using a distinct v2 output identity. The historical v1 evaluator and config hashes were `3e79803bc41db924f9356ce3c184fb96e636cbaf11afb6330a613ebab361403b` and `8a669c0c9cd2039ed1d0bbf78bd2b53d357d199a33460100130a884684f33a03`. Final v2 preflight and paired inference passed.

## Bound artifacts

| File | SHA-256 |
|---|---|
| Paired evaluation report | `daf4e6c212fbf491b3210e7e17bd9cb326f758efc9c106d035e7afedb70e90b5` |
| Paired preflight receipt | `7dc48690de341fc85536d2fdd694eac6ec8384cd39aae772179d535b10f38de1` |
| Overlap audit | `9da9a546d1caf78b08eb2551d9408fc3c737de3adcaa85e42fbd390301a3823e` |
| Per-proposal scores | `47e92bc8488bb38beb458fb5e700f6405842230ac82946a35723bce96c9e30e7` |
| Paired evaluator | `ea2e04d5440f8b0a8d3abfcf5fa34e13ad0be5ea1ac50ab0b1858f9518c27786` |
| Paired config | `aaab5df1432f212f3628daf55b1cffb93cb7ed212ecd39de3890c900dc572171` |
| P4 checkpoint | `9b924cafd94c079ad866d1b6e79961fe3abab61c3e96fe5efac7c9796b836df9` |
| P4 training report | `f7ad0713ff10866c5ea5f093059aa8e3cad3a932fba6682c7d22b212319ffd35` |

## Commands

```powershell
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p4/train_classifier_p4.py', '--train')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p4/evaluate_p4_probe.py', '--preflight')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p4/evaluate_p4_probe.py')
```

Focused verification passed: P4 training tests (7), P4 paired-evaluator tests (3), plus `py_compile` and `pyflakes` on the paired evaluator. The paired inference emitted non-fatal MIOpen missing-kernel-database warnings; runtime receipts confirm both models remained on the RX 6600 XT with CPU fallback false.
