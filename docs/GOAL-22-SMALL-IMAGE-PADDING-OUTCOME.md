<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Reject small-image padding removal, 2026-09-30

## Problem and attempted change

Two owned synthetic panels are smaller than the detector's 1200-pixel source
window: 361 by 240 and 863 by 395. Padding them to width 1200 reduces text size
under DB resizing. The initial hypothesis was that passing these original
images directly would recover missing labels. Both contained all missing text
inside the prepared image; no panel-crop loss was found.

The attempted change forwarded any image fitting 1200 by 1200 directly to the
existing detector and advanced its configuration fingerprint. Larger-window
padding, clipping and overlap stayed unchanged. Boundary, transformed-image,
pixel-preservation and tall-window tests passed. The current detector is the
adapted `graph-text-extent-db-head-v44-p1`, not the unmodified pretrained model.

## Validation and rejection

`artifacts/goal22-tools/Run-SmallImagePaddingChecks.ps1` built App and the native
workflow runner in Release and ran all 515 App tests successfully, with no
warnings, errors or skips, in 123.259 seconds.
`Run-SmallImagePaddingEvaluation.ps1` ran the fixed V27/V5 native and complete
CSV workflows serially in 414.531 seconds.
Every source, model, runtime, input manifest and report is checksum-bound in
the [evidence](GOAL-22-SMALL-IMAGE-PADDING-OUTCOME.json).

| Native measure | Retained baseline | Rejected attempt |
| --- | ---: | ---: |
| Text region matches / 453 | 392 | 377 |
| Extra text regions | 108 | 132 |
| Missing text regions | 61 | 76 |
| Exact readings / 453 | 348 | 337 |
| Correct roles / 453 | 360 | 352 |
| Character error rate | 23.4839% | 27.1290% |
| Marker matches / 838 | 733 | 729 |
| Extra markers | 25 | 26 |

The attempt loses eleven exact readings and four marker matches, with no
paired gains. Only the two small native panels change. All other 29 native
panels and all 39 CSV panels are preserved. Axis geometry is unchanged.
Every CSV source status, numeric value, phase and row count remains unchanged:
15/23 exports, 414/706 correct values and 409/724 correct rows. All 426 actual
audit rows have valid calibration and non-null x/y. Eight sources still fail;
their truth remains counted. The workflow harness exit 1 reflects those
retained review-blocked cases, not an engine crash.

Both edited files were restored to their exact prior bytes after verifying
their rejected bytes against the check-run snapshots:

- `src/GraphReader.App/Integration/Workflow/ProductionSourceScaleOcrDetector.cs`
- `tests/GraphReader.App.Tests/ProductionSourceScaleOcrDetectorTests.cs`

The verified runtime baseline remains commit `f3480d8`, whose CI run
36702173042 passed. Only this outcome and the readiness record are retained.
No identical baseline test rerun is needed after exact-byte restoration.

## Next diagnostic and limits

The regression is already present in raw detector proposals, before text
assembly and refinement. Removing padding is therefore not an established
repair. Inspect the adapted detector's training input geometry before another
scale change. A model response to scale is a hypothesis; this experiment does
not prove a coordinate-mapping defect or authorize another training recipe.
Source-derived tensor dimensions are not runtime instrumentation.

The existing 95% per-item bars and calibration safeguards remain unchanged.
No new dependency, model weights or threshold. Reviewed Apache-2.0 source and
models, owned synthetic train/dev only, zero private/sealed reads or optimizer
steps. One hidden Idle local job at a time, 12 CPUs and the shared 80% guard.
No production activation, packaged build or release. Build 433 remains 0.4.33.
The attempted repair and overall Goal 22 acceptance are **FAIL**.
