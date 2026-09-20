<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve panel axes around open symbols

An open marker interrupted a short stretch of a vertical axis. Raster import
dropped the lower axis fragment and merged the first two of three stacked
graphs. The original image still contains a connected ink path around the
marker's outline.

Panelization now extends an already qualified long line across such a short
gap only when original ink actually connects the endpoints. It searches a
bounded neighborhood and preserves source pixels. Disconnected strokes retain
the existing gap rule. Both horizontal and vertical cases have regression
tests, including identical gaps with no connecting outline.

All 418 application tests pass, including 16 panelizer checks. The PDF suite
passes 59 checks with one existing optional skip. On all 23 unchanged synthetic
sources, direct panel detection improves from 33 to 37 of 39 physical panels.
All detected plots remain fully inside their correct crops; no source is
over-split. Nineteen source results are unchanged. The direct
replay takes 1.17 seconds and performs no model inference.

The actual import workflow retains the remaining two single images through
its existing full-image review fallback. It now imports all 39 expected panels,
compared with 37 previously. This count does not establish successful reading.

| Whole workflow, same weights and truth | Before | After |
| --- | ---: | ---: |
| Imported panels | 37 | 39 |
| Sources that export | 13 / 23 | 12 / 23 |
| Correct unique values | 360 / 706 | 322 / 706 |
| Correct relational rows | 352 / 724 | 314 / 724 |
| Extra unique points | 8 | 8 |
| Wrong phase rows | 5 | 5 |

The complete replay takes 127.12 seconds. Separating the panels exposes another
defect: the axis fitter selects a phase divider after an open symbol splits
the true axis's native line candidates. A replay reproduces all 237 candidates
and the wrong axis exactly. The next repair must retain measured ink connectivity
without reconnecting empty gaps. Numeric OCR also remains wrong on the other
newly separated source. Export guards remain intact.

Thirty-two panels reach the diagnostic observation point, versus 33 before.
Corrected crop identities differ, and inference stops at the first failed panel
within a source. All 39 imported panels and all 706 truth points remain counted.
Do not describe the reduced observation count as removed inputs or claim an
end-to-end accuracy improvement from this repair.

The first workflow preparation omitted required manifest fields and failed
before inference. Its record is retained. A new preparation uses the complete
authenticated input identity. Only the PDF library changes in the successful
runtime comparison; every model and other executable remains fixed.

The [aggregate evidence](GOAL-22-PANEL-CONNECTED-INK.json) binds source, tests,
replays and diagnosis. No private/sealed read, training, dependency, production
approval, package or release occurs. Source remains Apache-2.0. Build 433 remains
0.4.33; all four Goal 22 outcomes remain incomplete.
