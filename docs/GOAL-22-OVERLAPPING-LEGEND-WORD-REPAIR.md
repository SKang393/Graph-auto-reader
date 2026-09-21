<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Assemble overlapping word boxes in a verified legend row

The OCR workflow now reads two additional complete labels on the current
development fixture. Full CSV output values, statuses and accuracy remain
unchanged. The OCR gate still fails.

## Defect and repair

The existing framed-row assembler rejected every pair of overlapping detector
rectangles. Two saved development labels contain individually recognized word
fragments whose boxes overlap slightly. Those fragments never become a single
label even though original pixels establish a closed frame, separate legend
symbol and continuous text row.

Allow overlap when both boxes contribute horizontal extent. Retain the existing
frame, original-ink completion, vertical alignment, word-gap, orientation and
protected-context checks. Contained boxes do not establish separate words.
Recognition still reads the original crop; the code contains no label vocabulary
or replacement text. The recovery/assembly identity advances to v3 for cache
separation. Models, thresholds, axis geometry and masks are unchanged.

## Verification

| Measure, all 453 labels | Before | After |
| --- | ---: | ---: |
| Geometrically matched labels | 378 | 380 |
| Exact complete readings | 330 | 332 |
| Correct roles | 350 | 351 |
| Extra text regions | 147 | 143 |
| Missing text regions | 75 | 73 |
| Character errors, all 3,100 truth characters | 1,029 | 969 |

All 330 earlier exact readings remain exact. Two legend labels are recovered.
One already incorrect numeric reading changes from `1.00` to `1-00`, with its
role changing from tick to other. Its actual truth is `100`; neither reading
is correct. This secondary change is retained, not described as unchanged
numeric recognition or assigned a cause without a crop/inference replay.

Native markers remain 579/838 matched, 86 extra and 259 missing. Geometry
remains 85/93 printed endpoints within two original pixels. The independent
23-source CSV check has identical values, statuses, upstream observations and
metrics: 12 exports, 11 review failures, 403/706 correct unique values and
399/724 correct rows. Wrong-scale and failed-source residual rows remain zero.
Every model stage uses CPU.

Tests pass 420 OCR checks and all 469 application checks. Sixteen optional OCR
checks are skipped and are not counted as passing. The new scale-one and
scale-two cases verify overlapping rectangles, immutable pixels, order
independence, repeat application, contained rectangles and protected numeric
context. Existing incomplete-frame, missing/ambiguous-symbol, separate-row,
large-gap, orientation and cache tests still pass. Native build: zero warnings
and errors. Checks take 98.68 seconds, native comparison 133.79 seconds, and
the complete CSV workflow 146.46 seconds. Native exit code 1 preserves the
known workflow failures.

The [evidence index](GOAL-22-OVERLAPPING-LEGEND-WORD-REPAIR.json) binds source,
tests, executable/model inputs, full scores and per-truth changes. The previous
mask repair passed [CI 35609147880](https://github.com/SKang393/Graph-auto-reader/actions/runs/35609147880)
on `c820759`. No training, imported dependency, private/sealed access, model
approval or packaged build occurs. Project-owned code and synthetic evidence
remain Apache-2.0 with existing reviewed dependency/model notices unchanged.

Continue the remaining original-pixel legend-frame diagnosis and OCR/marker
accuracy work. Build 433 is 0.4.33. All four Goal 22 outcomes are not yet
jointly verified.
