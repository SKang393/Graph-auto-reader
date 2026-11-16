<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Retain independently measured ticks during affine label review

## Problem and bounded change

The [initial partial-tick implementation](MANUAL-AFFINE-PARTIAL-TICKS-20261116.md)
retained one observed axis only after the user manually marked the tilted frame.
The same explicit label-review workflow used strict measurement for an
automatically suggested affine frame. If either span failed, it discarded both
measured tick lists before OCR, even when the other side passed the unchanged
origin, full-span, tick and shared-work-budget checks.

`ManualPreviewWorkspaceService.ReadRectifiedCalibrationLabelsAsync` now uses the
existing partial-measurement route for both sources of affine geometry. Its API
and worker flag retain their `ManualPartial` names for compatibility. This route
serves explicit label review; it does not approve geometry or alter the production
automatic pipeline. Automatically suggested frames retain their native provider,
review reasons and original-pixel coordinates. Rectangular review, the normal
worker mode and existing adapters' strict fallback are unchanged.

All source/frame currentness, derived-image hash/dimensions, exact-box receipt,
response binding and tick-array echo checks remain in place. The failed side has
no measured ticks. OCR may independently read labels there using its existing
estimated-position path; an empty measured list is not proof that labels are
unreadable. Missing values still require human input, and Apply requires fresh
review. No missing endpoint, label or scientific value is fabricated.

The acceptance bar is preservation of the independently valid side, honest
absence on the other side, unchanged provenance and no calibration mutation
before complete values plus explicit review. No threshold, window, bridge cap,
model, schema or export rule changes. The prior private source-support and
clipped-window investigations remain closed. Private and sealed reads: **zero**.

## Owned regression and integration validation

Two owned regression rows use the existing fake automatic geometry service and
literal-label reader. The tick fixture explicitly refuses strict one-span
measurement, while partial measurement returns only the valid X or Y side.
This tests orchestration and review behavior, not automatic geometry or OCR
accuracy on real images.

The focused unchanged-source run produced **zero passed, two failed, zero
skipped**, with both failures reporting the expected partial-call count of one
versus the actual zero. Test duration was **715 ms**; command wall time
**16.156442 s**. Its filter was:

```text
FullyQualifiedName~AutomaticAffineReviewRetainsIndependentTicksAndRequiresHumanCompletionBeforeApply
```

Two earlier attempts were void before test execution: `NETSDK1004` because the
completed App test assets had been retired (**1.5180861 s**), followed by
`CS0121` in the new owned test's target-typed point construction (**9.2625217 s**).
A restore from the existing local NuGet cache took **4.9650894 s**. Making the
point type explicit repaired the fixture; neither setup repair changed product
behavior or consumed an evaluation run. The red TRX SHA-256 is
`58f748d21c7b6d6c0c2bca5220816e03654161735daeace563492a9bb2170d75`.

The final controls verify one geometry, measurement and OCR call; the sole
measured side; unchanged native provider/frame/warnings; incomplete numeric
drafts; review invalidation; explicit Apply; inverse mapping to original pixels;
and Undo. A separate rectangular guard confirms that strict refusal still
withholds both measured arrays. Existing stale, cancellation and receipt guards
remain in the full App suite.

One integrated build passed with **zero warnings/errors** in **67.9449164 s**.
The complete App suite then passed **1,325/1,325**, with **zero failures/skips**,
**47 s** reported test duration and **51.4477164 s** command wall time:

```text
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-build --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --logger "trx;LogFileName=app.trx" --results-directory artifacts/affine-review-partial-check
```

The test environment selected the existing source Rust worker
`a20ced41ed47baf9daa879fa66c1e3d0e6ce658868fbccb2ba7334d3702281c6`.
After the ordinary build, the existing reviewed OpenCV runtime
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`
was staged in the App and test outputs before execution. No original data,
model, obsolete assembly or private baseline was substituted. All eleven
protected Data, QA-image, checkpoint, pointer and package hashes remained equal.
App TRX SHA-256:
`1e393dd2bc7069308a209879cb2b3817078d8f78fa00b96104d93bcee7971717`.
The compact six-command/attempt receipt is
`80d6c86da070bc02d8c5591c2f6c6ed3d8188737de291c5ff0f6bdb07b4cf990`.
The local compiler outputs predate the mechanical 0.4.57 version preparation;
the prepared source still requires exact-head full Windows CI before packaging.

## Delivery and retention

This source change is grouped with the [inference resource improvements](INFERENCE-RESOURCE-EFFICIENCY-20261116.md)
for one integration and CI batch. Version 0.4.57 is prepared once for that
meaningful preview. The delivered package remains 0.4.56 until a
replacement is built, uploaded, verified and activated. Native desktop QA is
still blocked by the helper's stale Escape stop despite renewed authorization.
Headless tests do not replace that acceptance.

After source/outcome integration, full CI and active consumers finish, retire
`artifacts/affine-review-partial-check` and inactive test outputs. Preserve original
data, the four source Data files, owned native-QA images, protected runtime
checkpoint and sole packaged build. No dependencies or licenses change; source
retains Apache-2.0 notices. No paid credits or reset redemption are used.
