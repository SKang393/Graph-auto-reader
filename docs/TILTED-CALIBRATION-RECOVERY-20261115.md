<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve actual tilted calibration anchors and automatic failure details

Runtime baseline: `991b3f3031ac315e8b93692a15de30fcebaa0b29`, the verified
0.4.55 internal preview. Worktree integration baseline:
`84c2bc2b599caf680b24eb7760b8b509e36a57f9`.

## Measured problem and boundary

The retained fixed twelve-development-panel report has eight OCR-returned
panels and four geometry refusals. All four report Rust `AMBIGUOUS_AXIS`
followed by native `OPENCV_AXIS_AMBIGUOUS`: three exact-pair budget refusals and
one `native_rotated_geometry`. The wrapper discarded those native reasons
when the fallback also lacked geometry, and the workspace presented only a
generic unavailable message. This conclusion uses the existing report, SHA-256
`fd4ffedb8a462825995556b8b3ee3059f9facd8f829d53fad578107df1900ba5`;
no image, OCR or geometry inference was repeated.

Manual calibration also rejected actual tilted anchors or evaluated X and Y
independently. Replacing an observed tilted basis with an axis-aligned bounding
box would change the scientific mapping. The repair uses the actual three
supplied pixel anchors. This is a manual recovery path and a coordinate
prerequisite, not automatic rotated OCR or a real-graph accuracy improvement.

Acceptance is structural: supplied original-pixel anchors must map to their
entered values; interior points, crop offsets, ordering, save/reopen and export
must preserve that mapping. Legacy accepted records retain their mapping.
Unsupported downstream assumptions must produce a visible recoverable refusal.
No numeric tolerance, rank, candidate cap, recognition threshold, model or
sealed-set gate changes. Sealed-run budget/use is zero.

## Implementation

The affine inverse basis maps both pixel coordinates to graph coordinates.
Nonseparable transient states carry no misleading scalar X/Y transforms.
Point edits and magnifier hover use the same mapper. Observation order and
first-observation checks use mapped X for this basis. Ordinary manual requests
retain the prior two-pixel click tolerance and scalar behavior. Explicit
`PreserveAffineGeometry` requests preserve intentional nonaligned geometry even
inside that tolerance; exact alignment always retains the scalar path.

The frozen project schema is unchanged. Existing actual anchor records and a
null session lattice represent the affine basis; the existing open reason list
records `manual_affine_original_pixels`. Session and printed-X anchor kinds
remain distinct. This marker separates new, slightly tilted printed-X records
from historical records that accepted up to two pixels of misalignment and
used scalar transforms. Both historical session and printed-X mappings and
their phase behavior remain intact. Marked invalid, degenerate or incompatible
representations are rejected on restore.

Screen-vertical phase boundaries cannot represent arbitrary rotated phases.
Adding/moving dividers, applying tilted calibration with existing dividers, and
exporting a saved incompatible combination therefore fail before mutation.
The phase commands are disabled with a localized visible notice. Baseline-only
manual point editing/export remains available. Automatic detection and marker
exemplar admission keep their existing one-dimensional restrictions. No
fabricated session lattice or inferred scientific value is introduced.

The automatic failure-detail repair retains the original Rust failure and
bounded, recognized native reasons, without retrying a worker. Known budget,
rotation and timeout failures get localized recovery messages. Successful
geometry and cancellation keep their existing behavior. Unknown native details
are not presented as a recognized diagnosis. Stale completions cannot replace
the status of a newly selected tab.

## Owned verification and complete attempt accounting

All fixtures are project-owned blank rasters with explicitly supplied geometry
or stub services. These controls do not establish image-to-axis detection.

- Initial positive/negative two-degree controls failed 2/2 at the old anchor
  validation, as expected; reported test time 348 ms. Red TRX SHA-256:
  `dfc8f0c2b7601883b9bf40352cbaead1ef6b85f45f425601678bf28a9a96106b`.
- The first combined build stopped before tests on CS0219, an unused test
  constant after correcting the hover midpoint fixture. Wall time 68.5859115 s.
  The fixture correction compiled with zero errors in 10.7718772 s.
- Review found residual one-dimensional span checks rejecting invertible
  ninety-degree bases. Those checks now apply only to scalar input. Owned
  positive/negative ninety-degree save/reopen/export cases include a first
  session greater than one and its explicit origin receipt.
- The first affine App/test compilation passed with zero warnings/errors in
  34.9615906 s. The focused suite passed **84/84**, zero failures/skips,
  7.3085675 s wall, about four seconds reported test time. TRX SHA-256:
  `d8269d3da26533fb287fb3dbcfba8932f49349be6916546f241f1e74ccad847b`.
- Combined failure-detail compilation passed with zero warnings/errors in
  44.1825765 s; focused checks passed 108/108 in 5.6173303 s wall. The first
  full App run then reported 1,247 passed, one failed, zero skipped, with a
  TRX span of 48.8405626 s. The failure was a real default-manual regression:
  `MarkerExemplarWorkspaceTests.OriginalBytesAndBindingGuardSeedCalibrationAndSeries`
  uses previously accepted two-pixel click drift. Treating every non-exact
  click as affine wrongly disabled its marker assistance. The test remains
  unchanged; the production request default is corrected as described above.
  Focused TRX SHA-256:
  `4ba5e33ad10992d21a2aad2153ee1d7eafb685ece6db43d2276cd7c00e86f632`;
  full TRX SHA-256:
  `40e8fd1dbabf9372435e1a2686595739adcad34b664e18e5ad513aa68a4a5560`.
  A post-run PowerShell metadata writer had a bareword `true` typo. It did not
  lose the TRX or rerun tests; command wall time for that run is unavailable.
- After the default-manual compatibility correction, App/test builds passed
  with zero warnings/errors in 33.2080836 s. Focused checks, including the
  unchanged marker-assistance test, passed **109/109**, zero failures/skips,
  5.742322 s wall. The single full App run on those same outputs passed
  **1,248/1,248**, zero failures/skips, 44.1311133 s wall and about 42 seconds
  reported test time. The earlier failure remains in the accounting above.
  Focused TRX SHA-256:
  `d025f72e890ccbabdbe61dc506097cae8a353ecc7b65f472fc6fa2d94b222928`;
  full TRX SHA-256:
  `4b22cbf6baf6255220e3c9caa101303c4dd6dea657e47c4d681b6535df5019c5`.

The focused suite includes session/printed-X, legacy two-pixel compatibility,
new sub-two-pixel affine printed-X, original anchors and crop offsets,
save/reopen/new edits, CSV/audit values, observation ordering, malformed data,
phase/detection refusal, magnifier coordinates and command/notice state through
Undo/Redo and tab selection. The UI geometry fixtures were supplied, not found.

Normal-host serial Debug builds use `--no-restore -m:1 -nr:false
-p:UseSharedCompilation=false -p:NuGetAudit=false`. Unchanged references were
reused for final App/test compilation with `-p:BuildProjectReferences=false`.
Final tests use `dotnet vstest` against the same built test assembly, with no
build or restore. Principal commands and focused filter:

```powershell
dotnet build src/GraphReader.App/GraphReader.App.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false -p:BuildProjectReferences=false
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false -p:BuildProjectReferences=false
dotnet vstest tests/GraphReader.App.Tests/bin/Debug/net10.0-windows/win-x64/GraphReader.App.Tests.dll "/TestCaseFilter:$filter" '/Logger:trx;LogFileName=compatibility-focused.trx' '/ResultsDirectory:artifacts/affine-manual-calibration-check'
dotnet vstest tests/GraphReader.App.Tests/bin/Debug/net10.0-windows/win-x64/GraphReader.App.Tests.dll '/Logger:trx;LogFileName=compatibility-full-app.trx' '/ResultsDirectory:artifacts/affine-manual-calibration-check'
```

```text
FullyQualifiedName~RotatedManualCalibrationTests|FullyQualifiedName~RotatedCalibrationHoverTests|FullyQualifiedName~PrintedXWorkspaceTests|FullyQualifiedName~ManualPanelCropWorkspaceTests|FullyQualifiedName~CalibrationTickViewModelTests|FullyQualifiedName~ManualAxisDraftTests|FullyQualifiedName~RustFirstAxisProposalServiceTests|FullyQualifiedName~MarkerExemplarWorkspaceTests
```

Reviewed OpenCV `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`
was restored and verified in source/test outputs after compilation. No
dependency, model, license, training, private/sealed evaluation, package or
public release was added. The current delivered build remains 0.4.55.

Final App DLL SHA-256:
`7a74e7d7cc0ca8aaf97d7c25057aa7a079ef91a98e874212311ef34fb2be4438`;
App EXE:
`1f51da839e6c84d726ddb9971534b34e4cb127f188b1fca7cb0303e40874b47a`;
test assembly:
`80e6ff672f9854dfd2ed68cc6babf404dd41ce35b3d867b67b3244e881243a1d`.
Compact local all-attempt outcome SHA-256:
`e05dc20337e21086d972c0da35a2afe4e985e2eefcd2b1924aa764c449d62aa5`.

Combined local App validation passes. Source checkpoint
`487bbaf1fa628165eebeb41f938d595170c236e0` was pushed to main and integrated in
both checkouts. Its [Windows CI run](https://github.com/SKang393/Graph-auto-reader/actions/runs/43763146799)
failed the full-solution build before tests:
`dotnet build GraphAutoReader.slnx --configuration Release --no-restore --nologo`.
Four CS8602 errors occurred at `RealGraphManualPortableValidationTests.cs:183,185`
and `ManualPreviewWorkflowSmokeTests.cs:170,171`, where aligned-only controls
dereferenced the now-nullable transforms without asserting the aligned mode.
Both controls now explicitly assert non-null X/Y transforms before testing
their values; no production code or data inputs change in this correction.

The corrected integration-test project compiled against the frozen checkpoint
runtime with zero warnings/errors, 12.3660604 s wall, using the same serial
Debug flags and `-p:BuildProjectReferences=false`. Its owned
`FullyQualifiedName~ManualPreviewWorkflowSmokeTests` filter passed 12/12,
zero failures/skips, 8.4846707 s wall, via `dotnet vstest` on
`tests/GraphReader.Integration.Tests/bin/Debug/net10.0-windows/GraphReader.Integration.Tests.dll`.
The private real-data opt-in test was not executed. Integration TRX SHA-256:
`6d6aae5f3265862c204ccc94ac1c979f9623bcefe95d04feacfff251477b81e6`.
Windows CI must pass on the corrected checkpoint before it is called green;
native UI behavior for this unpackaged change has not been checked.
Temporary owned test outputs remain active only through CI and outcome
integration, then retire under
the existing artifact policy. The current fixed-development report remains
the next geometry revision's comparison input.
