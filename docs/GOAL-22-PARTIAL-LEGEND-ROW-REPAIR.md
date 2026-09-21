<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recover a legend row from partial detections

An existing detector fragment vetoed the missing-row search even when the
original pixels contained a complete, independently measurable legend row.
The authenticated geometry replay reproduced this defect without inference.

The existing strict closed-frame, detached-symbol, component-count and
single-row checks now also support partial detections. Replacement requires
every overlapping detection to be wholly contained and unprotected. Complete
boxes, vertical text, numeric contexts and ambiguous frames retain their
protections. The recognizer reads the measured original-pixel crop; no expected
text is supplied. Confidence remains capped and Review remains required.
The opt-in recovery cache identity advances to v5.

## Verification

All 439 OCR tests pass, with 16 existing optional skips. All 478 application
tests pass. The native runner builds with zero warnings and errors. Twelve
added checks cover scale, prefix/suffix fragments, complete and protected
boxes, ambiguous frames, immutable pixels, idempotence, order independence,
one recognition call, review warnings and cached reuse.

On all 21 open development sources and 31 panels:

| Measure | Before | After |
| --- | ---: | ---: |
| Exact text readings | 338/453 | 339/453 |
| Matched text regions | 383 | 384 |
| Extra text regions | 134 | 132 |
| Missing text regions | 70 | 69 |
| Character errors | 863 | 843 |
| Correct roles | 354 | 355 |

All 338 prior exact readings remain. The only changed truth match is a
previously fragmented `Primary outcome` legend label. Markers remain 579/838
matched, with 86 extras and 259 misses. Axis corners remain 85/93 within two
pixels, with all three corners correct on 23/31 panels.

The full 23-source CSV comparison retains 12 exports, 11 review failures,
403/706 correct unique values and 399/724 correct rows. No wrong-scale or
failed-source residual rows. Six paired point positions across two sources
move by at most 0.231284 pixels and 0.140530 y units; x values and export
metadata remain unchanged. No added or removed point remains unpaired within
the existing five-pixel diagnostic radius. These are measured differences,
not identical outputs.

Eleven full-workflow panels change OCR geometry. Legend text stays the same;
one already malformed title/tick reading changes from `Outcom25` to `Outcom75`,
retaining its Other role. That unrelated recognition change's cause is not
independently traced. Every observed model uses CPU.

Tests/build take 98.47 seconds, native verification 135.03 seconds, and full
CSV verification 145.40 seconds. A geometry-replay preparation initially fails
before inference on a `JsonElement.RootElement` compile error, then succeeds
after repair in 9.56 seconds. Both attempts are retained. Native exit code 1
preserves existing calibration failures and does not constitute acceptance.

[Three source bindings and 53 evidence references](GOAL-22-PARTIAL-LEGEND-ROW-REPAIR.json)
bind tests, original inputs, models, runtime assemblies, geometry replay,
complete result comparisons and output deltas. Commands are the OCR/App
`dotnet test` suites, native-runner `dotnet build`, frozen synthetic workflow
execution, and the unchanged OCR, marker, axis and CSV scorers. The preceding
3adf5f2 checkpoint passes CI 35614862196.

## Remaining work

Accuracy gates still fail. Continue the missed-marker and OCR diagnosis,
then sealed prerequisites, real acceptance, production activation and both
final 2.0.0 distributions. No private/sealed read, optimizer step, dependency,
model payload, license or approval change occurs. No package is created;
build 433 remains 0.4.33. All four Goal 22 outcomes are not jointly verified.
