<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Single-glyph OCR masks repaired, 2026-09-21

Unreviewed single-character annotations inside a plot now remain visible as
OCR readings with an explicit review warning, without automatically erasing
overlapping marker detections. A star read as `*` or a triangle read as `A`
cannot by itself establish whether those pixels are text or a plotted symbol.
The rule covers Unicode text elements and uses no character whitelist.

Words, axis digits, outside-plot text and human review decisions retain their
existing treatment. Text, alternatives, confidence, roles and geometry are
preserved. Result-cache identity includes the new mask policy. There is no
model, threshold, calibration rule or export-guard change.

## Observed result

| Same development inputs and models | Before | After |
| --- | ---: | ---: |
| Correct native marker centers | 721/838 | 726/838 |
| False / missing centers | 27 / 117 | 25 / 112 |
| Exact text readings | 345/453 | 345/453 |
| Full-workflow exports / failures | 12 / 11 | 12 / 11 |
| Correct unique exported values | 408/706 | 409/706 |
| Correct relational rows | 404/724 | 405/724 |

Every previous native truth match remains. The five recovered identities
exactly match the five traced single-glyph text exclusions. Nine native masks
and one full-workflow mask are withheld; every other mask and OCR region is
unchanged. The broader native fixture has 21 sources and 31 panels; the full
CSV fixture has 23 sources and 39 panels. Their denominators remain separate.

Every previous numeric export value remains and one physical row is added.
Source completion states and failure messages are unchanged. There are zero
wrong-scale, duplicate or failed-source residual rows. Three earlier phase
errors remain. Native precision/recall are 96.67%/86.63%, so accuracy acceptance
is still incomplete.

Axis geometry is unchanged. Five native calibration records change because
their point columns change: four remain `NeedsReview` and one remains
`InvalidSessionOrigin`. Its unsupported first-session inference changes from
25 to 48 but remains blocked, with exact x values unavailable. One full-workflow
calibration record changes and remains `Valid`; all previous exported numeric
values are preserved. No claim of unchanged calibration internals is made.

## Validation and retained failures

All 489 OCR tests and 494 application tests pass; sixteen optional OCR tests
skip. The native tool builds with zero warnings and errors. The new pipeline
tests cover symbols, letters, a combining-character glyph, warm-cache behavior,
ordinary words, outside-plot annotations and single-digit axis labels.

Checks take 98.05 seconds, native inference/scoring 147.47 seconds, and full
workflow plus its scoring retry 161.56 seconds of active execution. All observed
model providers are CPU. No inference is repeated for a reporting repair.

The first test build failed because a test directly referenced an internal
helper. Its source snapshot and compiler output are preserved; the revised
tests use the public pipeline. Full CSV inference then completed, but its scorer
pointed to that earlier failed build directory. A 1.70-second scoring-only retry
uses the verified build and saved evaluation input. The first effect audit
counts timing changes; the normalized audit excludes only elapsed timings and
retains every substantive calibration difference.

The [evidence record](GOAL-22-SINGLE-GLYPH-MASK-REPAIR.json) binds 55 supporting
files, source snapshots, actual outputs and failures. The previous source
checkpoint `1b41fac` passes CI 35644736561.

## Remaining work

A real one-character annotation can still resemble a data symbol. Both remain
unreviewed with overlap warnings. Multiple-character false text and other point
and text detection failures remain. Continue their diagnosis, followed by the
required sealed and real-data checks, promotion and both final distributions.

No private/sealed input, training, new model import, dependency, production
approval or package occurs. Source and owned synthetic examples retain
Apache-2.0 provenance. Build 433 remains 0.4.33. All four Goal 22 outcomes remain
incomplete; this repair does not constitute a Goal 22 PASS.
