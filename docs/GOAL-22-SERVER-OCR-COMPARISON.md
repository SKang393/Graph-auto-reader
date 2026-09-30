<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Larger pretrained OCR comparison, 2026-09-30

All four configurations completed. None meets the existing development bars.
The server detector and combined configuration are not selected. The server
reader remains an option for further diagnosis, with no production change.
The [aggregate evidence](GOAL-22-SERVER-OCR-COMPARISON.json) binds each run,
model, source snapshot, metric, conversion, license and test result by checksum.

## Implementation and validation

The server reader has 18,384 explicit dictionary tokens and one CTC blank.
Ten tokens contain multiple Unicode scalars. Splitting those tokens as ordinary
characters changes all subsequent model class indices. The native recognizer
now preserves explicit token boundaries, validates the declaration, copies it
on construction and includes boundaries in result-cache identity. Existing
Unicode-scalar alphabets remain supported. No frozen schema changed.

CSV scoring now reports the existing 20 train and three dev sources separately.
Membership must be disjoint, nonempty and cover every frozen source exactly.
Failed sources keep their complete truth denominator. Full-suite metrics remain.

Tests pass: 510 OCR and 500 App, with 16 optional OCR skips. The native build
has zero warnings/errors, and all 28 CSV binding/self-test scenarios pass.
Both repeated ONNX exports are byte-identical. Sixteen CPU parity cases pass
per model: detector maximum error 3.688037395477295e-7, reader maximum error
1.2040138244628906e-5, against the frozen 1e-4 limit. The completed reader
parity was reused. Paddle itself rejected the earlier 1200-square detector
probe; the repaired probe uses stride-compatible 1280-square input. Conversion
warnings and every unsuccessful preflight/check attempt remain recorded.

## Native development results

Same 21 dev sources, 31 panels, original pixels, thresholds, downstream code,
V27 center model and V5 classifier. Time is total measured workflow execution
under the shared CPU cap, not isolated detector or reader CPU time.

| Configuration | Exact text | Extra text regions | Correct markers | Extra markers | Seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| baseline | 348/453 | 110 | 726/838 | 25 | 137.34 |
| server-detector | 328/453 | 204 | 616/838 | 8 | 237.22 |
| server-reader | 377/453 | 112 | 727/838 | 25 | 166.17 |
| server-both | 340/453 | 204 | 596/838 | 8 | 252.87 |

The reader retains 344 earlier exact readings, recovers 33 and loses four.
Exact reading accuracy rises from 76.82% to 83.22%, still below 95%.
All configurations retain 84/93 correct axis corners. Native source exports
remain zero for baseline/detector and one for reader/both; failed sources are
still included in every reported label, marker and geometry metric.

## Full workflow CSV results

| Configuration | Exports | Correct values | Correct rows | Wrong-phase rows | Dev exports | Dev values | Seconds |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline | 12/23 | 409/706 | 405/724 | 3 | 2/3 | 156/206 | 218.31 |
| server-detector | 10/23 | 299/706 | 294/724 | 3 | 1/3 | 133/206 | 262.00 |
| server-reader | 16/23 | 429/706 | 416/724 | 11 | 2/3 | 156/206 | 264.39 |
| server-both | 12/23 | 314/706 | 309/724 | 3 | 1/3 | 133/206 | 330.34 |

The reader's extra 20 correct values occur on train sources. Dev remains
156/206 correct values and 156/224 correct rows. Wrong-phase rows rise from
three to 11. These gains do not establish held-out acceptance or justify
production activation. All arms have zero scored wrong-scale rows, duplicate
shared-baseline rows and failed-source residual rows; this synthetic result
does not replace application safety tests or private acceptance.

The historical and current baselines have identical accuracy counts and 420
matched export-row identities, but threading changes produce small numeric
differences: maximum X difference zero and Y difference 1.6799134527900605e-8.
387 rows differ numerically. No row identities are unmatched.

## Missing diagnostics and scorer repair

Two server-detector sources fail with an unstructured printed-tick validation
exception. A reader diagnostic continuation also exposes the validation path.
The original scorer incorrectly required raw OCR and completed downstream
observation counts to agree. A checksum-bound scoring-only repair accepts
diagnostic gaps belonging to failed sources, verifies all available artifacts,
and reports coverage. Saved inference is reused; no failure or truth is removed.

| Configuration | Completed panel observations / 39 | Raw OCR observations / 39 |
| --- | ---: | ---: |
| baseline | 39 | 39 |
| server-detector | 34 | 36 |
| server-reader | 38 | 39 |
| server-both | 39 | 39 |

The zero-confidence calibration repair is separate work and is not included
in these frozen runtime results. All 23 sources, 706 unique truth points and
724 expected rows remain scored in every configuration.

## Reproduction, resources and licensing

The frozen protocol is `ml/ocr/official_bakeoff/SERVER_COMPARISON_PROTOCOL.json`.
Conversion source is `ml/ocr/official_bakeoff/server_conversion.py`. Retained
commands, inputs, immutable runtime/source snapshots and scorers are under
`artifacts/goal22-runs/server-ocr-comparison-v4/`; execution wrappers are
`artifacts/goal22-tools/Run-ServerComparisonV4.ps1` and
`artifacts/goal22-tools/Resume-ServerComparisonV4.ps1`. The source hash bindings
were reverified after all eight jobs completed. Tests/build commands are in
`artifacts/goal22-tools/Run-ServerResumeChecks.ps1` and
`artifacts/goal22-tools/Run-ServerAppChecksV2.ps1`.

Native runs use CPU only, Idle priority, all 12 processors eligible and an
80-percent aggregate Windows job cap. Explicit .NET processor count 12 avoids
the cap shrinking its detected count to 10. Python parity additionally uses
cooperative work/rest. Native validation does not implement cooperative per-core
work/rest; the protocol's flat work/rest field is clarified in the evidence.
Do not interpret the aggregate cap as an instantaneous per-core guarantee.

The maintainer identified and approved both imports in REV-009. Official
Apache-2.0 licenses, notices and pinned upstream revisions were verified.
Detector ONNX: 88,045,198 bytes, SHA-256
`2429e33dcee532d1c61e6ad30cb145995f3e85203cb36e266492ddcec8e531f4`.
Reader ONNX: 84,483,046 bytes, SHA-256
`0980049401eceb506952f769765a6b95be07cb1197dcbb13c93f107a328f8418`.
No new runtime dependency, committed weights, private/sealed reads, training,
model approval, packaged build or release. Build 433 remains version 0.4.33.

## Next work

Repair confidence validation so malformed tick evidence is preserved for
Review and blocks export without an unstructured exception. Continue the
fixed off-center marker revision under the unchanged current OCR baseline.
Diagnose the reader's phase regression before considering it for the pipeline.
Goal 22 acceptance remains **FAIL**.
