# Goal 22 marker V27 development diagnosis

This is synthetic development evidence, not production acceptance. V27 remains
`failed_dev_unconsumed`. No private or sealed data was read, no optimizer step
was performed, and no threshold was selected.

## Reproduction

The fixed 0.25 operating threshold reproduced the recorded V27 outcome exactly:

| Split | Scenes | Truth markers | True positives | False positives | Missed markers |
| --- | ---: | ---: | ---: | ---: | ---: |
| Component development | 167 | 2004 | 1796 | 238 | 208 |
| Family development | 9 | 206 | 190 | 39 | 16 |

The diagnostic evaluated 270236 proposals and saved 176 prediction arrays.
Elapsed diagnostic time was 8443668.895 ms, approximately 140.7 minutes, under
the low CPU budget alongside OCR training. The five focused tests passed.

## Observed failure sources

Of the 208 component misses, 160 had no positive proposal anchor above the fixed
threshold, 45 were suppressed by non-maximum
suppression, and three were rejected by consensus. Of the 16 family misses,
15 were attributed to classification below threshold and one to suppression.

For component proposals, positive anchors below threshold increased from
683 in V26 to 791 in V27. Truth markers without an above-threshold positive
anchor increased from 169 to 207. Negative anchors above threshold decreased
from 2375 to 2331, but final false positives increased from 224 to 238.
These are different stages and denominators; fewer high-scoring negative
anchors did not establish a better final detector.

Only four component positive anchors decoded beyond five pixels, compared
with five in V26. Both models had zero such family positive anchors. This
supports investigating confidence discrimination before another offset-only
repair, but does not prove a causal training intervention.

Among the 45 component suppression misses, 38 actual suppressors were final
false positives and seven were final true positives. The median applicable
suppression distance was approximately 10 pixels. This identifies an additional
postprocessing interaction; it does not authorize changing the threshold or
suppression rule without separate development evidence and runtime parity.

## Evidence identities

- Diagnostic report: `artifacts/goal22-runs/marker-v27-score-localization/diagnosis-v1/diagnosis.json`
- Report SHA-256: `732c8b9f058c314e5927823a4fe0a45502452c755b12667aaf6566605d166dfc`
- Prediction cache SHA-256: `ea2089262c1b2d0b57a4b8eeb7e576956af462b2d67166e3e3fd05f82e87493a`
- Diagnostic source SHA-256: `3dda4482fdd5df1067718615a72e4acc02ed964d00031af2c59a3f37211b6061`
- V27 ONNX SHA-256: `4979777a404be48c6c75b490218d1de31d2edb2550f5bc71c7aa6894c1bcca04`

The report authenticates the frozen proposals, patches, V25/V26 caches,
V27 outcome, model, configuration, protocol, and source dependencies.
It replays the frozen Python development postprocessor. It does not establish
C# runtime parity or any real-image acceptance result. The generated cache
and full report remain local artifacts; this document records aggregates only.

Independent review verified all 176 cache arrays, their hashes and finite
float32 shapes, all ten input path/hash bindings, and the four dependency
hashes. It confirmed the full denominators and aggregate attribution.

Before selecting another training revision, replay the saved outputs with
confidence from V27 and geometry from V26, then the reverse combination, at
the same fixed threshold on both complete development splits. Paired truth
transitions and positive-anchor score margins can separate confidence ranking
from geometry interactions without another model inference or training run.
The follow-up completed and cannot authorize production acceptance.

## Saved-output confidence and geometry replay

The authenticated replay completed in 2784705.539 ms (46.4 minutes), with
zero model inference, proposal inference, optimizer steps, private reads, or
sealed reads. It consumed fixed synthetic dev scene, truth, and domain metadata from the authenticated V27 reconstruction and
used the saved proposal coordinates and model outputs. Both original models
reproduced their recorded counts exactly at the unchanged 0.25 threshold.

| Confidence / geometry | Component TP / FP / FN | Family TP / FP / FN |
| --- | --- | --- |
| V26 / V26 | 1837 / 224 / 167 | 194 / 47 / 12 |
| V27 / V27 | 1796 / 238 / 208 | 190 / 39 / 16 |
| V27 / V26 | 1795 / 242 / 209 | 191 / 37 / 15 |
| V26 / V27 | 1840 / 220 / 164 | 193 / 48 / 13 |

The component denominator remains 2004 truth markers across 167 scenes; the
family denominator remains 206 across nine scenes. Replacing V27 geometry
with V26 geometry changes component true positives by minus one. Replacing
V27 confidence with V26 confidence changes them by plus 44. Against V26,
the V27-confidence/V26-geometry hybrid loses 87 formerly matched truths and
recovers 45 missed truths. These observations point primarily to confidence
ranking in the component regression, rather than an offset-only defect.

The hybrids break learned coupling between output heads. They are diagnostic
counterfactuals, not deployable candidates, and do not establish the causal
effect of a future training change. Family results show a precision/recall
tradeoff, so the component improvement must not be generalized into a claim
of passing both development splits. No threshold or new revision was selected.

- Report: `artifacts/goal22-runs/marker-v27-head-swap/diagnosis-v1/diagnosis.json`
- Report SHA-256: `613b9bd9217bcc5218e0cdfbc199ca7c73ea835da21fea5d5f1bfa690627810d`
- Replay source SHA-256: `568f7be7ef1750a196c925abe3843f44a95b89ec32a85393c01065143936d826`
- Focused tests: six passed; compilation and helper self-test passed.

Independent review verified all ten input bindings, exact original counts, full denominators, paired-transition conservation, and confidence-margin accounting. The reconstruction also builds historical train/base structures, although this replay consumes only the development scene data. V27 remains failed-dev and unconsumed; neither hybrid is approved.

## Cached component cohort attribution

A subsequent saved-output analysis conserved all 2,004 component truths:
1,746 remained matched, 91 lost their match, 50 gained a match, and 117
remained missed. The reviewed second replay completed in 10.55 seconds without
inference, optimizer steps, private reads, or sealed reads. Ten focused tests
passed. Both executed helper sources are authenticated before and after the
replay; the ten one-pixel truths are correctly classified as points and all
remain missed. The original diagnostic is retained separately.

The changed-negative selection strata overlap every transition cohort at
100%. This coarse overlap is non-discriminative and does not explain why
previously detected markers were lost. It does not justify another sampler
revision by itself.

The largest substantial morphology loss cohort was horizontal cross
rectangles: 31 of 236 formerly matched truths lost their match (13.1%).
Diameter-27 and diameter-6 cohorts lost 10/85 and 9/90 respectively. The
diameter-40 cohort lost 3/6, but six observations cannot support a broad claim.
These are descriptive development findings, not selected thresholds or proof
of a training cause.

- Report: `artifacts/goal22-runs/marker-v27-cohort-attribution/diagnosis-v2/diagnosis.json`
- Report SHA-256: `945ad94a565634e51bd9f5404b0fc67ae0347238682da3914cfb244eb8b649d1`
- Source: [diagnose_cohorts.py](../ml/markers/center/component_diversity_v27/diagnose_cohorts.py)
- Source SHA-256: `b856c5ce67a23eae4414c32788ddf0333649bcae7bef99343a4f66508e352f2e`

No model, revision, threshold, production approval, or packaged build changed.
