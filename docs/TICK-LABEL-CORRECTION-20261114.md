<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Explicit correction of calibration labels

The review list previously allowed inclusion and exclusion, but not correction
of recognized text. On an owned three-tick axis, `0, 8, 10` fails consistency
when the middle printed value is `5`; excluding it leaves too few pairs. This
is a source-demonstrated review limitation, not an explanation established by
the [fixed private aggregate](REAL-DEV-CALIBRATION-449-CONFIRMATION-20261114.md).
Ordinary irrelevant text already has role filtering, and explicit deselection
already refits from fresh selected rows. Those safety rules are unchanged.

Each geometrically assigned X/Y row now has an editable tick value, a visible
user-correction indication and Reset to recognized text. Raw OCR text, role,
confidence, failure and response remain immutable and visible. A valid explicit
correction can give the local review row its X/Y tick role, including an OCR
glyph read as `S` or `I`. Rows without geometric axis assignment remain
read-only. Copy-to-manual-value actions use the validated correction.

An edit or reset clears computed calibration values and prior review. Refitting
uses cached rows without another OCR call. An included invalid or blank edit
blocks refitting, including when the raw role was Other and enough other valid
pairs exist. The row shows an inline instruction for invalid input, using the
same validity property as the refit guard. Excluding or resetting that row
restores ordinary processing.
Numeric-literal, association, monotonicity, spacing, range and source-currentness
checks still apply. Explicit review and Apply remain required. Corrected rows
do not turn into automatically accepted OCR calibration evidence.

The first focused batch passed 67 tests. Refinement for nonnumeric glyphs
initially produced 67 passes and one fixture failure: its X label had a
Y-oriented polygon. The fixture was corrected. Independent review then found
the included-invalid-Other-row gap and the implementation added its narrow
guard. That batch passed 69 tests. The final batch, including inline guidance
and an Apply/export/Undo regression, passed **70/70**, zero failures or skips,
with TRX run interval 2.696149 seconds. No production threshold or test timeout
changed.

The added integration test used actual workspace Apply and export with an owned
OCR stub. Correcting `8` to `5` required explicit review, produced a
user-confirmed `manual_three_anchor` calibration with three null evidence IDs,
and preserved raw OCR `8`. Pixel `(80, 60)` mapped to graph `(12.5, 5)`; the
observation-order CSV contained X index `1`, Y value `5`. Undo removed the
calibration. Its initial fixture incorrectly assumed only one CSV rather than
excluding the separate audit CSV; the corrected focused test passed, then
passed again in the final 70-test batch. This is not a native visual check or
an actual-reader accuracy measurement.

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:OutDir="$PWD/artifacts/tick-correction-build/" --filter 'FullyQualifiedName~CalibrationTickViewModelTests|FullyQualifiedName~CalibrationTickProposalTests|FullyQualifiedName~CalibrationLabelViewModelTests' --logger 'trx;LogFileName=tick-correction-guidance.trx' --results-directory artifacts/tick-correction-check --verbosity minimal
pwsh -NoProfile -File packaging/localization/Audit-Localization.ps1 -RepositoryRoot . -ReportPath artifacts/tick-correction-check/localization-final.json
git diff --check
```

The final localization audit passed: 429 contract keys, no missing, extra or duplicate
keys and no unresolved WPF resource references. It reported 62 existing unused
keys. The independent source review found no remaining material issue after
the invalid-edit guard. Neither compilation nor these tests establish visual
usability in the native window.

| Evidence | SHA-256 |
| --- | --- |
| App DLL | `f636514e63966a8e51eb6ab33560e5db08a0fdb972b65ef321a5ef1bd741c844` |
| 70-test TRX | `2faec6f507239a7a7d7cee5b4559b07696f9a0877f12f7fe2ea6b68f793e7e81` |
| Localization report | `d408f115ed7fdb5a449d62de404d0a0e64c53c6b218d2c9983024af5c59daff8` |

An earlier redundant no-build test attempt for the rejected irrelevant-text
hypothesis aborted before executing tests: testhost connection timeout after
32.8417312 seconds. No product failure was established; normal-host CIM later
confirmed its reported PID was absent. No retry of that unchanged test attempt
was made.

No dependency, model, frozen contract, training run or license changed. Source
remains Apache-2.0. No private input was used for this implementation or its
owned tests. The retained 0.4.49 package predates this source change. Native
acceptance remains waiting under REV-011. Source and outcome reached main in
`98a8f6ad30c8371e794f83a6e0173eb3028b874c`. After validation consumers finished,
guarded cleanup retired `tick-correction-check`, `tick-correction-build` and
`tick-label-hypothesis-check` at `2026-11-14T21:16:30.6354353Z`: **225 files /
99,848,891 path bytes**, zero active consumers, all targets absent. Receipt:
`artifacts/tick-correction-retirement.json`. Originals, current dependencies
and the delivered package were preserved. The tests regenerate these outputs.

Full Windows CI [43641587267](https://github.com/SKang393/Graph-auto-reader/actions/runs/43641587267)
passed on `98a8f6ad`, job `132590117826`: **2,732 .NET tests passed, 30 skipped,
zero failed** across 13 suites, including all 1,039 App tests. All 49 Rust tests,
62 version-policy checks, 5 ledger checks and 12 portable-packaging checks
passed. Public-release jobs were skipped; this does not add native visual
acceptance or package the correction into the retained 449 build.
