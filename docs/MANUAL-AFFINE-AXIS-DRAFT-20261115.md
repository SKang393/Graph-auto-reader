<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve manually marked tilted axes during label reading

Baseline: `5920ebc2ddbf23dad73951c8e6e534cce9eeda30`.

## Failure and scope

The fixed development geometry record still contains three explicit pair-budget
refusals. Manual axis marking is the supported fallback, but source inspection
found that `CreateManualAxisDraft` accepts tilted points and constructs only a
rectangle for OCR. Its currentness check permits affine geometry only from the
native provider. This prevents manually marked tilted axes from using the
[existing reversible calibration reader](AFFINE-CALIBRATION-RECTIFICATION-20261115.md).
The defect is a representation mismatch; no new private comparison is needed
to demonstrate it.

Keep the legacy aligned-click path, including its existing two-pixel alignment
behavior. For truly tilted manual points, retain their exact O/Y/X geometry,
use a manual provider/reason and no detector confidence, and validate that bound
manual variant before reading. Reuse the existing derived raster, raw OCR/tick
receipts and inverse mapping. Draft creation and reading must not change the
calibration; application remains an explicit reviewed action. Do not attribute
manual geometry to an automatic detector.

The workspace axis-suggestion service, its manual-axis VM path, focused tests
and existing generic tilted-axis notice are in scope. No model, threshold,
search budget, immutable input, frozen schema, full Auto Detect or phase change
is proposed. The localized notice uses marked-axis wording for both provenance
types.

## Validation

Owned controls verify exact tilted-click preservation, rectified reading and
original-coordinate mapping, stale source/tab/frame refusal, no scientific-state
mutation before review, affine persistence after Apply, and unchanged legacy
aligned-click behavior. Existing calibration safety and culture-refresh tests
remain required. Build/test once after source review; then use one changed-stage
owned actual OCR check only if needed to validate the new manual provenance
route. Keep supplied geometry distinct from automatic calibration.

Acceptance is structural coordinate/binding correctness plus the existing
literal-label and review safeguards. There is no new model accuracy bar or
post-run threshold. Private and sealed evaluation budget/use is zero. Native
QA remains authorized but waits for Computer Use tool recovery in REV-011.

## Implementation and focused result

`CreateManualAxisDraft` now keeps the exact clicked origin, upper endpoint and
right endpoint when their geometry exceeds the existing two-pixel legacy
alignment rule. Its manual affine suggestion carries zero detector confidence,
the shared `manual_affine_original_pixels` provenance, and original image/binding
identity. The existing geometry validator checks finite bounds, orientation and
the fourth corner. The aligned rectangle path is unchanged. The view-model
notifies the existing tilted-axis notice when the third click completes.

Ten additional owned cases cover positive/negative tilt, shear, session and
printed-X reading, exact mapped anchors, explicit Apply, save/reopen/Undo, stale
source/tab/frame, and both two-pixel legacy boundaries. Existing rectified
receipt/crop/cancellation tests and language/accessibility tests are reused.
One full Debug build passed with zero warnings/errors in **133.7190828 s**.
The focused set passed **128/128**, zero failures/skips, in **16.2129413 s**.
There were no failed build or focused-test attempts. Independent bounded source
review found no actionable defect in the manual path.

The one full solution run on the same outputs exited 1 after **115.7673348 s**:
**3,173 passed, one failed, 32 expected skips**, thirteen projects and 3,206
total. All **1,306 App tests** passed. The single failure was the pre-existing
`AffineRasterRectifierTests.CancellationDuringLargeRasterWorkStopsRectification`:
an exact-type assertion expected `OperationCanceledException`, while cancellation
inside the existing paced `Task.Delay` returned its valid `TaskCanceledException`
subtype. The test now accepts the cancellation hierarchy and additionally
requires the original cancellation token and unchanged input pixels. Production
cancellation, pacing and timeouts are unchanged. Only the Imaging test project
was rebuilt, with zero warnings/errors in **4.2512309 s**; its complete suite
then passed **31/31**, zero failures/skips, in **2.639955 s**. The other twelve
full-run suites remain green. This is a repaired component rerun, not a second
green full-solution invocation. The original failed full-run evidence is retained.

Commands used the existing cache, disabled restore/audit and private/model
opt-ins, and serialized build/test work:

```text
dotnet build GraphAutoReader.slnx -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet vstest <App.Tests.dll> /TestCaseFilter:<manual/rectified/affine/VM/localization/shell filter> /Logger:trx
dotnet test GraphAutoReader.slnx -c Debug --no-build --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --logger trx
dotnet build tests/GraphReader.Imaging.Tests/GraphReader.Imaging.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:BuildProjectReferences=false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test tests/GraphReader.Imaging.Tests/GraphReader.Imaging.Tests.csproj -c Debug --no-build --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --logger trx
```

Focused TRX SHA-256:
`5efabd93cd9b6ef318e968561d18743862199419f229e7800e5ae180908ad9ff`;
original full summary:
`470714392abb8353ad55fe42ed0a065fd44b1e84ed0b48e588adc3890efcb4c0`;
repaired Imaging TRX:
`5a3fc451838d5259c44964c620f53afc7ef9510f2eab65bc8582a9608e8f4061`.
All four existing App Data files remained byte-identical. The frozen twenty-file
runtime manifest is
`a1de99f6af70bb85824eef14614b27648b60cb142d7f5d211638d8907ddb027a`;
App DLL `0a6bdd4624c5f342c186d190f7637677a911b49a95aff9c42f0eb0678d90cd16`,
EXE `ff6d9f1f7ff9f8c952df5e9dede9260db5afdbb29aeb65eb705b55f6be8186ee`.
All twenty source/test libraries matched. Reviewed OpenCV and Rust identities
remain unchanged. These are unpackaged Debug runtime identities.

The owned probe adds one manual route using the retained positive-tilt PNG,
without rendering or copying an image. It has no automatic axis service; exact
supplied geometry is explicitly identified as manual. It reuses runtime/model
preflight and the existing read/fit helper, retains incomplete/refused results,
and exits nonzero when the required draft or source/state preservation fails.
Its deadline is 120 seconds with a 125-second watchdog. The old eight-path
report shapes are preserved. No private inference is required by this route.

## Actual manual-route worker check

The probe-only build passed first attempt with zero warnings/errors in
**8.1795007 s** wall (**7.74 s** MSBuild). It verified the frozen App/Rust
identities, all twenty source/probe libraries, and the unchanged reviewed OCR
model, manifest and notices. The one command was:

```text
GraphReader.CalibrationOcrProbe.exe --owned-manual-affine-calibration-check <existing-positive_tilt.png> <frozen-App.exe> <reviewed-OCR-assets> <Rust.exe>
```

It ran **2026-11-16 00:11:19.0977966 to 00:11:23.1555701 UTC**, exited zero
with empty stderr and took **4.0496994 s**. The real manual draft retained its
manual provider and zero detector confidence. Rectified reading returned all
six literal numeric labels, three measured X ticks and three measured Y ticks,
and a complete six-pair review draft with no estimated positions. No stage
failed, timed out or was refused. Original-mapped anchor residuals were below
**1.3e-13 pixels**, reported as floating-point evidence rather than a new bar.
The 6,898-byte source PNG retained SHA-256
`2a6b7431217fcc2267471c13601e92316a3a9098fd60e5d8b2301f4e8bf426ba`;
project/calibration state remained unchanged. No automatic geometry, Apply,
export, private input, sealed evaluation or new image was used.

The verified CPU OCR worker reported **1,847.4124 ms** total, including
**624.0099 ms** initialization and **999.2138 ms** recognition. The parent
budget remained **416/10000 on twelve logical processors**, or **0.4992 logical
cores**. This is one cold owned control, not a real-graph latency comparison.

Probe DLL SHA-256:
`4dd30b97e19a1e1f51242a88afaf332c821843841ba10bd64dea1c961f479494`;
report: `027e624c0cb16209792cc83c41ab9bd16f8acc583484becdba1a3433a6eb4b7c`;
run receipt: `35a4ec76e8c6f2b2dcb3c8fbee3af0deccb50f7542bc214c216797f571f36631`.
Generated outputs remain only for current integration/CI and native-check
handoff; retire them after the concise record is pushed and those consumers
finish. The original user data, four retained owned native inputs and sole
delivered package remain outside this cleanup scope.

Source/outcome commit `bb40265adc0a37b5906ed38f86a9e18ffae25d47` is pushed and
integrated in both checkouts. The unrelated primary edits retained their hashes.
The primary fast-forward initially refused the restored source file's checkout
normalization; HEAD, index and normalized working blob were proved identical
before restoring only that clean file and completing the fast-forward. No user
edit was discarded. Version metadata is prepared for internal preview 0.4.56;
the actual delivered keeper remains 0.4.55 until verified replacement.

The exact-head
[Windows CI 43779136477](https://github.com/SKang393/Graph-auto-reader/actions/runs/43779136477)
passed for `bb40265`, from **2026-11-16 00:13:59 to 00:26:43 UTC**:
**3,176 passed, 30 expected skips, zero failures** across thirteen .NET projects,
including **1,306 App** and **31 Imaging** tests. All **72 Rust** tests and
**68 packaging verification** controls passed, as did localization, evidence,
handoff, upload and packaging audits. Release jobs were skipped. This verifies
the initial manual-draft batch, not the correction follow-up below.

Status: initial source integration, local validation, actual manual-route check
and exact-head CI pass. Native QA remains incomplete. After the maintainer
confirmed no new Escape, a fresh desktop
connection still reported its Escape stop without usable window state or input;
REV-011 records this tool blocker. One integrated internal preview is being prepared to deliver the
automatic tilted-frame, reversible reading, affine marker and manual fallback
changes together. This is not a production approval or public release.

## Correction-route follow-up

Before creating the prepared 0.4.56 package, bounded source review found the
same representation loss in
`MainWindowViewModel.CalibrationLabels.UpdateAxisSuggestionGeometryFromCorrectedAnchors`.
When an automatic rectangle is corrected with tilted clicks, the overlay retains
their two-dimensional positions but the OCR suggestion still uses independent
rounded X/Y components. The manual factory is currently selected only when the
previous suggestion was already user-defined. Its replacement branch also lacks
notifications for a change between aligned and tilted geometry.

The follow-up keeps corrected tilted geometry through the existing manual
factory, preserves legacy aligned corrections and partial numeric values,
invalidates old label evidence/review, and refreshes the geometry-kind notices.
Acceptance uses owned coordinate/state and fresh-review invariants; private and
sealed use remain zero. One owned regression should expose the defect against
`bb40265` before the small source repair. No package has been created and no
version is consumed again for this correction. The delivered keeper remains
0.4.55; the prepared integrated preview remains 0.4.56.

The four new owned cases reproduced all four defects against the unchanged
frozen App DLL `0a6bdd4624c5f342c186d190f7637677a911b49a95aff9c42f0eb0678d90cd16`:
orientation loss, missing geometry-kind notification, and invalid or source-stale
tilted corrections retaining a draft. The test-only build with
`-p:BuildProjectReferences=false` passed with zero warnings/errors in
**16.8189626 s**. The red run had **four expected failures, zero passes/skips**
in **2.7209122 s**, TRX SHA-256
`3067fcea182f6c26a438677213372e9c0ab0d524347fa0abd62c45c02f063606`.

The source repair extends the existing guarded manual-factory branch only to
rectangular proposals corrected beyond the legacy two-pixel alignment rule.
Aligned automatic corrections and the existing automatic-affine branch remain
unchanged. The replacement now notifies both geometry-kind and provider-warning
bindings. Tests also retain partial X values, invalidate the old rectangle
receipt, require newly read rectified evidence and fresh review, and preserve
scientific state until explicit Apply. Independent bounded source review found
no material issue. The red run is a regression demonstration, not an acceptance
pass.

One dependency-enabled App.Tests Debug build passed with zero warnings/errors
in **91.1517742 s**. The focused manual/rectified/correction/VM/localization/shell
set passed **114/114**, zero failures/skips, in **16.4570158 s**. The single
full App suite on those same outputs passed **1,310/1,310**, zero failures/skips,
in **76.6850995 s**. All four formerly failing cases passed. No source repair
or repeated green run was needed. A full local solution/probe/private comparison
was not repeated; the final source commit still requires its full Windows CI.

```text
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet vstest <App.Tests.dll> /TestCaseFilter:<focused correction and calibration classes> /Logger:trx
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-build --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --logger trx
```

Focused TRX SHA-256:
`1c0c2141caf5fafb8ef4337e7c7f3ad1af59dc375f7d6fe5d48d9faf43462c6b`;
full App TRX:
`0e4cebf5e69c80d54d45375c1702af20c53fb58c3534048326952234d4b84643`.
All four existing App Data files retained their paths, sizes and hashes.
The current twenty-library manifest is
`6d1db92f4c9d221db2e8434ac833568593fb5c79c0b283ec029d00acaca68396`;
App DLL `811d3c5c84c34f5112595dc52f25c90769212ac678896eb9949099c2e6d1b787`,
EXE `d79bac03aef6e3cb61d110e6506220aabdbfb445d8eda1a9e67361bb9f26b898`.
All twenty source/test libraries matched; reviewed OpenCV and Rust identities
were unchanged. No dependency, model, frozen schema, scientific threshold or
license changed. New code retains Apache-2.0 notices. Source validation passes;
final integration/CI, the one prepared package, and native QA remain pending.
