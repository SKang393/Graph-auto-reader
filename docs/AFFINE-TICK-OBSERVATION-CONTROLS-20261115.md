<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Diagnose exact-box observation on owned affine rasters

Baseline: `2316841baf1e4be6444962f9584133cabcb472d8`, frozen runtime from
[the rectification outcome](AFFINE-CALIBRATION-RECTIFICATION-20261115.md).

The changed-stage development trace returned `AXIS_BOX_NOT_OBSERVED`. No cause
has been established. Rust requires strong ink at the exact supplied origin and
observed continuous axis spans; multiple failed predicates share this code.
Bilinear rectification could alter thin-stroke coverage or introduce a gap at
an adverse subpixel phase. This hypothesis is tested only on owned geometry.

Before execution, fix eleven controls with nominal geometric stroke widths:
one width-three identity reference; width-three positive/negative two-degree
tilts at integer and half-pixel phases (four); width-three mild shear at both
phases (two); width-one/two identity (two); and width-one/two positive two-degree
half-phase tilt (two). These binary pixel-center samples are not exact raster
widths: the inclusive distance rule makes a nominal width-two integer-centered
axis three dark pixels wide. Effective coverage varies with phase. Keep plot
and tick geometry fixed. Render in memory and preserve original bytes. Record
source/derived seed intensity and axis coverage/gaps, actual exact-box responses,
tick counts, extras/missing observations, and residuals from the planted tick
positions. Do not invent a post-run tolerance or silently omit failed cases.

Use the current `CalibrationWorkingFrame` and unchanged Rust tick service with
the frozen twenty-library preflight and existing CPU budget. No OCR/model call,
private-image read, threshold/cap change, resampling variant selection, exported
scientific value or sealed evaluation occurs. This is one bounded diagnostic,
not a calibration accuracy gate. Batch source review before compilation and
run once after the control definitions and preflight are checked.

## Outcome

The one probe-only build passed with zero warnings/errors in **11.75 s**.
`--owned-affine-tick-observation-check-self-test` verified eleven declarations
and basic fixture geometry, with zero worker or private calls. The actual
`--owned-affine-tick-observation-check <App-exe> <Rust-exe>` then verified both
copies of all twenty frozen libraries and ran once from **2026-11-15
23:04:00.6407506 to 23:04:05.2703627 UTC**, taking **4.6171765 s** and exiting
zero with empty stderr.

All **11/11** cases returned: zero exact-box refusals, exceptions, timeouts or
unattempted cases. Every derived origin was black except the nominal-width-one
positive half-phase tilt, which was gray 15 and still below the unchanged strong
ink limit. All sampled derived-axis longest white runs were zero. Original
encoded inputs remained unchanged. No private/image-file writes, OCR, model,
Apply, export or sealed calls occurred.

The exact-box refusal **was not reproduced**. All cases returned three Y ticks
and zero X ticks. This additional shortfall is an owned fixture/scan interaction,
not evidence against rectification: the height-141 box sets the unchanged X
stroke limit to seven pixels, while the fixture's outward X strokes are eight
pixels plus their raster caps. `x_tick_positions` rejects a stroke longer than
that limit. The corresponding width-based Y limit is ten, allowing its strokes.
The same X omission occurs on identity input. This run therefore establishes
neither X-tick accuracy nor the cause of the private refusal. Keep its missing-X
accounting, do not relabel the controls as complete calibration successes, and
do not repeat the eleven cases or change thresholds to obtain a passing count.

Probe DLL SHA-256:
`a417cafac543c23fac3fc9b3e1fb083ec033506ebddeebb907688daac0a04f09`.
Report SHA-256:
`8f8737b501d2199208daab6fd9149a672d560dd3ed7dba038ac5678544973938`;
run receipt:
`a4c7771356ad6160a1fa50d3d9960bde0e45731cc59bf2a911afa3701f20573b`.
The three output files total **11,314 bytes**, retained only until this concise
record is pushed and the integration consumer finishes. No runtime source,
model, dependency, license or frozen schema changed in this diagnostic.

Status: bounded diagnostic complete with negative cause-finding evidence.
The real exact-box refusal remains unresolved; no correction is claimed.
