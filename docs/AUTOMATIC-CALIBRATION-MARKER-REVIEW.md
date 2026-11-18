<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Marker review after automatic calibration fails

On one designated development graph the automatic evaluation produced **zero
of ten reference rows**. The unapproved candidate completed axis detection,
OCR and marker classification. Ambiguous axes, competing numeric readings on
eight X labels and three Y labels, and a first-session conflict prevented
calibration. Phase assignment and export were never reached.

## Automatic results

The workflow received the embedded image alone. No manual anchors, marker
centers, numeric labels, session origin or phase names were supplied to it.
Scoring used the original digitizer centers and a separately stored numeric CSV.
The same original panel and the same model configurations were used both times.

| One panel | Baseline at `cad76a11` | With pixel review |
| --- | ---: | ---: |
| Marker candidates available to review after refusal | 0 | 51 |
| Editable data points | 0 | 0 |
| Valid calibrations | 0 | 0 |
| Exported rows / reference rows | 0 / 10 | 0 / 10 |
| Matched / missing / extra sessions | 0 / 10 / 0 | 0 / 10 / 0 |

Of the 51 candidates, **4** matched a reference center, **47** were extra and
**6** reference centers were missing. Matching was one-to-one within five
original-image pixels. Baseline internal centers were unavailable for scoring
after refusal. Both runs have zero comparable Y values. The reference contains
no phase labels. The baseline took 25.967 s. The run with pixel review took
28.972 s including the WPF review callback.

The checksum-bound evaluation used the production candidate workflow locally
on CPU with one inference thread. Its detector was `graph-scale-coverage-db-head-v46-p1`
0.0.1, its recognizer `PP-OCRv5_server_rec` 5.0.0, its marker-center model
`marker-center-component-diversity-v27` 0.0.1 and its classifier
`graph-marker-classifier` 0.1.0. The ManualPreview Analyze command remained
disabled. This evaluation used zero sealed inputs.

## Review behavior

A recoverable calibration failure now carries the accepted marker centers in
original panel pixels. The review surface verifies their run, project, panel,
image and bounds before offering **Show uncalibrated marker candidates**. The
toggle starts off. Turning it on draws dashed boxes. The next analysis clears
the previous boxes. They are transient and create no project points, session
values, phase assignments or export rows.

A nonactivating Windows WPF window rendered boxes at all 51 supplied locations
on the real panel with zero data rows. An owned native regression exercised the
final opt-in control through its automation peer. Two known boxes appeared at
their original coordinates, disappeared on the second toggle, and left the
point table and calibration empty. Batch regressions check panel identity,
rejected coordinates and preservation of prior review state.

The automatic numeric result on this graph remains a failure. The change
recovers visual evidence for a reviewer and leaves model selection, decision
thresholds and the calibration guard intact.
