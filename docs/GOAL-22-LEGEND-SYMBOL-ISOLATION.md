<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Keep frames and text out of legend-symbol classification

## Problem

The independent symbol path reached the real classifier, but all 24 measured
symbol crops also intersected a legend frame. The classifier's normal crop is
2.25 times the marker radius, which brings nearby frame/text pixels into a legend
sample. Of 21 train symbols, only six had correct shape/fill and were accepted;
none of three located development symbols were accepted.

## Implementation

The classifier now supports measured original-pixel content bounds for selected
symbols. Sampling outside those bounds uses white padding, including bilinear
sampling neighbors. Pixels inside the bounds, patch scale, radius, trained weights,
and the 0.5 artifact-rejection threshold stay fixed. Ordinary point crops are
unchanged. Bounds are copied immutably, included in cache identity, and identified
by a distinct classifier stage version. Invalid or unsupported isolation fails
with retained earlier evidence. No stored image is changed.

## Files changed

- Marker classification options, patch extraction, and cache material.
- Production classifier adapter, symbol-input preparation, and workflow wiring.
- Focused classifier/workflow tests, this report, [aggregate evidence](GOAL-22-LEGEND-SYMBOL-ISOLATION.json), and readiness.

## Tests and commands

Release-mode `dotnet test` passes 33 classification tests with one existing optional
skip, plus 35 workflow tests. Tests verify selective pixel preservation, unchanged
point crops, rejection of invalid bounds/non-original frames, immutable bounds,
cache separation, and legend exclusion from CSV. Compilation reports zero warnings
and errors.

The local `LegendSymbolProbe` executes the actual production PNG decoder, symbol
locator and approved classifier. The preceding OCR output is replayed from its
authenticated report. Both runs cover the same 37 synthetic panels and 24 symbols,
using the same model, manifest, DirectML provider and artifact threshold. Synthetic
truth is used only after inference. The scorer retains all 41 known legend entries
in its coverage denominator and verifies the three fixed development source PNGs.

## Metrics and timing

| Measure | Before | After |
| --- | ---: | ---: |
| Located train symbols | 21 / 30 entries | 21 / 30 entries |
| Correct train shapes | 12 / 21 | 16 / 21 |
| Correct train fills | 16 / 21 | 16 / 21 |
| Correct and accepted train symbols | 6 / 30 entries | 11 / 30 entries |
| Located development symbols | 3 / 11 entries | 3 / 11 entries |
| Correct and accepted development symbols | 0 / 11 entries | 3 / 11 entries |

No shape-correctness result regresses. One development fill improves. All 24
symbols are accepted after isolation; remaining train shape/fill errors are still
counted. Application probes take 2.026 seconds before and 1.628 seconds after.
Truth scoring takes 16.012 seconds; the comparison reuses its authenticated truth
instead of regenerating it. These are local diagnostic timings, not a speed claim.

## License review

The existing Apache-2.0 classifier payload and notices are checksum-verified by
the normal model store. No dependency, font or weight is added. The new code is
Apache-2.0. No private or sealed image is read.

## Known limitations

Eight development legend entries are still not located. Train shape/fill errors
remain. A three-symbol development subset cannot establish the full stage's 95%
bar. OCR accuracy, real acceptance, full workflow accuracy and release readiness
remain unverified. This diagnostic executed DirectML; no new CPU-parity claim is
made. Original OCR inference was not repeated.

## Integration notes

The probe initially selected a source store containing extra files, then exposed
a case-sensitive checksum comparison in the probe. Both attempts stopped before
inference; the existing staged package store and case-insensitive digest comparison
resolved them. A missing patch-dimension argument was repaired before compilation
succeeded. A stale scorer source binding was corrected to the frozen preflight's
authenticated current binding without rerunning inference. All are recorded as
plumbing repairs, not model experiments.

No production approval, model revision, package, tag or release is created.
Portable build 433, version 0.4.33, remains retained. Earlier sources and executing
assemblies remain archived. Development continues during Windows CI.

## Acceptance status

**FAIL** for Goal 22 completion. The crop repair improves actual model behavior;
the four required product outcomes remain incomplete.
