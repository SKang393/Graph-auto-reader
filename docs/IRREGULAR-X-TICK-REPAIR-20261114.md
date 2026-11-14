<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Irregular X tick positions

An owned 128-by-128 control with measured X strokes at 20, 40, and 100 returned no X ticks before this change. Those pixel intervals can map to unequal numeric increments on a linear scale, so spacing alone cannot reject them.

Strict measured X tick positions now use the existing Y rule: three to 128 positions, at least four pixels apart. The historical endpoint-only fallback retains its median-gap check. Stroke validation, ambiguity handling, candidate limits, and the observed-ink work budget are unchanged. Values are not inferred from the positions; numeric pairing, fit checks, and explicit review still apply.

The pre-change owned regression failed with `[]` versus `[20, 40, 100]`. After the change, `cargo test --offline --manifest-path native/graphreader-engine/Cargo.toml --target-dir artifacts/native-engine` passed 49 tests. One obsolete negative fixture was corrected to use an invalid three-pixel gap; the first full run was 48 passed and one failed before that correction. A release worker built offline returned `OK 8 100 115 10 0.710938 20 100 20,40,100 -` on the same owned geometry, exit 0, in 103.9 ms wall time. Worker SHA-256: `9cd273aa2465e3b472137f5afa091770e5507797dbbf062ee117f21e650c5879`. Source SHA-256: `a946461db843686f2748ef21e65eb0398726f2c3e24d96a50afbc9f04e433f27`.

This is an owned pixel-geometry check, not an OCR, private-data, accuracy, or production-approval result. Fallback can still reject ambiguous axes or occluded ticks; irregular marks are review proposals rather than scientific values.

Independent source review found no safety issue. The owned controls and raw
results in `artifacts/irregular-x-check` are disposable after this source and
outcome are pushed. Regenerate the labelled control with the existing
`New-TickCalibrationFixture.ps1`: move the middle X tick/label from 340 to 240
and change the right label from 3 to 5, preserving all other geometry. Refresh
the owned request image hash before the existing `--tick-proposal-check`.

An additional owned 640-by-480 label-bearing control used X ticks at 140, 240, and 540 with printed values 1, 2, and 5. The existing `--tick-proposal-check` ran the repaired worker and actual CPU OCR with the reviewed 0.4.48 assets. It returned those three X pairs and the three observed Y pairs (10, 5, 0), all measured and none estimated. The review-only proposal was complete and was not applied. Source PNG SHA-256: `e5f3c66f7c4e9516fbfd5ff28e1a6a2aff3b1cc25f7fc5c8f31bb30a0fffeb9f`; App DLL SHA-256: `939a76b3f0ce08ea76b63006c97dc2bfe073570adc6772c1cecbfd5385f3fffd`. Process wall time was 4,377.2 ms, including 71.6 ms geometry and 2,954.9 ms OCR. This single synthetic control does not establish accuracy or production approval.
