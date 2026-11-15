<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Packaged printed-X save, export and reopen check

Computer Use exercised the retained 0.4.53 ManualPreview app from source
`9adee655d526d62f936e208063eaaa2e4296291c`. No package or production flag changed.
The App EXE and DLL matched the [verified package](INTERNAL-PREVIEW-453-VERIFICATION-20261115.md)
hashes `c32696a14fb2c208339ae63fea59bb1753406dbfa72e64fbb960b8c0ca493ea0`
and `5d81550e05259003e089b6d7a85b00cc50dff00e9599f465aabd5a2a934ac6b2`.

## Observed native workflow

The tracked `tools/GraphReader.CalibrationOcrProbe/New-TickCalibrationFixture.ps1`
generated one owned control with `-ZeroOriginXLabels`. Its 640x480 PNG is 4,092
bytes, SHA-256 `966cbe6d3b3eb7f09f6e8a35ddcc7daac7086886284608a637ae61567dbafdc8`.
It contains printed X labels 0/5/10 and Y labels 10/5/0. It contains no plotted
data markers. No private input, truth, independent split or model selection was
used; sealed reads were zero.

1. Open imported that PNG normally. Suggest calibration performed actual geometry,
   tick measurement and literal OCR with no supplied geometry or values. The UI
   reported six regions and GPU inference on AMD Radeon RX 6600 XT. The paired
   rows were X 0/5/10 at measured pixels 140/340/540 and Y 10/5/0 at 80/230/380.
   This observation does not establish GPU speed or load limits.
2. Use printed X values filled first X 0, final X 10 and Y maximum 10. Apply
   remained disabled until the explicit anchor/label review checkbox was checked.
   Applying reported the 0-through-10 printed range and estimated point X values.
   The accessible names and help switched from session inputs to printed-X
   inputs. This native check used English; language-change notification is covered
   by the focused source tests and full CI, not a new visual language test.
3. Create series and Points placed two UI-authored persistence test points. Point
   placement is one-shot: the initial second canvas click moved the cursor only.
   Re-selecting Points added the second point; the series count became two.
   These points test persistence and export, not marker detection accuracy.
4. Save wrote `printed-x-review.garproj`. Export preview showed two points, one
   series, one phase, zero blockers and zero warnings. Export wrote one ordinary
   CSV plus CSV/JSON audit files to an isolated owned directory.
5. The app closed normally. Relaunch and Recent projects reopened the saved file
   with both points, zero review issues, first printed X 0, final printed X 10,
   Y maximum 10 and matching accessible help. The project hash stayed unchanged.
   The second window also closed normally; no target window/process remained.

Some Computer Use accessibility metadata lagged the visible dialog focus. One
initial click lacked coordinate geometry, one modal set-value had an unavailable
cached index, and one offscreen review click was rejected. Fresh screenshot
coordinates and scrolling recovered without repeating inference. There was no
application exception or failed save/export operation.

## Saved values and export verification

Three manual anchors persist `(140,380)->(0,0)`, `(140,80)->(0,10)` and
`(540,380)->(10,0)`, with null session lattice and null OCR evidence IDs. The two
points retain separate observation indices 1 and 2, null printed-X observations,
and estimated-X provenance.

| Observation | Original pixel X/Y | Fitted X/Y |
| --- | --- | --- |
| 1 | 239.33884297520666 / 231.4049586776859 | 2.4834710743801667 / 4.953168044077136 |
| 2 | 440.3305785123967 / 156.03305785123968 | 7.508264462809919 / 7.46556473829201 |

An independent read-only artifact check recomputed the affine coordinates and
matched both CSV values to the saved project. Both audit formats additionally
matched point IDs and original pixels, reported `x_source=estimated`,
`export_mode=printed_session`, valid calibration and no session-origin override
or override reason/time. Each output contains exactly two rows.

| Output | Bytes | SHA-256 |
| --- | ---: | --- |
| Saved project | 12,121 | `952f0d594e227bea212824289220c442b4a145e63943890cdce91b5840908df9` |
| CSV | 98 | `e4084adcb7da1a55ccd2b187f5a1142d4b9fb3dfeb17811341d12dae56ce4d35` |
| CSV audit | 1,019 | `566993beb7b381dfb9eadb4aa05a055b926bd5e565e94ef70b3159a090e550f9` |
| JSON audit | 2,421 | `c604c36ecfa6fa5fc3f36c30abb82590e7d11050bba8475fb2395821f696add2` |

## CI, scope and retention

Full Windows CI [43730928951](https://github.com/SKang393/Graph-auto-reader/actions/runs/43730928951)
passed exact source `8fc3821b5fe2bff2e2c571369518c299c098ad47`, which includes
the newer unpackaged source repairs and diagnostic correction. Thirteen .NET
suites passed 2,991 tests, with 30 skipped and zero failed; Rust passed 63 tests.
Version policy 62, ledger 6 and portable-retention 12 checks passed, together with
localization, bounded-host, evidence and packaging validation. The earlier
`a3411544` run was superseded/cancelled by this push, not failed. Public release
jobs were skipped. No newer runtime is implied for packaged 0.4.53.

The owned runtime/save/export/reopen scope passes. It does not establish
real-graph automatic accuracy, marker detection, installer or fresh-machine
acceptance. No inference/UI performance timing is claimed. No dependency or
license changed; project source remains Apache-2.0.

At `2026-11-15T15:48:18.7532714Z`, after both app windows closed, its five newly
created isolated Data files / 12,262 bytes were moved into the owned QA directory.
All sixteen pre-existing shared Data files remained byte-identical. The sole
retained package tree returned to exactly 478 files / 283,847,583 bytes.
The complete `artifacts/printed-x-native453-check` scope is twelve disposable
files / 36,363 bytes, including the generated fixture, project, exports and
isolated Data. Its consumers are complete; retire it after this record is pushed.
Original inputs, the active 449 owned fixture and current package remain intact.
