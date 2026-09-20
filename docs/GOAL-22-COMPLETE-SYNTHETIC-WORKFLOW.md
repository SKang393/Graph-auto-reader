<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Complete synthetic workflow diagnosis

Actual model inference from all 23 source PNGs completes in 128.24 seconds.
One source writes CSV; 20 fail calibration, one fails point validation, and
one fails session-origin export validation. No manual anchors, points, review
corrections, previous panel crops or axis answers enter the workflow.

The application detects 37 of the generator's 39 physical panels. Earlier OCR
diagnostics replayed 37 prepared panels. The complete evaluation includes all
39 truth panels, 41 series and 706 points, including the two omitted panels.
All 23 source images reproduce exactly from their independent synthetic scenes.

| Result | Count |
| --- | ---: |
| Sources with a written export | 1 / 23 |
| Exported unique points matching pixels and values | 1 / 706 |
| Exported points without a matching truth point | 1 |
| Missing unique points | 705 |
| Correct relational export rows | 0 / 724 |

The one matched point is within the existing five-pixel and five-y-unit
tolerances. Its matched-only accuracy cannot establish overall success.
Series/phase relations still fail, and the two exported rows are extra under
the complete relational matching. All written files pass integrity checks.
Goal 22 remains incomplete; this is failed development evidence.

## Evaluation repairs

The offline checker previously accepted only an older seven-source report.
It now binds the declared report by checksum, authenticates its frozen model
and source manifests, and requires every ordered source and failure. Changed
identities, missing cases, altered snapshots and path traversal are rejected.
The old report reproduces every prior metric exactly with the inventory repair.

The first scoring attempt exposed an existing checker defect: it required
every row's series name and symbol to match the target intervention. Export
correctly preserves the shared baseline's own source-series metadata. The
checker now compares root metadata only with target-series rows and separately
requires each source series to remain consistent across rows and artifacts.
CSV/JSON agreement, checksums, values and full truth denominators remain checked.
The same saved export was rescored, with no inference rerun.

Twenty-one binding scenarios and 33 evaluator scenarios pass. The final tool
build has zero warnings/errors and takes 12.65 seconds. Truth reproduction takes
42.09 seconds; scoring takes 0.17 seconds. Earlier preparation, truth-count and
scoring failures remain preserved with source identities.

## Next work

Capture actual axis, OCR, accepted-marker and calibration evidence before a
synthetic workflow fails. Diagnose marker-spacing conflicts with printed axis
numbers, the two export rejections, and missing panels. Keep calibration and
export safeguards intact while repairing their inputs.

Existing V44 OCR detection, PP-OCRv5 recognition and latest completed V27 marker
weights are fixed. V27 remains failed-dev and unconsumed. No training, private
or sealed read, new model revision, production activation, package or release
occurs. The retained portable remains build 433, version 0.4.33.

The [evidence record](GOAL-22-COMPLETE-SYNTHETIC-WORKFLOW.json) binds source
snapshots, runtime, licenses, models, input PNGs, reports and checks. Model and
image payloads remain local.
