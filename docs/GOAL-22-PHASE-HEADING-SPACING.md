<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve phase meaning when OCR drops spaces

Saved synthetic development output contains the exact letters
`Withdrawalcontinued`, but the missing space prevents phase recognition. Two
otherwise correct exported rows consequently receive an unknown phase. A
different image contains damaged letters, which remain unresolved.

The OCR role classifier and phase reasoner now compare the existing withdrawal
and reintroduction continuation terms without whitespace. They retain the raw
OCR text, original-pixel locations and existing heading-geometry requirements.
No fuzzy spelling correction or inferred scientific value is introduced. The
OCR cache identity advances to v8 and the phase stage to 0.1.2; schema version 1
is unchanged.

All 388 OCR, 48 phase and 418 application checks pass. Sixteen existing optional
OCR checks remain skipped. The Windows x64 checker builds in 9.01 seconds with
zero warnings or errors, and both fictitious runner checks pass. The
[aggregate evidence](GOAL-22-PHASE-HEADING-SPACING.json) binds the tested source
bytes and result files.

The same 23 owned source images complete actual inference in 123.84 seconds.
All model weights and operating thresholds remain fixed. Correct relational
rows improve from 314/724 to 316/724; wrong phase rows fall from five to three.
All 33 prior panel observations, numeric exported values, axis results, accepted
marker centers, calibration outputs and source completion states are preserved.

The complete workflow is still below acceptance: 12/23 sources export, 322/706
unique values are correct, and eleven failed sources, 384 missing points, eight
extra points and three unclear-heading phase errors remain counted. Continue
marker and numeric OCR diagnosis. No training, private/sealed read, model
activation or package occurs. There is no new dependency or license obligation.
Build 433 remains 0.4.33; all four Goal 22 outcomes remain incomplete.
