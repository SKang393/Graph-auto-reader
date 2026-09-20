<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve native axis connections around open symbols

Native line detection follows stroke edges. An open symbol interrupted both
edges of a real y-axis, leaving a nine-pixel gap between otherwise aligned
segments. The axis fitter consequently selected a phase divider as the left
axis. An exact replay reproduced all 237 original candidates and the error.

The provider now supplements existing aligned segments only when an actual
eight-connected path of original ink joins their nearby ends. The default
endpoint gap is greater than six and at most nine pixels. Search is local,
cancellable and indexed by endpoint location. It neither fills source pixels
nor joins equally sized empty gaps. The application stage identifier advances
so cached geometry cannot hide the change.

All 64 axis tests and 418 application tests pass. Regression cases cover both
orientations, connected outlines, empty gaps and preservation of source pixels.
The Windows x64 checker builds with zero warnings/errors in 10.52 seconds; both
fictitious safety/binding self-tests pass without reading private data.

The unchanged 23-source workflow takes 126.55 seconds. On the diagnosed panel,
the selected left axis moves from x=352 to x=104.95, consistent with the visible
axis near x=105. Calibration becomes valid and accepted markers rise from 13
to 19. Inference reaches the next panel, which still fails because its first
observed marker is session two. That missing marker is not fabricated.

All 32 previous observation panels remain, with one additional panel reached.
All exported numeric values and aggregate CSV metrics remain identical:

| Complete workflow | Result |
| --- | ---: |
| Sources that export | 12 / 23 |
| Correct unique values | 322 / 706 |
| Correct relational rows | 314 / 724 |
| Extra unique points | 8 |
| Wrong phase rows | 5 |

Eleven failed sources remain counted. A severely damaged source still has
incorrect axis geometry and is blocked before export. These results establish
the bounded connection repair, not complete workflow acceptance.

The checker and managed dependencies were rebuilt together. Other assemblies
are not claimed byte-identical; all current runtime files are individually
hash-bound. Source edits affect only the native axis provider and its stage
identifier. Model payloads, input images, truth and scoring tolerances stay fixed.

The [aggregate evidence](GOAL-22-AXIS-CONNECTED-INK.json) binds tests, source and
runtime reports. No training, private/sealed read, new dependency, activation or
package occurs. Source remains Apache-2.0. Build 433 remains 0.4.33; all four
Goal 22 outcomes remain incomplete. Continue marker and numeric OCR diagnosis.
