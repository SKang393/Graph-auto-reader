<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Peripheral label layout repair

This is a synthetic development diagnostic. Goal 22 and production approval remain incomplete.

Participant names crossed axis numbers, axis titles touched ticks, and condition codes touched phase headings. The opt-in transform moves only these labels to empty pixels within the corresponding panel margin or phase header. It preserves text, roles, points, calibration, arrows, legends and marker masks. Ambiguous bindings and layouts with no room remain unchanged and reported.

Across 23 authenticated sources, 39 participant names, 26 axis titles and 15 condition captions move. All 27 development-label moves have clear placements. Thirteen training cases remain unresolved: four ambiguous phase bindings and nine without space in the allowed area. No labels are dropped.

The evaluator uses a separately versioned input profile for the three layout transforms. The earlier two-transform profile retains its exact source inventory. Every panel is compared byte-for-byte against the declared full-image crop after production decoding. Historical panel and axis geometry is explicitly replayed, so this does not establish end-to-end acceptance.

| Measurement | Train before | Train after | Development before | Development after |
| --- | ---: | ---: | ---: | ---: |
| Labels in truth | 709 | 709 | 183 | 183 |
| Matched labels | 637 | 648 | 161 | 169 |
| Extra labels | 10 | 11 | 19 | 19 |
| Missing labels | 72 | 61 | 22 | 14 |
| Exact readings | 405 | 423 | 146 | 158 |
| Correct roles | 412 | 410 | 141 | 139 |
| Character edits | 1335 | 1298 | 235 | 128 |

Both fixed-model CPU runs complete all 37 panels. The baseline reproduces 827 earlier outputs exactly. The before/after runs take 49.54/37.27 seconds; scoring takes 39.00 seconds. The source-layout check takes 459.95 seconds.

- train: 28 exact-reading gains and 10 losses; 21 role gains and 23 losses. All 709 labels remain in the comparison.
- validation: 23 exact-reading gains and 11 losses; 13 role gains and 15 losses. All 183 labels remain in the comparison.

Raw development detection precision is 0.7868, recall 0.8470. Raw detection and assembled-output metrics are reported separately. The unchanged central bars grade all labels, including misses and regressions.

Existing development comparisons: {"detection_precision": false, "detection_recall": false, "exact_text": false, "character_error_rate": false, "role_accuracy": false}.

Validation: 13 layout tests and 47 evaluator checks pass; compilation has zero warnings and errors. The initial one-test failure was an expected-message mismatch after private input was correctly rejected, and its log remains preserved.

No training, private/sealed evaluation, new model revision, production activation, packaged build or public release occurred. The retained portable is still build 433, version 0.4.33. Source and runtime snapshots preserve reproducibility.

Next: diagnose remaining reading and role errors, and test conservative recovery of missed tick regions using existing pixel components and the unchanged recognizer. A saved-component investigation is diagnostic only; broad peripheral recovery adds too many false proposals.

Evidence and source hashes: [aggregate record](GOAL-22-PERIPHERAL-TEXT-CLEARANCE.json).
