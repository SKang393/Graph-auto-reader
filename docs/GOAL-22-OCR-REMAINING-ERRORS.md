<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Remaining OCR errors after inside-plot grouping

The completed saved-output diagnostic retains all 709 train and 183 development
truths from the [inside-plot trial](GOAL-22-OCR-INSIDE-PLOT-ASSEMBLY.md).
The same one-to-one geometry matcher reproduces 625 train and 141 development
matches. No new inference, training, private input, or sealed input was used.

| Finding | Train | Development |
| --- | ---: | ---: |
| Truth labels with a box overlapping another truth label | 86 | 20 |
| Matches among those labels | 43 | 12 |
| Truth labels without such overlap | 623 | 163 |
| Matches among those labels | 582 | 129 |
| Extra detections overlapping a truth box below the matching threshold | 17 | 38 |
| Extra detections with no truth-box overlap | 1 | 5 |

Development has 42 missed labels. Of those, 33 have a nonzero overlap with a
detected region and nine have no overlap. Only eight of the 42 misses involve
overlapping truth labels. The generator's label collisions deserve correction,
but they cannot explain all remaining localization failures.

The ten development truth-box overlap pairs comprise seven axis-title/tick
pairs, two participant/tick pairs, and one annotation/legend pair. Positive
rectangle intersection is evidence of crowded layout, not proof that every
intersecting glyph is illegible. The earlier train preflight already recorded
43 such pairs; this diagnosis does not claim they were newly introduced.

Of 141 geometrically matched development labels, 110 have the correct role.
The 31 role errors comprise nine participant labels returned as Other, seven
axis titles returned as Other, three inside-plot legends returned as Annotation,
and twelve annotations returned as PhaseHeading or Other. Existing participant
assembly is a separately validated repair and must not be reimplemented.
Missing legend/arrow context remains distinct from text recognition.

## Completed pixel-bound diagnostic

Some saved proposals contain the correct text but their boxes miss the fixed
IoU 0.5 geometry requirement. Other proposals cover fragments or adjacent labels.
Do not change that requirement or assume every overlapping proposal is padding.

`tools/GraphReader.SyntheticRuntimeEvidence/diagnose_ocr_pixel_bounds.py` tests
one original-pixel hypothesis: remove only empty outer margins of existing
effective boxes, using the existing component detector's full-panel intensity
threshold. It retains all foreground pixels, including noise and nontext
structures, and neither creates nor drops proposals. No text, role, or truth
is used to derive the trial boxes. Original Gray8/BGR hashes and coordinate
mappings are checked before scoring all 892 truths.

The check reports recovered and lost matches separately on train and development.
It uses the same matcher and central 95% precision/recall references; there is
no threshold sweep. Recognition and role scores cannot be inherited after
changing crop geometry. A useful geometry result would still require actual
application OCR validation before integration or any accuracy claim.

The comparison completed in 5.53 seconds and all 13 diagnostic tests passed.

| Geometry result | Train | Development |
| --- | ---: | ---: |
| Full truth count, unchanged | 709 | 183 |
| Matches before trimming | 625 | 141 |
| Matches after trimming | 635 | 152 |
| Recovered matches | 10 | 12 |
| Lost matches | 0 | 1 |
| Precision after trimming | 98.76% | 82.61% |
| Recall after trimming | 89.56% | 83.06% |

The lost match is a faint x-axis digit: its retained dark stroke occupies less
than half the truth rectangle after lighter edge pixels are trimmed. The
threshold is unchanged; this tradeoff is retained in the evidence. Labels
fused with a neighboring label and missing proposals remain unresolved.

Local diagnostic: `artifacts/goal22-runs/ocr-pixel-bounds-diagnostic/run1/diagnosis.json`,
SHA-256 `3a03d5c5cf4bfc74888e0550204dfb770aa6f72de1e5abd9a77c934b16365c3a`.
The completed [application OCR trial](GOAL-22-OCR-PIXEL-BOUNDS.md) uses the same
fixed V44 detector and official recognizer. Fresh scoring verifies 135 exact
development labels, up from 128, and 118 correct roles, up from 110. The full
report records both gains and losses; all five acceptance bars still fail.
No training, private or sealed reads, production approval, new model revision,
package version, or retained portable changes follow from this diagnostic.
