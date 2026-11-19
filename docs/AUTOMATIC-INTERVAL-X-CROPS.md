<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recovering printed labels between X-axis tick strokes

The automatic OCR pipeline previously used three recognized labels to establish a text row. With just one or two readings its sparse-row recovery looked for a tick directly underneath each label. On a graph with labels between strokes, the initial detector located two numeric crops, but that fallback could not locate the rest of the row.

The new recovery step measures continuous outward strokes in the original raster. It requires two existing numeric crops in distinct intervals on the same row and measures the ink and white space in each proposed cell. A detector box can have a diagonal long edge around an upright digit. The edge angle guides neither the value nor the position of the proposed label. An explicitly observed incompatible text direction prevents the proposal.

Each additional crop goes to the automatic configuration's existing recognizer. Existing text regions keep their pixels and alternatives. A crop with a crossing stroke, incomplete glyph coverage or an unsupported inter-glyph gap stays out of recovery. Calibration independently rechecks the interval on its fitted axis. A printed position between strokes remains an estimate. A discrepancy still blocks automatic numeric output.

## Owned Windows controls

With two supplied numeric regions and an independently drawn rail, the old recovery code proposed zero crops for the missing two-glyph label. The new code proposed one crop on the same raster. Controlled recognizer outputs exercise role assignment and retention of conflicting readings. Additional cases cover absent or detached ticks, one existing anchor, wide gaps, crossing prose, overlapping crops, and reported text direction versus the longest edge of a detector polygon.

## Automatic development evaluation

The same automatic detector and recognizer read 12 of 19 printed X labels on the interval-labeled development image, up from two. All ten additional readings matched the visible text. Two existing fragments retained their original regions. Of the seven labels still missing, two overlapped those fragments, four had original ink outside the recovered glyph group, and one lacked a pair of measured boundary strokes within the fitted rail. These counts describe printed labels, separate from plotted observations.

Nine labels without a reported numeric conflict gave a 0.374 px RMS residual when fitting graph values to glyph centers. With the three disputed readings withheld, the primary readings fell within 0.40 px of that line and the competing values were at least 26.6 px away. This is a diagnostic using estimated glyph centers. The calibration check still requires independently supported evidence to resolve these alternatives. Their source readings and the refusal remain available. The other development image had no additional interval crops.

Both image-only outputs were hashed before their registered references were scored. With one-to-one matches at 5 px, the first graph had 5 matched centers, 18 extra and 5 missing (21.7% precision, 50% recall). The interval-labeled graph had 12 matched centers, 3 extra and 4 missing (80% precision, 75% recall). Its session-column RMS changed from 0.888 px to 0.810 px. The respective cold image-only runs took 23.148 s and 14.309 s. Both refused calibration, leaving all 10 and 16 reference rows missing and automatic Y error undefined. These two exposed development cases do not estimate general accuracy.

## Native review and export

Each image was imported into a fresh Windows process with a real, transparent, nonactivating WPF window. The window used an isolated portable profile and the same candidate models as image-only evaluation. The first image reopened with zero numeric rows and export disabled. The second image required explicit manual session-first calibration. The simulated reviewer entered the visible X and Y ranges and placed three anchors. Detection then projected 15 points. One horizontal point edit confirmed session one. Six series reassignments followed the visibly disconnected traces, the right-hand series was marked unknown, and the intervention was linked to its shared baseline. The reviewer made no Y edits and confirmed no phase labels.

After Save As and reopen, acknowledging 76 warnings enabled export. The primary CSV and two audit companions each contained 12 rows. The audit recorded one X-only move, six series-only reassignments and five untouched exported points. All 12 phase assignments remained unconfirmed. No points were individually accepted. The run used the candidate profile throughout, with simulated reviewer actions and fixed file-dialog paths. The installed preview was not used for this check.

The assisted CSV matched 12 of 16 reference sessions, with four missing and zero extra. Ten matched Y values were within one pixel and two were outside. Mean absolute Y error was 0.023951 graph units and maximum absolute error was 0.084220. The first graph had no assisted numeric export.

Focused Windows Release checks passed 9 interval-recovery cases, 57 related OCR cases and 54 fitted-tick and calibration cases. The original raster recovery code and the model checksums are independent of review-assistance assets. No model approval or release was part of this evaluation.
