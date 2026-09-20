<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Goal 22: synthetic non-data context repair

The historical generator adds an unlabelled open circle to a data line, then
calls that circle a negative `legend_symbol`. It also scatters standalone
letters, triangular arrowheads without shafts, and tick-like strokes without
axes. Their names do not supply visible evidence that they are non-data.

The explicit `synthetic-contextual-negatives-v1` profile replaces this injection
with sampling labels on the graph's existing text, named legends, axis ticks,
arrows, brackets and phase structures. It preserves the entire native graph.
The historical source documents, renderer, images and failed results stay
unchanged. The profile accepts owned train/dev scenes only. It is not registered
for training, sealed evaluation or production approval.

## Complete inventory verification

All 23 original source PNGs were reproduced before comparison. All native
annotations and marker masks remain identical in the corrected derivatives.
The same physical-source V3 rendering and degradation are retained.

| Inventory | Train | Development | Total |
| --- | ---: | ---: | ---: |
| Sources | 20 | 3 | 23 |
| Physical panels | 30 | 9 | 39 |
| True points, all preserved | 500 | 206 | 706 |
| Historical injected requests replaced | 180 | 27 | 207 |
| Native contextual negative samples | 1,362 | 349 | 1,711 |
| Negative candidates omitted for true-marker clearance | 46 | 13 | 59 |

An omitted negative candidate is still present in the image and its native
annotation. It is excluded only from negative sampling. For example, all 39
native arrowheads touch actual data markers, so those overlapping heads must
not be taught as negative point locations. Five training scenes have an injected
legend circle whose shape and fill match a true marker. This establishes a
generator defect; it does not quantify the effect on previously learned weights.

Generator verification takes 55.62 seconds. All **38 contextual-profile and
existing renderer-regression tests pass**, in 73.55 seconds. The initial test
fixtures used an unsupported tick property, wrong family name and too-specific
error text; these were corrected without weakening schema validation. The
inventory initially rejected its historical `validation` label. The tool now
requires an explicit mapping to the already documented development role.

## Frozen workflow comparison

The same application assemblies, OCR models, marker V27 weights, classifier,
operating thresholds and explicit unapproved enclosure supplement run both
inventories. No geometry, labels, calibration or point answers enter inference.
The complete 706-point and 724-row reference remains unchanged. Only raster
hashes and runtime source IDs differ in the evaluator input.

| Result | Historical pixels | Corrected pixels |
| --- | ---: | ---: |
| Completed sources | 11 / 23 | 14 / 23 |
| Correct unique point values | 226 / 706 | 287 / 706 |
| Correct relational rows | 218 / 724 | 251 / 724 |
| Missing unique points | 480 | 419 |
| Extra unique detections | 46 | 46 |
| Wrong matched phases | 5 | 31 |
| Wrong matched numeric values | 0 | 0 |
| Runtime | 166.33 seconds | 160.67 seconds |

All three newly completed sources are training sources. The corrected inventory
completes 13/20 training sources and 1/3 development sources. Nine sources still
fail calibration. Increased export coverage exposes 16 phase errors in the newly
completed three-panel source; a previously completed development source also
gains ten wrong phases. These regressions remain counted. This is a generator
comparison, not an improvement to weights or a passed model gate.

The first workflow attempt failed pre-inference because its protocol omitted
the exact train/dev manifest fields. The invalid attempt and exception are
retained. The repaired attempt supplies the existing schema, without changing
runner validation or candidate authorization. No sealed budget was consumed.

## Verification and next work

Commands: `python -m pytest ml/synthetic/tests/test_contextual_negatives.py
ml/synthetic/tests/test_repair_regressions.py -q -p no:cacheprovider`, the
hash-bound contextual inventory audit, the frozen synthetic workflow runner and
the frozen CSV scorer. The [evidence record](GOAL-22-CONTEXTUAL-NEGATIVE-REPAIR.json)
binds source snapshots, reports, both input inventories, the failed attempt and
the full metrics. The preceding `ce32224` checkpoint passes Windows CI.

The original Apache-2.0 classifier source checkpoint was located in the main
checkout and authenticated against its recorded checksum. On all 2,160 cached
owned diagnostic patches, its PyTorch source agrees with the approved ONNX
payload within 0.00000293, below the existing 0.00001 tolerance, with zero shape,
fill or artifact-decision disagreements. This preserves an exact starting point
for a separately defined coverage repair; no optimizer ran.

Next: trace the exposed phase errors and remaining calibration failures using
saved original-pixel evidence, then continue the classifier coverage repair.
Do not relax origin/unknown-value safeguards or silently merge differently
classified series. All new code is Apache-2.0; existing licensed dependencies
and weights are unchanged. No private/sealed read, formal model revision,
production activation or package occurred. Build 433 remains 0.4.33. All four
Goal 22 outcomes remain incomplete.
