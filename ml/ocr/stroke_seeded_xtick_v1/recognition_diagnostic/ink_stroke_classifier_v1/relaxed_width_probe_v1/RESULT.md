# Frozen P3/P5 relaxed-width probe result

This is exploratory evidence on the already-consumed synthetic development probe, not acceptance or product approval. The scanner change is fixed at `maximum_cluster_width_px=1,000,000`; all other scan settings, crop geometry, models, and classifier threshold (`0.5`) remain fixed. Inference used the project launcher on AMD Radeon RX 6600 XT with CPU fallback disabled. Truth geometry was read only after both score passes finished.

## Truth-blind admission

The regenerated scanner reproduced all 365 frozen baseline proposals exactly, then generated 442 relaxed-width proposals across 16 source groups and 24 panels. Candidate crop hashes yielded 386 unique images; all 56 repeated rows were retained. Nine rows overlapped both the P3 training crop hashes and the P5 training crop hashes and were quarantined, leaving the same 433 hash-clean rows for both models. P3's 446 training image hashes were verified as a subset of P5's 465 training image hashes. Holdout crop overlap was zero. Probe/training and probe/holdout source IDs, source identities, and full-panel pixel hashes had zero overlap. Holdout image and annotation bytes, private data, and sealed data were not read.

## Paired metrics

The full denominator is 181 visible ticks. Metrics below are on the 433 eligible rows; duplicate rows remain present.

| Scorer | Proposals | Matched | Unmatched | Precision | Recall | False proposals on no-visible panels |
|---|---:|---:|---:|---:|---:|---:|
| Raw relaxed-width scan | 433 | 94 | 339 | 0.21709 | 0.51934 | 37, on all 10 panels |
| P3 at 0.5 | 99 | 78 | 21 | 0.78788 | 0.43094 | 2, on 2 panels |
| P5 at 0.5 | 105 | 82 | 23 | 0.78095 | 0.45304 | 8, on 3 panels |

Both classifiers cut unmatched proposals sharply while losing some of the raw scan's matched ticks. P5 retained four more matches than P3, with two additional unmatched proposals and more false proposals on no-visible panels. There were 22 axis-uncertain panels; 94 of P3's 99 selected proposals and 101 of P5's 105 were on those panels. Full per-source results are in `paired_report.json`.

## Bound source and artifact identities

| File | SHA-256 |
|---|---|
| Evaluator | `04b183c38966eb5ce5dfb84b87361f205a877135715bf48fc3b4709df83c4788` |
| V2 config | `a67c4e566da28e862e833d5c87b6cb095345714932baf9a8715334e9e34c93fe` |
| Focused test | `04a5b0cc2f3144a24e0099224663b4e343983f7909a6430c6d0a5bde07f04ac9` |
| Preflight receipt | `c4f100b6557764623a1bda298918ba6d01bf3695da74e5a55e091ea004ebf087` |
| Candidate manifest | `efb3da7ee27baaea3ffa10b2202c0419d174ffea3b375667264de378307ad1cd` |
| Fresh overlap audit | `3b2b14340cb0dc4c40b5e0d8417cc83f226c14d32b1c6efbdec87f23d7d01056` |
| Paired report | `5612b128c796c224084493b3e4890cc6f5c0a22eb5d36fa1472ddd62c952d172` |
| P3 checkpoint | `8b1f8ce7d9897e3db19e71a4f21576b92e51ae56a90aa1fec9fb1df364820c92` |
| P5 checkpoint | `d34c1b9ea9674f4b7d7a2dd41ca40d872bdc338b5d30092f7cc997c41d3e7445` |
| P5 paired evaluator dependency | `1dd3ada4bd9fdd9de33b71c72c604c05b8fe637c8561b822e82834c413f455b4` |
| Relaxed-width diagnostic source | `32662ad05c220de6a9a5fce451f5101baab32ce4ac6156f1ba4b58d3266b834a` |
| Prior relaxed-width diagnosis | `10f1282c134285070c9fcbc29d09fb8d2c46d1fe27ccac2d1c31d9592077d1d8` |
| Probe manifest | `127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba` |
| Frozen raw baseline scanner proposals | `0297966c41243bd4a27a0c1fa0aa7e849fc8beb5fa7a5dc177fc7e9029cb6081` |

P3 metadata binds the 60-epoch source-uniform checkpoint and its training manifest. P5's training dependency provenance limitation remains as described in `../fine_tune_p5/RESULT.md`; evaluation source and checkpoint bytes were hash-verified for this run.

## Verification and commands

Ten focused tests passed, including synthetic wide-cluster recovery, hash/source overlap handling, coordinate conversion, and synthetic rows through both pinned P3 and P5 batch adapters. `py_compile` and `pyflakes` passed. The GPU run emitted non-fatal MIOpen database warnings.

```powershell
& .\tools\gpu-training\venv\Scripts\python.exe -m unittest ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.ink_stroke_classifier_v1.relaxed_width_probe_v1.test_evaluate_relaxed_width_probe
& .\tools\gpu-training\venv\Scripts\python.exe ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/relaxed_width_probe_v1/evaluate_relaxed_width_probe.py --preflight
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/relaxed_width_probe_v1/evaluate_relaxed_width_probe.py', '--run-inference')
```

The first v1 inference attempt failed before either model scored and before truth access because generated rows used `crop_pixels` instead of the pinned P3 adapter's required `pixels` field. Its failure receipt is `artifacts/stroke-seeded-xtick-v1-relaxed-width-p3-p5-probe-v1/inference_failure.json` (SHA-256 `4b91f0b0b8c1e6e729649cd8e5cd1ee144c5ee150cc3dac9af87c0bd38fb10db`). V2 added actual P3 and P5 adapter-batching tests before the successful paired run. V1 scratch config and generated artifacts are not required source files.

## Scope and commit files

Commit only the isolated evaluator, `relaxed_width_probe_v2.json`, its focused test, and this `RESULT.md`. The v1 config, failure receipt, candidate manifests, preflight, audit, and paired report are historical or generated evidence under `artifacts/`. No training, production activation, or acceptance claim is made.
