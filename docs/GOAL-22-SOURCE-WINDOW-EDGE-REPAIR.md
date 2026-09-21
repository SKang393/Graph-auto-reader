<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Source-window text fragment repair

The source-scale detector could retain both a clipped word at an internal
window edge and its complete detection from the overlapping window. For
example, one window detected `Pr` while its neighbor detected the complete
word. The duplicate fragment prevented an otherwise valid legend row from
assembling.

The adapter now prefers a compatible horizontal detection from another window
only when it covers the fragment's horizontal extent and crosses that exact
internal boundary. Context, row overlap and relative height must agree. The
comparison uses image coordinates before mapping back to original pixels.
The complete detection may have lower confidence. Original pixels, ordinary
1200-pixel input behavior, same-window detections and model thresholds remain
unchanged. The detector cache fingerprint advances to version 2.

## Verification

- 478 application tests pass, including nine boundary, context and transformed
  coordinate cases. The native runner builds with zero warnings and errors.
- On all 21 open development sources and 31 panels, exact text readings rise
  from 332 to 334/453. Both changes recover complete legend labels; all 332
  previously correct readings and all other truth-matched readings are retained.
- Matched text regions rise from 380 to 382. Extra regions fall from 143 to
  137, missing regions from 73 to 71, and character errors from 969 to 904.
  Correct roles rise from 351 to 353.
- Markers remain 579/838 matched, 86 extra and 259 missing. Axis corners remain
  85/93 within two pixels, with all three corners correct on 23/31 panels.
- The 23-source, 39-panel CSV regression has identical values, statuses,
  upstream observations and metrics: 12 exports, 11 review failures, 403/706
  correct unique values and 399/724 correct rows. Zero wrong-scale rows and
  zero residual rows from failed sources. Every observed model uses CPU.
- Tests/build take 86.44 seconds, native development verification 133.70
  seconds and full CSV verification 147.14 seconds. Native exit code 1 retains
  the existing calibration failures; it is not a passing product gate.
- The preceding `fdcaa91` checkpoint passes GitHub CI run 35611214038.

The original-pixel frame replay verifies all 31 saved panel records and both
runtime assembly hashes. It confirms usable frame and symbol evidence behind
the duplicate-fragment failures. This replay takes 9.31 seconds including its
build and performs no model inference; it does not replace full OCR execution.

[Source hashes, run bindings and results](GOAL-22-SOURCE-WINDOW-EDGE-REPAIR.json)
record 42 local evidence artifacts. Validation uses `dotnet test` for the App
suite, `dotnet build` for the native runner, the frozen synthetic workflow and
the unchanged original-pixel OCR, marker, axis and CSV scorers. No private or
sealed inputs are read, no training occurs, and no model or dependency changes.

## Remaining work

Horizontal internal window boundaries are covered; this repair does not add
vertical fragment recovery. OCR and marker accuracy remain below their gates.
Multi-row legend completion, real acceptance, production promotion and both
final 2.0.0 distributions remain unfinished. No packaged build is created;
build 433 remains 0.4.33. All four Goal 22 outcomes are not yet jointly verified.
