# Width-8 local cluster-split diagnostic

## Result

This is an exploratory train-source-only synthetic diagnostic. The scanner's width-8 baseline stayed fixed; the prototype only attempted to split over-width contiguous hit clusters at support valleys. It uses raster pixels and the frozen selected axis at inference, with no text labels supplied to the proposal step. No production code/default changed, no model was trained, and no GPU was used.

The source-group rule was frozen on fit-only labels: consider support prominence thresholds 1, 2, 3, and 4 dark samples; select the highest train text-target F1 among candidates whose precision is no more than 0.02 below the width-8 fit baseline, tie-breaking on precision and then larger prominence. The selected prominence was 4.

Fit split: 7 source IDs, 74 positive text targets and 79 visible stroke truths. Width-8 baseline matched 35/74 targets from 552 proposals (514 false, 0 ambiguous, target precision 0.06341, target recall 0.47297, F1 0.11182). The selected splitter matched 36/74 from 582 proposals (543 false, 0 ambiguous, target precision 0.06186, target recall 0.48649, F1 0.10976). It gained one target match while adding 30 proposals and 29 false proposals. It produced 38 proposals on 6 panels with no visible truth, versus 28 baseline proposals.

New validation split: 7 source IDs across 4 complete source/hash components, 61 positive text targets and 65 visible stroke truths. Both baseline and splitter matched 28/61 text targets and 30/65 visible strokes. Baseline had 156 proposals (124 false, 2 ambiguous, target precision 0.17949, recall 0.45902, F1 0.25806); the splitter had 160 proposals (128 false, 2 ambiguous, target precision 0.17500, recall 0.45902, F1 0.25339). Both made 25 proposals on 9 panels with no visible truth. Per source, only groups `27103103` and `27103116` gained two proposals each, with no additional matches; the other validation groups were unchanged. The fit recall gain did not transfer to validation.

Validation groups were frozen by salted hash order over whole components and were disjoint from the earlier width-study and center-correction validation groups. Candidate-fit sources were `27103101`, `27103105`, `27103109`, `27103110`, `27103115`, `27103117`, and `27103119`. New validation sources were `27103102`, `27103103`, `27103107`, `27103108`, `27103114`, `27103116`, and `27103122`. Prior validation labels were not read. This is not product acceptance evidence.

## Provenance

| File | SHA-256 |
|---|---|
| `cluster_split_experiment.py` | `ed8b3419b6aca9a8c2ac19fcc92c71df3099338acddc407c5051d6648bc35bdd` |
| `test_cluster_split_experiment.py` | `14c8e4e74a33f385090c95c624ba1d30deaf7fd14a95bfd172d2387c2844e39c` |
| Frozen fit rule | `46408afdb0ae08a7bd6f429d79a3e9d37979119e9486a9646582833d7d5c7706` |
| Truth-blind validation proposals | `3fd372560b7f0f674ada0f49608c9089024f3fed2a0142443019037d6f0d2cad` |
| Final report | `574b906078da66f5409d8d8c6439617781e534c545ff023a5463ee96dec36ee6` |
| Prior v2 width report | `a08da3e040112e4476777c7c7d4e21b4882e1f32e1f2a37209bebd5b84984e83` |
| Prior fit proposal rows | `e3d1476b8264e61e35c1187e5d7851cd5abf2f5ff30e889043a8aa0d9eee99ca` |
| V6 train manifest | `12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a` |
| V6 curation manifest | `aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672` |
| Center-study report used only to exclude its validation IDs | `4957523a6d955f3cb6194daa9d294b9b45af99a19a9d8780f27875dfca2a56d7` |

The report also stores hashes for the executed scanner helper dependencies. The initial attempts stopped before output creation: one had a report-hash typo, one exposed a linked-component exclusion bug, and one used exact float equality for geometry parity. The final run used corrected component exclusion and 1e-6 geometry tolerance. That parity check matched the frozen width-8 output before candidate scoring.

## Verification

`pyflakes` passed on the experiment and test files. Five focused tests passed: source/hash-component split exclusion, support-valley splitting, no split without a valley, frozen width-8 parity, and absence of probe/dev/private input paths. Final command: `E:\Graph-auto-reader\tools\gpu-training\venv\Scripts\python.exe -m ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.scanner_cluster_split_v1.cluster_split_experiment`.
