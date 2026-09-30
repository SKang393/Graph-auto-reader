<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve connected axis ink, 2026-09-30

Native line endpoints around a 24-pixel round marker leave a 25.38-pixel gap.
The old 24-pixel lookup misses the connected y-axis and selects an internal
phase divider. Candidate lookup now includes the path checker's existing
2-pixel uncertainty at each endpoint. The actual ink path, 24-pixel detour,
angle checks and empty-gap rejection stay unchanged.

Four native tests cover horizontal/vertical markers, connected/disconnected
ink, original-pixel immutability and a competing internal divider. All **88
Axis and 508 App tests** pass; all relevant builds have zero warnings/errors.
The initial App launcher path failure is retained and repaired. Restored source
matches the tested endpoint-only snapshot exactly, so its unchanged tests were
not repeated after rejecting the larger variant.

| Measurement | Previous | Retained repair | Rejected line-fit variant |
| --- | ---: | ---: | ---: |
| Native matches | 726/838 | 733/838 | 730/838 |
| Extra markers | 25 | 25 | 25 |
| Correct printed-axis corners | 84/93 | 85/93 | 85/93 |
| Exact OCR strings | 348/453 | 348/453 | 348/453 |
| CSV exports | 12/23 | 12/23 | 12/23 |
| Correct unique values | 409/706 | 409/706 | 409/706 |
| Correct relational rows | 405/724 | 405/724 | 405/724 |

The retained repair keeps 725 earlier matches, recovers eight and loses one.
That lost decoded marker lies 2.074 pixels outside the fitted plot; the final
2-pixel boundary is unchanged. All 21 native sources, 31 panels and 838 points
remain scored. Native recall is 87.47%, still below the unchanged 95% bar.
One repaired y-axis also extends into heading ink; 85/93 corners is not a pass.

All 23 workflow sources, 39 panels, 706 points and 724 relational rows remain.
Every source completion/failure state and failure message is preserved. Train
retains ten exports, 253 correct values and 249 correct rows; dev retains two
exports and 156 correct values/rows. One point's original coordinates and
exported Y change by 0.915573/
0.058853 pixels and
0.071752 data units. It remains correct under
the unchanged metric. Outputs are therefore not byte-identical. Three
wrong-phase rows and 11 failed graphs remain; there are no scored wrong-scale,
duplicate or failed-source residual rows. This does not replace the runtime
calibration-safety tests.

A second attempt removed generated bridges from line-fit weights. It passed
92 Axis/508 App tests and recovered the targeted boundary marker, but lost
three earlier markers in one tall panel. Its source snapshot and complete
471.04-second workflow run are retained without integration. A 17.17-second
mask replay found identical grayscale and OCR masks but 52,237 changed
structural/residual mask pixels, including 44, 59 and 108 pixels around those
three centers. Two disappear before classification; the third is rejected by
the classifier. Continue controlled mask and boundary diagnosis before another
change. Do not select a fit merely because its unit tests pass.

The retained native and CSV inference runs take 165.15 and
236.53 seconds; the complete sequence takes
421.10 seconds. All recorded models
use CPU. The shared 80% CPU guard is fixed; older runs under compounded limits
are not controlled speed baselines.

The [aggregate evidence](GOAL-22-AXIS-ENDPOINT-REPAIR.json) binds every source,
protocol, runtime, score, test and rejected attempt. Model weights, operating
cutoffs, acceptance policy and input bytes are unchanged. Existing Apache-2.0
provenance is unchanged. No private/sealed access, training, model import,
production switch, packaged build or release occurred. Build 433 remains
0.4.33. Goal 22 is incomplete.
