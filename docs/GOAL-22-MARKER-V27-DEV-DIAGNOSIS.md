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
This follow-up remains pending and cannot authorize production acceptance.
