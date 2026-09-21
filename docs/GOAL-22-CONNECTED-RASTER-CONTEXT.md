<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Connected raster context repair, 2026-09-21

The open development diagnosis found complete stacked graphs reduced to small
axis fragments during import. Straight scanline runs could not follow mildly
skewed axes. Lost headings, legends and entire panels never reached OCR.

Standalone raster import now supplements incomplete scanline proposals with
the extent of a connected L-shaped ink component. It requires observed left
and bottom coverage and never bridges absent ink. Complete existing proposals
remain unchanged. These extents propose crops only; the axis module still fits
scientific geometry independently from immutable original pixels. Default PDF
panelization is unchanged. Existing disconnected-axis and independent-figure
regressions continue to pass.

With exactly the same weights and all 21 open sources counted, imported panels
increase from 26 to 31. Matched text regions increase from 233/453 to 311/453
(51.43% to 68.65% recall); precision increases from 64.90% to 76.04%. Exact text
increases from 186/453 to 260/453, correct roles from 159/453 to 247/453, and
character error rate falls from 60.87% to 43.26%. These remain failing OCR
acceptance results, not grounds for another sealed run or production approval.

Validation: 429 application tests and 59 PDF tests pass, with one existing
external-renderer PDF skip. Native archive/memory/worker checks pass 13/30/15
and the build has no warnings or errors. The repair job takes 216.93 seconds.
All 23 baseline sources, 39 panels, 17 exports and six calibration failures are
unchanged. Every scientific output and metric remains identical, including
479/706 correct unique values and 474/724 correct rows. Its inference takes
149.73 seconds using the reviewed c96 source-native library. Exact source and
result hashes are in the [evidence report](GOAL-22-CONNECTED-RASTER-CONTEXT.json).

Next: inspect the complete open native diagnosis and investigate input scaling
on small, very wide and tall images. The current fixed resize can reduce a
12-pixel character to roughly three pixels on long canvases. Remaining text,
role and marker/calibration failures still require work, followed by authentic
real acceptance, stage approval and both final 2.0.0 distributions.

No dependency, license, model weight, training, private/sealed read, promotion
or package changes. Prior sealed accounting and two unused reserves remain.
Build 433 stays 0.4.33. Previous checkpoint aba93ee passed CI run 35572535698.
All four Goal 22 outcomes remain incomplete; development continues.
