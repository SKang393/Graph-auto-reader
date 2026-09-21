<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Complete short rows inside an established legend frame

The original-pixel diagnostic found complete legend labels that established
their enclosing frame, alongside shorter rows left split into separate words.
The completion path required each partial row to establish a small frame
independently, so a taller multi-row frame prevented completion.

Completion now reuses a closed frame established by another complete,
unprotected row. The partial row must lie inside that frame and have its own
unique detached symbol. Ambiguous frames, protected contexts, separate rows,
large gaps and frame-connected strokes retain their existing protections.
The recognizer reads the resulting original-pixel crop; no text is supplied
by a vocabulary or expected answer. Recovery cache identity advances to v4.

## Verification

All 427 OCR tests pass, with 16 existing optional skips. All 478 application
tests pass, and the native runner builds with zero warnings and errors.
Seven added cases cover two scales, separate-row preservation, idempotence,
order independence, missing symbols, missing borders and protected anchors.

On all 21 open development sources and 31 panels:

| Measure | Before | After |
| --- | ---: | ---: |
| Exact text readings | 334/453 | 338/453 |
| Matched text regions | 382 | 383 |
| Extra text regions | 137 | 134 |
| Missing text regions | 71 | 70 |
| Character errors | 904 | 863 |
| Correct roles | 353 | 354 |

All 334 prior exact readings are retained. Three legend labels recover: one
`Maintenance probes` row and two `Treatment B` rows. A participant reading
also changes from `Participant O1` to `Participant 01`. No participant or
recognizer code changed; that additional correction's cause is not independently
traced and is not presented as a direct legend-completion result.

Markers remain 579/838 matched, with 86 extras and 259 misses. Axis corners
remain 85/93 within two pixels, with all three corners correct on 23/31 panels.
The full 23-source CSV comparison has identical observations, values, statuses
and metrics: 12 exports, 11 review failures, 403/706 correct unique values and
399/724 correct rows. No wrong-scale or failed-source residual rows. Every
observed model uses CPU.

Tests/build take 142.19 seconds, native verification 135.10 seconds and full
CSV verification 146.44 seconds. The native exit code 1 retains existing
calibration failures. These checks do not constitute a product acceptance pass.

[Source bindings and 44 evidence references](GOAL-22-SHARED-LEGEND-FRAME-REPAIR.json)
include the unchanged synthetic inputs, models, prior frame replay, complete
result comparisons, test logs and runtime assembly hashes. Commands are the
OCR/App `dotnet test` suites, native-runner `dotnet build`, frozen synthetic
workflow execution, and the unchanged OCR, marker, axis and CSV scorers.

## Remaining work

The model accuracy gates still fail. Missing or heavily fragmented labels,
marker accuracy, sealed prerequisites, real acceptance, production activation
and both final 2.0.0 distributions remain. No training, private/sealed read,
model payload, dependency, license or approval change occurs. No package is
created; build 433 stays 0.4.33. All four Goal 22 outcomes are not yet jointly
verified.
