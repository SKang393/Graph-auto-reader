<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recover labels on left-aligned axes, 2026-09-21

Axis-label recovery now measures both possible y-label edges. When printed
numbers share a left edge, changing digit counts no longer disqualifies an
otherwise complete original-pixel crop. Existing right-edge and x-axis lanes
are preserved. No expected number, numeric sequence or truth geometry supplies
the recovered text. The recovery cache identity advances to v3.

On the unchanged 21-source development check, four missing regions recover.
Three are read correctly, increasing exact readings from **337 to 340/453**
and correct roles from 349 to 353. Every earlier reading is preserved.
The fourth is read as `1.50` instead of `150`; its calibration remains
`NeedsReview`. All four additional readings carry explicit review warnings.
The four affected calibration records change internally, but retain their
previous blocked validity states. This is not a claim of unchanged calibrations.

| Check | Before | After |
| --- | ---: | ---: |
| Matched text regions | 381/453 | 385/453 |
| Exact text readings | 337/453 | 340/453 |
| Extra / missing text regions | 118 / 72 | 118 / 68 |
| Character errors | 867/3100 | 858/3100 |
| Correct marker centers | 666/838 | 666/838 |
| Correct axis corners within two pixels | 84/93 | 84/93 |
| Full-workflow exports / failures | 12 / 11 | 12 / 11 |
| Correct unique values / relational rows | 403/706 / 399/724 | 403/706 / 399/724 |

The separate 23-source, 39-panel CSV fixture has identical OCR, axis,
calibration, source completion states, errors and exported numeric values.
Three existing wrong-phase rows remain counted. There are zero wrong-scale,
duplicate or failed-source residual rows in this fixture. All observed model
executions use CPU. No detector weights or operating thresholds change.

Validation passes **452 OCR tests**, with 16 existing optional skips, and all
**486 App tests**. Five added cases cover left alignment, both possible edges,
unestablished alignment and original-pixel recognition of numeric and
non-numeric text without replacing prior readings. The native tool builds with
zero warnings and errors. Checks take 101.37 seconds, the native development
run 146.15 seconds and the full CSV run 159.69 seconds.

The first test build stopped on CA1861 in a test assertion. Its exact source
snapshot and log remain recorded as a void attempt. The corrected assertion
passes; no inference or sealed budget was consumed by the failed compile.

Commands use `dotnet test` for `GraphReader.Ocr.Tests` and
`GraphReader.App.Tests` in Release, then the native tool's
`--run-frozen-candidate-synthetic` and `--score-frozen-workflow-csv` operations.
Exact options, source hashes, results and the wrong-reading review state are
bound in the [evidence record](GOAL-22-TICK-ALIGNMENT-REPAIR.json).
The [coverage diagnosis](GOAL-22-REMAINING-COVERAGE-DIAGNOSIS.md) records the
input defect and closes two unsuccessful marker approaches without integration.

OCR still falls below the unchanged 95% bar. Missing detector regions,
component merging and absent initial tick anchors remain. Continue accuracy
work, sealed prerequisites, real workflow/Chandler verification, promotion and
both final 2.0.0 distributions. No private/sealed access, training, new model
import, approval or package occurred. Apache-2.0 project code and existing
reviewed dependencies are retained. Build 433 stays 0.4.33, and all four Goal
22 outcomes remain incomplete.
