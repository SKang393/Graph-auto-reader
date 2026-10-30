<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR request snapshot and calibration recovery

The OCR request cache previously accepted supplied detector regions before
validating their coordinate space. Mutable caller-owned regions, dividers,
strokes, and pixel buffers could also change after a cache key was derived but
before asynchronous processing completed. The pipeline now snapshots the
request inputs, validates supplied regions and image coordinate spaces and
transforms before cache lookup, and carries the same private image planes
through the direct, dual, V8 and V9P3 paths. Equal memory segments share one
copy. Callers must still avoid writing to a buffer during the initial
synchronous copy. The managed-memory check is an estimate, not a guarantee.

When automatic calibration fails, the app now selects the affected panel only
if the retained evidence identifies one current-project panel and original
image. Ambiguous, missing, or mismatched evidence produces a separate message
without directing the user to calibrate the currently selected graph.

Release checks on October 30: 821 OCR tests passed, 13 optional tests skipped;
701 App tests passed; and 45 manual/production integration tests passed. A
blocking-cache regression reproduced the input mutation bug before the fix.
These are correctness and recovery checks, not evidence that automatic model
accuracy, production approval, or release acceptance passed. No training was
run.
