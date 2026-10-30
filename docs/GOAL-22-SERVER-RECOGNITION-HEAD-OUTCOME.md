# Goal 22 server recognition head outcome

**Status:** Both mobile-detector and V44-matched pairs are scored as synthetic diagnostics. The trained head is not promoted.

## Scope and cohort

This is a project-owned synthetic development experiment. The training split contains 28 panels and 709 truth regions; the source-disjoint dev split contains 9 panels and 183 truth regions. The full cohort has 23 sources, 37 panels, and 892 truth regions. The V44 development cohort had earlier development use, so the current V44 rerun is not a fresh independent holdout.

No private or sealed case inputs were read. Earlier historical aggregate sealed metrics surfaced during review and were not used for this experiment. Production approval, model activation, release eligibility, and Goal 22 completion remain false.

## Training and GPU provenance

The fixed recipe `server-crop-head-ctc-v2` trained 616 train-only examples for 500 AdamW steps with seed 2201. The optimizer ran on the AMD Radeon RX 6600 XT through `torch.cuda` with ROCm 7.16.26385 and `cpu_fallback:false`. The resulting patched ONNX SHA-256 is `564334524eb3a913a3cc20c22972f3959ecb9beca1b15e41757ffd69afd91afd`; only `linear_9.w_0` and `linear_9.b_0` changed from the original recognizer.

Frozen-trunk feature extraction used ONNX Runtime DirectML 1.24.4 on the same RX 6600 XT, PCI vendor/device `0x1002:0x73FF`, with CPU fallback disabled. The training receipt binds 17 width-specific profiles, 616 model runs and 616 DML node events, with zero CPU node events. Profile-set SHA-256: `af847d90688c85fd4cfbb1e7de50ba443ad5f485d41eab886dabb57b1628ff3e`. The receipt also notes ROCm CTC backward is not bitwise deterministic, despite a fixed seed and row order.

## Mobile detector paired score

The baseline and trained arms use the same PP-OCRv5 mobile detector (`d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb`) and the same production runtime path. Each arm completed all 37 panels with no failed panels. On the 9-panel dev split, detector geometry was identical. Recognition and role metrics were lower for the trained head:

| Dev metric | Baseline | Trained head |
|---|---:|---:|
| Raw detection precision / recall | 71/208 = 0.3413 / 71/183 = 0.3880 | same |
| Exact text | 66/183 = 0.3607 | 56/183 = 0.3060 |
| Character error rate | 1007/1019 = 0.9882 | 1013/1019 = 0.9941 |
| Role accuracy | 58/183 = 0.3169 | 49/183 = 0.2678 |

The documented policy requires 0.95 exact text, at most 0.05 character error rate, 0.95 role accuracy, and 0.95 detection precision and recall. Both arms fail those bars. The trained arm is worse on all reported recognition and role metrics. The scorer is diagnostic and does not select or approve a candidate.

The baseline and trained score JSON files, candidate bindings, runtime reports, source hashes, and acceptance policy hash are recorded in the companion JSON. Score output hashes are `235aea18222bbd07bc171a21067cb3d2856defc8d22e37c440ba1d11ebed5b12` and `096e57a6369222602100582bc51353b2ee8212a7559095a15775f9d3ff20e9ab`.

## V44 matched-detector pair

The V44 detector pair uses `graph-text-extent-db-head-v44-p1` (`615495cc882b6ae8633081d89c3ab08c0d4a80a04aa36bbde4157fc7828f5923`) with the original recognizer in the baseline arm and the trained head in the candidate arm. Both runtime reports say `panels_completed`, with 37 completed and zero failed panels. Their hashes and candidate bindings are in the companion JSON.

The paired score receipt is `d3969808c6808114a42eea4e873e515bd28a24af2ddcf9ac07b1ad59e46f1a49`. Both arms use the same V44 detector and have identical detection geometry.

| Split and metric | Baseline | Trained head | Trained minus baseline |
|---|---:|---:|---:|
| Dev exact text | 124/183 (0.6776) | 122/183 (0.6667) | -2 correct |
| Dev character errors | 626/1019 (CER 0.6143) | 627/1019 (CER 0.6153) | +1 error |
| Dev role accuracy | 116/183 (0.6339) | 115/183 (0.6284) | -1 correct |
| Dev geometry | TP 139, FP 48, FN 44 | TP 139, FP 48, FN 44 | unchanged |
| Train exact text | 423/709 (0.5966) | 465/709 (0.6559) | +42 correct |
| Train character errors | 1346/3268 | 1217/3268 | -129 errors |
| Train role accuracy | 439/709 (0.6192) | 479/709 (0.6756) | +40 correct |

Both arms fail the documented dev bars for exact text, CER, role accuracy, and detection precision/recall. Training-split gains do not carry over to dev. The final score receipt confirms the same V3 validation binding was previously used for a V44 score, so this is not independent confirmation. There is no promotion. The scorer reused the completed runtime reports and did not run inference or optimization; the first trained-score invocation failed closed on a malformed binding hash without writing output, then the corrected invocation completed without overwriting an output.

## Restoration and cleanup

The authenticated synthetic panel restoration receipt records 37 panels recreated from the bound request: 28 train and 9 dev, with no inference or optimizer run. Its request SHA-256 is `1586f7fd28ffbc6024e07fb0b12b2fb79f0d4dc2dc790aba2d963813b0686675`; the receipt hash is recorded in the JSON.

Under the user-authorized cache cleanup review, the two completed mobile-pair inference-cache directories were removed. The manifest enumerated 219 files totaling 5,103,930,886 logical bytes; the result records 219 removed files and confirms both evaluation report hashes remained unchanged. The V44 reports and their needed inputs were outside the earlier cleanup scope and remain retained through outcome publication. The review covered regular, non-reparse `.bin` files and found no report or scorer dependency on the cache payloads. The trained ONNX and training outputs remain retained until this outcome is pushed and active consumers close.

## Decision and remaining work

No model is selected, activated, or approved. This synthetic evidence does not satisfy product acceptance and does not replace private/sealed acceptance. The V44 scores and no-promotion decision are recorded. After the outcome is committed and pushed, check active consumers, record actual cleanup totals, and retire remaining disposable run outputs.

The companion JSON contains receipt paths and hashes for the training report, model, feature profiles, mobile pair, V44 pair, panel restoration, cache cleanup review, and cleanup result. Source paths in the JSON are relative to the `server-crop-head-20261029` worktree. Run artifact paths are relative to `E:\Graph-auto-reader`. The full OCR evaluation reports do not record an execution-provider field, so this record makes GPU claims only for optimizer training and frozen-trunk extraction, as bound by the training receipt.
