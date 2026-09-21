<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Raw and assembled OCR evidence

Synthetic workflow diagnostics now capture detector regions before assembly,
recognition and recovery. A final-result cache cannot omit this observation.
Supplied fixture regions are identified explicitly; private and aggregate-only
routes reject detailed capture. Normal production calls install no observer.
No persisted project or vision contract changes.

The unchanged V27/V5 marker and V44/official recognition combination completes
the native replay in 147.46 seconds. All 39 panels are captured, including panels
from all six failed sources. Axes, recognized text, source statuses, exported
values and every CSV metric are identical to the preceding baseline: 17/23
sources export, with 479/706 correct unique values and 474/724 correct rows.

The new saved-output scorer authenticates runtime files, all four model pairs,
input images, crops and separate synthetic truth before scoring. It rejects
missing panels, supplied regions, duplicate identities, altered files and
private/sealed scope. The existing geometry-only pairing and OCR metric remain
unchanged. All 709 training and 183 development labels remain in the totals.

| Development measurement | Raw detector | Finished OCR |
| --- | ---: | ---: |
| Matched labels | 157 / 183 | 181 / 183 |
| Extra regions | 41 | 8 |
| Precision | 79.29% | 95.77% |
| Recall | 85.79% | 98.91% |
| Exact recognized text | Not applicable | 177 / 183 (96.72%) |
| Correct roles | Not applicable | 174 / 183 (95.08%) |
| Character edits | Not applicable | 16 / 1,019 (1.57%) |

Raw regions are intermediate evidence, not an additional acceptance gate invented
for this run. Assembly is part of the actual candidate workflow. These two
measurements must not be substituted for one another in a source adapter.
The report grants no stage admission or production approval. Training-layout
OCR remains weaker: 416/709 exact texts, with 71 missing finished regions.
The six calibration failures still block their exports.

Validation: 391 OCR tests pass with 16 existing optional skips; 425 application
tests, 14 scorer tests and six checker commands pass. The x64 checker builds
with zero warnings and errors. An initial scorer fixture used the wrong relative
path for its traversal check; fixing the fixture produces the recorded passing
run. .NET checks take 102.82 seconds; scoring saved output takes 1.03 seconds.

[Bound evidence](GOAL-22-RAW-OCR-OBSERVATION.json) records the exact sources and
results. CI for the preceding checkpoint `185ad6c` passed run 35549537112.
Earlier run 35548489420 failed a scheduler timeout test; that failure remains
recorded, and the succeeding full run passed.

No new dependency, license, model payload, training, private/sealed read,
activation or package occurs. Project changes remain Apache-2.0. Build 433
remains 0.4.33. Continue current composed stage evidence, calibration and marker
coverage repairs, then genuine acceptance and final distribution verification.
All four Goal 22 outcomes remain incomplete.
