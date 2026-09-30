<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Server recognizer with repaired V46 context, 2026-09-30

After qualified-heading and legend-crop repairs, 43 matched native text regions
still have literal errors. This comparison replaces only the English recognizer
with the already reviewed PP-OCRv5 server recognizer. Detector, marker models,
runtime assemblies, thresholds, source pixels and truth remain fixed. The
existing runtime checks, 563 OCR and 520 App passes with 16 optional skips,
are reused without rebuilding or claiming another test run.

Two serial native/CSV runs take 421.982 seconds
under the shared CPU cap. All 21 native sources, 31 panels, 453 text truths and
838 marker truths remain scored. CSV retains all 23 sources, 39 panels, 706
points and 724 expected relational rows, including every failed source.

| Measurement | English reader | Server reader |
| --- | ---: | ---: |
| Exact native text | 392 / 453 | 412 / 453 |
| Geometry-matched text | 435 | 434 |
| Extra text regions | 125 | 123 |
| Character edits, 3100 truth characters | 342 | 316 |
| Correct native text roles | 395 | 399 |
| Matched native markers | 743 / 838 | 733 / 838 |
| Extra native markers | 19 | 20 |
| CSV exports | 15 / 23 | 16 / 23 |
| Correct unique CSV values | 414 / 706 | 429 / 706 |
| Correct relational CSV rows | 409 / 724 | 416 / 724 |
| Wrong-phase CSV rows | 3 | 11 |
| Correct dev values | 156 / 206 | 156 / 206 |
| Correct dev rows | 156 / 224 | 156 / 224 |

Exact text retains 383 earlier readings, gains 29 and loses nine. Marker truth
retains 732, gains one and loses eleven. All eleven losses belong to the small
361-by-240 source. Its plotted-symbol row is recognized as a run of infinity,
degree and ideographic full-stop symbols. Unlike the lower-confidence English
reading, it receives a text mask over the plotted points. The current review
rule protects a single unreviewed annotation glyph, but not this symbol-only
run. This is a specific masking defect to investigate, not evidence to discard.

One train source gains an export after the server directly reads its numeric
tick labels. Dev accuracy does not improve. All 15 previous source exports
remain completed, but their numeric rows have changes; do not describe them as
identical. The server emits 440 rows, including 11 extra points, 13 extra rows
and 11 wrong-phase rows. Every emitted row has valid calibration and non-null
x/y. Scored wrong-scale, duplicate and failed-source residual rows remain zero.

The unchanged shared composed-OCR policy accepts detection recall only.
Precision, exact recognition, character error rate and role accuracy fail.
**Reject this diagnostic configuration for promotion.** Keep the English
recognizer and V44/V27/V5 baseline; V46 remains a closed diagnostic model, not
a newly approved training outcome. Follow up the symbol-only masking defect
and the eight additional wrong-phase rows before any further promotion claim.

The [diagnosis](GOAL-22-V46-SERVER-READER-CONTEXT-DIAGNOSIS.json) and
[outcome evidence](GOAL-22-V46-SERVER-READER-CONTEXT-OUTCOME.json) bind source,
runtime, shared bars, model and license hashes, protocols, paired results and
all audit outputs. The commands are retained in
`artifacts/goal22-tools/Run-V46ServerReaderContextEvaluation.ps1` and the
checksum-bound preparation and scoring scripts. Numerical policy functions
are reused directly; no sealed admission, reserve access or transport runs.

The recognizer remains the pinned Apache-2.0 intake and converted payload.
No new external file, dependency, training, private/sealed read, production
activation, package or release. WPF interaction was not manually exercised.
Build 433 stays 0.4.33. Goal 22 remains incomplete.
