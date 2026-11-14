<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Label-position drafts and white margins, 2026-11-14

## Rust geometry: white-margin fallback

The owned 1920 x 1760 control has 640 pixels of white margin and is
`artifacts/axis-margin-check/owned-white-margin.png` (19,151 bytes, SHA-256
`dddc3d297be08f1bd84ddaf070c24619ac73c32deb0b86ff1ca65eb8480c9909`). Before
the change, the probe returned `NO_AXIS` in 0.642 s; report SHA-256
`5f664881cbaf21e2b62335de8b44b5979288778c1f0a0948b575c5fc9d821fda`.

After the fallback, the same owned input produced six measured pairs and a
complete proposal in 3.9558303 s (3,620.3025 ms pipeline, 206.1232 ms geometry).
The measured coordinates remain in the original image: bounds left 740,
bottom 1020, right 1220, top 720; X ticks 780/980/1180; Y ticks 720/870/1020.
The proposal maps session 1..3 and Y 0..10, retaining the original pixel
offsets. Report SHA-256 is
`0d8340629936e4d4a43183c40fff698c4d8db413d52fc8fd8dbde17f26ba40a5`; Rust
worker SHA-256 is `45f9c0975e644f17b9d80f271a7f11fa625d94488597d1b4042d14b6068d22c7`.

The fallback is restricted to primary `NO_AXIS` failure and uses connected
content spans with three strict measured ticks on each axis. It does not copy,
crop or resample the pixel buffer. Primary geometry behavior, protocol and
threshold constants are unchanged. This does not establish universal
white-padding invariance. Independent source review found no material issue.

Rust validation before the fallback was 24 passing tests plus the expected
`NO_AXIS` result for the white-margin control (compile 2.15 s, wall 4.050 s).
After the change, 31/31 Rust tests passed (compile 2.04 s, wall 3.522 s,
tests 0.04 s). The change added 41 lines and removed 11 in the Rust source.

## App review draft from label glyph centers

An owned tickless control (`owned-tickless.png`, 640 x 480, 3,990 bytes, SHA-256
`e70e983cf790759fbe59163af77af69c8ffc010689e66b99d742fc6f5203aebc`) returned
six numeric labels but no measured tick positions. The prior tick proposal
remained incomplete with `CALIBRATION_TICKS_UNPAIRED_LABEL` and
`CALIBRATION_TICKS_INSUFFICIENT_PAIRS` in 3.143838 s; report SHA-256
`00f1ecc915b3c793b15ec53aa018ab74b61b4dfa70ee79276a54ec2068842a66`.

`CalibrationTickProposal.CreateReviewDraft` now estimates a label center only
for an axis with no measured ticks. Estimated pairs are explicitly marked and
the UI labels their positions as estimated, with a persistent localized
warning. Existing sequence, role, zero, range, geometry and fit checks still
apply. It cannot replace a nonempty measured tick set or bypass other failed
validation. The user can edit the proposed anchors and values; existing
explicit review and Apply remain required. Nothing is automatically applied,
and this adds no inference step, frozen-schema change, model or dependency.

## Integrated validation

Based on source `11b24b0f`, the combined App/test build completed in 67.40 s
with zero warnings/errors; the final probe-only build took 6.28 s with zero
warnings/errors. App SHA-256 is
`40cf5c0a99dbbd7d7fedef7e706ba3f5557db5001ca9e8b63998c9c9091ade32`, probe
SHA-256 is `7a0e6791b0260f0df53ca2250f294c0368cd8af495b0d2a930bed20bfbdcabc9`,
and Rust remains `45f9c0975e644f17b9d80f271a7f11fa625d94488597d1b4042d14b6068d22c7`.

Focused VSTest passed 64/64, zero skipped, in 2 seconds. Filter:

```text
FullyQualifiedName~CalibrationTick|FullyQualifiedName~CalibrationLabel|FullyQualifiedName~AxisSuggestion|FullyQualifiedName~ResourcesAndLocalizationTests|FullyQualifiedName~ShellContractTests
```

TRX SHA-256: `c83af1935ce8d9658dc92526d62efb5c7016fc387367c57dd0e54fb030740569`.
The tests cover estimated-versus-measured labeling, warning persistence through
edits, explicit review before Apply, and stale-context rejection.

The locally built source workflow produced two owned-control results, both exit 0
with empty stderr and `applied=false`:

| Control | Process / pipeline | Evidence | Result SHA-256 |
| --- | --- | --- | --- |
| Tickless | 3.4758671 s / 2,985.0604 ms | 6 estimated pairs; measured tick arrays remain empty | `34e6dbede60514455355f5a1a7f72a0dba031923111f63081a36ca382a077f5d` |
| White margin | 3.3972164 s / 3,096.0523 ms | 6 measured pairs; 0 estimated | `e0a1cc60f943368d9287bdc804666e01adbebc4f50ae9e090cd059b4211e1e58` |

The tickless draft anchors are (140,381), (140,81), (540,381). Since the
geometry bounds are y=80 and y=380, the label-center estimates differ by one
pixel; they are explicitly estimated, not measured ticks. The padded control
retains measured positions at x=780/980/1180 and y=720/870/1020.

These checks validate the owned controls and App workflow only. They are not
native GUI acceptance or private-corpus accuracy evidence. No private candidate
run was made for this batch, and the App change has not been shown to improve
real calibration accuracy.

The under-1-MB raw scratch in `artifacts/axis-margin-check` remains until the
0.4.45 package consumers finish and this record is pushed. Then retire the
scratch; preserve original data and current runtime assets.

## Packaged 0.4.45 workflow

The hosted package from source `4e721496c434c209cbf64a4cbc953940dd735e1e`
reproduced both owned controls: six measured padded ticks in 3.609 s and six
estimated tickless label positions in 3.500 s. Neither proposal was applied.
The packaged prepared-crop tickless workflow then completed review, CSV export
and reopen in 4.643 s: three CSV rows, three reopened points, and a saved
user-confirmed manual three-anchor calibration. The saved calibration has no
tick-evidence regions, consistent with the fact that all six label positions
were estimated. CSV SHA-256 is
`249b49a16b036e5be69191a4aea4a867e75d4cdbe0ac6fd31fa2568a23b3edee`; workflow
report SHA-256 is `12a5342ea85b8c84d584b3f7309b49d0a10712420d8685d91066f52f6d9000c9`.

This verifies an owned packaged workflow with explicit review, export and
reopen, not native GUI acceptance, real-data accuracy or production approval.
