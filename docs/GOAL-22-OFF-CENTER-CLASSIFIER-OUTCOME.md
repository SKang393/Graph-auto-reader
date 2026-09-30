<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Off-center classifier adaptation rejected, 2026-09-30

V9 passes unchanged patch-development bars but makes native graph reading
worse. Keep the V27 center model and V5 classifier. No production switch,
threshold change or sealed run follows this result.

The fixed 12-epoch recipe starts from the retained V5 checkpoint. It preserves
all 69,424 V8-cache training rows and adds 6,912 authored off-center negatives.
Their crops are six pixels from the authored target and more than five pixels
from every authored center, using the same original synthetic rasters. No model
prediction selects a crop. All 3,136 dev rows remain byte-identical. Architecture,
losses, seed, optimizer, temperatures and the 0.5 artifact cutoff are unchanged.
Only the final checkpoint is evaluated.

| Measure | V5 baseline | V9 candidate |
| --- | ---: | ---: |
| Patch shape accuracy | 97.64% | 98.26% |
| Patch fill accuracy | 99.44% | 99.65% |
| Native correct markers | 726/838 | 675/838 |
| Native false markers | 25 | 45 |
| Native precision | 96.67% | 93.75% |
| Native recall | 86.63% | 80.55% |
| CSV source exports | 12/23 | 11/23 |
| Correct unique CSV values | 409/706 | 387/706 |
| Correct CSV rows | 405/724 | 384/724 |
| Dev correct CSV values | 156/206 | 159/206 |
| Dev correct CSV rows | 156/224 | 159/224 |

V9 retains 660 earlier correct markers, recovers 15 and loses 66. Sixty lost
markers are crosses. Eleven recorded centers change from acceptance to artifact
rejection, with median center error 3.95 pixels. The other 55 old centers are
absent from the new recorded classified-marker list; the saved evidence does
not identify whether recovery omitted them or classification rejected them.
Do not treat that missing detail as a proved mechanism. Further diagnosis must
separate crop geometry, recovery proposals and rejection before another recipe.

Every source, failed export and truth remains in the comparison. All 31 native
and 39 CSV panels are observed. OCR is unchanged at 348/453 exact readings,
axis corners remain 84/93, and the CSV audit reports zero upstream OCR/axis
changes. Train CSV values fall from 253 to 228/500; dev values rise by three.
Both dev exports remain complete, while train exports fall from ten to nine.
Wrong-phase rows fall from three to two because fewer rows survive. This is
not a phase-reasoner improvement. No wrong-scale, duplicate or failed-source
residual rows are emitted. All observed model providers are CPU.

All 160 preparation tests pass: eight fresh runner tests and 152 reused data
tests whose sources and results were reverified. Cache validation preserves
earlier pixels/targets and dev bytes. Training performs 7,164 optimizer steps
in 558.34 seconds, or 564.94 seconds including its launcher. ONNX parity covers
all 79,472 train/dev tensors, with maximum error 5.9604644775390625e-6 and no
changed shape, fill or artifact decisions. Native and CSV inference take
132.16 and 240.36 seconds. The maximum completed training work/rest cycle is
79.995%; it is not an instantaneous per-core guarantee.

The canonical patch result remains `dev_pass`. The complete outcome is
`failed_dev_unconsumed`. Preregistration, authorization and outcome close in
one revision commit, preserving all 91 earlier ledger entries. Initial launch
deferred training because the independent calibration check needed a compiler
analyzer repair; it performed no optimizer steps. The subsequent run completed
once. No private/sealed data, new dependency, external weights, model approval,
packaged build or release. Owned source, checkpoint and synthetic data retain
Apache-2.0 provenance. Build 433 remains version 0.4.33.

Evidence is in the [aggregate record](GOAL-22-OFF-CENTER-CLASSIFIER-OUTCOME.json)
and [revision outcome](../ml/markers/classifier/off_center_context_v9/P1_RESULT.json).
Goal 22 remains incomplete. Continue the calibration defect repair and diagnose
the remaining OCR and marker failures from open synthetic evidence.
