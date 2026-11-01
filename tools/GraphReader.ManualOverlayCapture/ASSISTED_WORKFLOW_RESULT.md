<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Training-only assisted workflow result

The existing unapproved candidate workflow was run on the seven eligible
training panels using panel-bound mappings derived from recorded Engauge
anchors. The manual-calibration confirmation flag was simulated for this
diagnostic; no user confirmed those derived anchors in the application. Five
panels completed the candidate pipeline and two failed with
`PHASE_INVALID_DIVIDER_OUTPUT`. All seven reproduced their prior accepted marker
geometry and confidence exactly. The two phase failures reported that detected
dividers were not unique, separated, and inside the plot; the specific cause
requires further diagnosis.

Across the seven images, there were 130 unique recorded curve points. The
accepted marker stage matched 79 within five pixels, identical to the earlier
uncalibrated capture. The completed workflow returned 183 candidate points,
50 of which matched recorded points within five pixels. Failed panels remain
in the 130-point denominator. The source labels are imperfect and incomplete,
so unmatched detections are not established false positives. These figures do
not establish graph-coordinate accuracy, correct series or phase assignment,
automatic calibration, generalization, or release acceptance.

| Evidence | SHA-256 |
| --- | --- |
| Binding registration | `53bd64ab4f62b189a5e29928672b49db8ac9e4d842e09d24a3f6c0ac82b5e497` |
| Prepared private bindings | `e092e6c352b3bd25ccf59debd72c6df60a5bfe2daf0b6a9b2384f0a8ad19cb9c` |
| Workflow registration | `51545d8d7673182e40900523bf9b09500ce516ea1842ec64654e8d8a897124d1` |
| Runtime before and after | `27d53822e5c84bf42c1404f2e2e81f3244939fb9ee9d956477cf2dda106908d1` |
| Candidate workflow capture | `190ea158d83837b1207cfaa8fdc3899bc3f447709f4fb71ed6f1b550030e3b2f` |
| Aggregate score | `b4a1a75ad56cb3d4e0061305304e2393a668ad8901b4a7398ba055d0d7e6c12f` |

The Release diagnostic build succeeded with zero warnings after an initial
missing `System.IO` import was corrected. Synthetic binding self-check,
seven-binding identity check, three focused Python tests, and pyflakes passed.
The capture returned nonzero because two panels failed. It used CPU inference,
no optimizer, no development or sealed payload, and no production approval.
Inputs and per-panel results remain in project-local ignored artifacts for the
ongoing phase investigation.
