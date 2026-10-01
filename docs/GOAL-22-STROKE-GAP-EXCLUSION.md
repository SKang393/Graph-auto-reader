<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Original-pixel stroke-gap marker exclusion

Two open-triangle proposals occupied white gaps between thin annotation and data
lines. Both were more than 18 pixels from a true marker and lacked numeric x
assignments, blocking their entire source exports. The [diagnosis](GOAL-22-STROKE-GAP-DIAGNOSIS.json)
was frozen before implementation. Its 210-panel shadow audit selected exactly
those two points; an outline-only rule was rejected because it would remove real
crosses misclassified as diamonds.

The new check runs after recovery and before calibration. It requires an open
closed-outline shape, a background center, no enclosed outline, only thin local
ink components, and one component spanning the predicted diameter. Ink centers,
closed outlines, two-dimensional components, large radii and clipped windows
remain eligible. It uses original pixels, preserves the classified candidates and
records rejected marker IDs in provenance. Adapter cache identity advances.

## Fixed workflow results

V31 is a closed failed development candidate used as a fixed regression context.
A fresh baseline on commit 6f03de45e2efac7d6ef96ca597946cc85bca1be1 isolates this
runtime change from the intervening legend repair. No model or operating point
changed within either comparison.

| Metric | Retained V44 / English / V27 / V5 | Closed V31 / V5 context |
| --- | --- | --- |
| Native matched markers / 838 | 742 -> 742 | 755 -> 755 |
| Native extra markers | 26 -> 26 | 13 -> 13 |
| Completed CSV sources / 23 | 15 -> 15 | 13 -> 15 |
| Correct CSV values / 706 | 414 -> 414 | 331 -> 411 |
| Correct CSV rows / 724 | 409 -> 409 | 328 -> 408 |
| Actual CSV rows | 426 -> 426 | 339 -> 421 |
| Wrong-phase rows | 3 -> 3 | 3 -> 3 |

All earlier matched native markers and their classified properties are retained.
Literal OCR metrics, axis geometry and physical upstream marker evidence are unchanged.
Text-exclusion IDs differ in 89 panels because panel/run identities change; their
original-pixel geometry and exclusion membership were independently reconstructed
and verified, including candidates already removed by the legend-frame guard.
The two exclusions restore two V31 source exports. No earlier exported numeric
row is removed or altered. Every one of the 847 emitted rows has finite
original pixels, finite x/y values and valid calibration. Wrong-scale, duplicate
and failed-source residual rows remain zero. Calibration differences, if any,
are explicitly bound in the [machine-readable outcome](GOAL-22-STROKE-GAP-EXCLUSION.json).

## Validation and timing

All 542 application tests pass, including original-pixel conservation, real
crosses, closed outlines, clipped windows, cancellation and an import-to-export
case that retains the rejected marker in review evidence. App and native-runner
builds have zero warnings and errors. Checks took 100.632
seconds. The current V31 baseline took 344.262
seconds and four patched workflows took 700.819
seconds. Jobs ran serially with 12 processors eligible, Idle priority and the
shared 80 percent CPU guard.

The first build failed CA1861 in two test assertions. The next attempt passed
535 tests and exposed seven fixture failures: six classified blank pixels as an
open square beside a legend edge; the seventh used the fake all-black OCR path
for a real image. Corrected physical fixtures retain the same marker locations
and boundary assertions. The production predicate did not change. Both failed
attempts are preserved and no workflow inference ran during them.

Commands are archived in `Run-StrokeGapChecksV3.ps1` and
`Run-StrokeGapEvaluationV3.ps1` under `artifacts/goal22-tools/`. They build and test
`GraphReader.App.Tests`, build `GraphReader.RealAcceptance.Ocr`, then run its
frozen-candidate synthetic workflow and existing native/CSV scorers. Inputs,
runtimes, source snapshots, models, manifests, notices, raw observations and
outputs are checksum bound in the outcome.

## Scope and remaining work

New source retains Apache-2.0 headers. No dependency or model payload changed.
V44/English/V27/V5 remains retained; V31 ledger row 95 remains closed and failed.
This repair neither reopens training nor approves V31. Remaining native misses,
false positives, phase errors and failed CSV sources still prevent Goal 22
acceptance. V46/server was shadow-audited but not rerun for this guard.

No private/sealed reads, optimizer steps, production activation, packaged build,
tag or public release occurred. Build 433 remains version 0.4.33.
