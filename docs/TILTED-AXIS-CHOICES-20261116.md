<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Retain tilted geometry in explicit axis choices

Baseline: `f3ea558749454cce232493285d24a94b0a68cc9b`.

## Defect and scope

The existing owned `CompleteAllRotatedSnapshotReturnsNoCandidates` control
demonstrates that the choice list discards a valid tilted fit even though the
[affine suggestion workflow](NATIVE-AFFINE-AXIS-REVIEW-20261115.md) already
supports its actual three anchors and rectified label-reading frame. The
development trace documents a rotated-geometry representation gap, but does
not establish how often a tilted chooser would help. No new private run is
needed to define this repair.

The transient candidate becomes a validated one-of rectangular or affine
geometry. Affine choices retain the fitted intersection, X endpoint and Y
endpoint in original pixels, with confidence zero and no best-choice claim.
The derived fourth corner must agree with the observed parallelogram. Existing
finite, bounds, orientation and nondegeneracy checks remain mandatory. Both
complete and explicitly incomplete searches can offer eligible choices.

The overlay previews the actual three anchors. A localized accessible option
label identifies the tilted frame. Explicit selection creates the existing
affine suggestion, starts with blank numeric values and unchecked review, and
reuses its working-frame, label review, refit and Apply/Undo behavior. No
axis-aligned bounding box or scientific value substitutes for the fitted frame.

## Compatibility and validation

The private worker accepts the legacy rectangular response and the new affine
variant, requiring exactly one geometry representation. Rust review remains
rectangular with measured ticks. Persisted project, vision-result and model
schemas are unchanged; saved calibration already stores three anchor points.
Automatic Suggest/Detect behavior, model assets, thresholds, work limits and
timeouts are unchanged.

Acceptance is structural: exact anchor preservation, validated alternatives,
source-bound selection, visible incomplete status when applicable, blank values
until read/entered, and explicit user review before applying calibration. Owned
controls cover native geometry/transport and headless selection through the
existing calibration flow. No sealed reads, training, model selection, private
truth scoring or repeated OCR/Mirage comparison belongs to this batch.

## Executed validation

The first App-test build stopped before tests with eight compiler errors in
**56.3372832 s**: one ambiguous `ToOriginal` point constructor, three ambiguous
Axis/Domain `PixelPoint` references, and four nullable rectangle uses in existing
tests. Explicit types and non-null assertions at the owned rectangle boundaries
resolved them. The production App had already compiled; no production warning
was suppressed. The corrected test build took **10.3431615 s**, zero warnings
and errors.

```powershell
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-build --no-restore -m:1 -nr:false --logger 'trx;LogFileName=app-full.trx' --results-directory artifacts/tilted-axis-choice-check
```

The full App suite passed **1,338 / 1,338**, zero failures/skips, in **47.3652665 s**
wall, finishing `2026-11-16T07:37:01.9501786Z`. TRX SHA-256:
`f786840a94b04b0b928eb272ca941d591ccc10cf46374e3c15b8da9a001e247c`.
Native raster tests used the already reviewed OpenCV DLL
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`;
optional Rust checks used the existing 457 packaged worker. All eleven protected
hashes and sixteen shared Data hashes remained unchanged.

Both complete and incomplete selected-affine cases passed the actual workspace,
working-frame and view-model flow with **fake tick/OCR receipts**. Each made one
candidate-review call, one partial-tick call and one label call, with no ordinary
Suggest or strict-tick call. Refitting retained six measured pairs, required
fresh review, applied mapped original-coordinate anchors, mapped an owned point
to `(2, 5)` and restored the prior calibration with Undo. Original PNG bytes and
raw receipt values stayed unchanged. This verifies integration, not OCR or
detector accuracy on images. Native visual behavior remains unverified.

Mixed and wholly tilted candidate tests, incomplete tilted prefixes, malformed
one-of responses, nonfinite/reflected/out-of-bounds frames, defensive copies and
stale/forged selections also passed. `RotatedCount` records encountered tilted
frames; new `RetainedAffineCount` counts only validated, deduplicated affine
choices actually returned.

The suite also includes the corrected capacity contract that failed the
[preceding full CI](INCOMPLETE-AXIS-REVIEW-20261116.md#full-ci-finding). It confirms
the incomplete prefix does not contain the deliberately unvisited global winner,
while complete automatic searches still select it. Full CI for this integrated
correction is pending. No package or model approval is claimed.

No dependency/license changes; project source remains Apache-2.0. Retire logs,
TRX and disposable compiler output after the coherent outcome is pushed and
active CI/review consumers close. Current originals, native-QA inputs, selected
runtime assets, and the sole 457 packaged build stay protected.
