<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Clipped heading suffixes recovered, 2026-09-21

The OCR pipeline now extends a clipped heading crop when adjacent original
pixels contain aligned glyphs on its right. For example, a crop containing
only `Criterion` can include the visible trailing digit. The unchanged
recognizer reads that completed crop. No expected text or value is supplied.

Recovery requires a bound, unreviewed horizontal phase heading and measured
plot/divider geometry. Existing word-gap, alignment, glyph-size and bracket
constraints prevent crossing a phase boundary, bracket end or another detected
label. Human review decisions remain untouched. Conflicting readings remain
visible; only exact contained text fragments can be removed. Every recovered
heading remains unreviewed with an explicit warning.

Completed headings use a separate recognition batch so their greater width
cannot change the padding of existing recovered tick or glyph crops. Cache
identity includes the new composition version.

| Same development inputs and models | Before | After |
| --- | ---: | ---: |
| Exact text readings | 345/453 | 348/453 |
| Character errors | 780/3100 | 774/3100 |
| Correct roles | 357/453 | 357/453 |
| Correct marker centers | 726/838 | 726/838 |
| False / missing centers | 25 / 112 | 25 / 112 |
| Complete-workflow exports / failures | 12 / 11 | 12 / 11 |
| Correct exported values / relational rows | 409/706; 405/724 | 409/706; 405/724 |

All 345 earlier correct readings remain. Exactly three native heading regions
change. Tick readings, axes, calibrations, marker geometry and classifications
remain identical after excluding elapsed timings and execution-scoped marker
IDs. The full CSV fixture preserves all 39 panels, every OCR region, every
exported value and all source completion/failure states. Zero wrong-scale,
duplicate or failed-source residual rows occur; three earlier phase errors
remain. The native fixture contains 21 sources and 31 panels; the CSV fixture
contains 23 sources and 39 panels.

All 506 OCR tests and 494 application tests pass; sixteen optional OCR tests
skip. The native tool builds with zero warnings/errors. Checks take 98.30
seconds, native inference/scoring 145.43 seconds and the CSV regression
160.42 seconds. Actual model observations verify CPU execution throughout.

The first implementation also recovered three readings without changing any
exported value. Review then identified the padding interaction and separated
the new batches. Its added test initially failed because the synthetic tick
rectangles were close enough to form one word. Widening only that fixture's
spacing makes the public-pipeline regression exercise distinct tick crops.
The failed 20.44-second check and its blocked downstream launches are retained;
no native inference ran from that failed check. Both completed implementations,
source snapshots and the original unnormalized effect audit remain available.

The [evidence record](GOAL-22-HEADER-WORD-SUFFIX-REPAIR.json) binds 55 supporting
files. The preceding source checkpoint `bff1179` passed CI 35651107978.

Left-side prefixes and other missed or damaged text remain. Exact recognition
is 76.82%, below the unchanged 95% bar. This repair does not complete Goal 22.
No training, private/sealed read, model import, dependency, production approval
or package occurs. Source and owned synthetic examples retain Apache-2.0
provenance. Build 433 remains 0.4.33; final 2.0.0 gates remain outstanding.
