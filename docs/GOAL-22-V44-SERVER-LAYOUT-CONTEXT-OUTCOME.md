# Goal 22 V44 server layout-context paired diagnostic

**Status:** Synthetic diagnostic only. No model is selected, promoted, or approved. Goal 22 remains incomplete.

## Scope

This paired evaluation compares the original server recognizer under the baseline composition with the same recognizer under `original-db-head-legend-context-v1`. Both arms use the same V44 detector, recognizer, runtime, execution assemblies, production detector postprocess, and 37-panel source/crop inventory. The paired score confirms raw detector regions are identical by ID and geometry. The downstream predicted-region geometry can still differ because the candidate composition enables additional assembly and bounds-refinement steps.

The project-owned synthetic cohort has 20 train sources, 28 train panels, and 709 truth regions; dev has 3 source-disjoint sources, 9 panels, and 183 truth regions. All 37 panels completed with zero failures. The V44 dev cohort had prior development use, so this rerun is not an independent holdout.

No private or sealed data was used. The evaluation report records `private_data=false`, `sealed_data=false`, `truth_used_by_runtime=false`, zero optimizer steps, and no production approval. This document records completed runtime evaluation output; no inference or training was run while preparing it.

## Paired results

| Split and metric | Baseline | Layout-context composition | Change |
|---|---:|---:|---:|
| Train exact text | 423/709 | 412/709 | -11 correct |
| Train character errors | 1346/3268 (CER 0.4119) | 1341/3268 (CER 0.4103) | -5 errors |
| Train role accuracy | 439/709 | 447/709 | +8 correct |
| Train geometry | TP 624, FP 19, FN 85, predictions 643 | TP 618, FP 9, FN 91, predictions 627 | TP -6, FP -10, FN +6 |
| Dev exact text | 124/183 | 136/183 | +12 correct |
| Dev character errors | 626/1019 (CER 0.6143) | 339/1019 (CER 0.3327) | -287 errors |
| Dev role accuracy | 116/183 | 144/183 | +28 correct |
| Dev geometry | TP 139, FP 48, FN 44, predictions 187 | TP 154, FP 20, FN 29, predictions 174 | TP +15, FP -28, FN -15 |

The candidate improves the paired dev counts, but it still misses every documented dev bar: exact text 0.7432 versus 0.95 minimum; CER 0.3327 versus 0.05 maximum; role accuracy 0.7869 versus 0.95 minimum; geometry precision 0.8851 and recall 0.8415 versus 0.95 minimum each. The acceptance policy is unchanged and its SHA-256 is recorded in the JSON. This is not an acceptance or promotion result.

## Interpretation limits

The `original-db-head-legend-context-v1` composition changes more than one component. It enables participant-lane assembly, inside-plot assembly, pixel-bounds refinement, header-layout resolution, and framed-legend context/recovery. The score therefore does not isolate the legend resolver or any single component. Raw detector regions are identical, while the composed output's predicted-region counts and geometry differ.

The evaluation remains synthetic and diagnostic. The V44 dev set was previously used, the candidate fails all documented dev acceptance bars, and no production, release, or Goal 22 approval follows from this comparison.

## Evidence

The companion JSON records the paired-score receipt, context evaluation report, candidate binding, baseline report and score, capture references, scoring helper, and unchanged acceptance-policy hashes. The earlier [server recognition head outcome](GOAL-22-SERVER-RECOGNITION-HEAD-OUTCOME.md) records the model-training and prior paired evaluations.
