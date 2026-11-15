<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Retain fitted tilted axes as a distinct review proposal

Implementation baseline: `28c7fbb0ce6b639c938160387c20435849e66557`.
The [manual affine prerequisite](TILTED-CALIBRATION-RECOVERY-20261115.md)
preserves three-anchor coordinates; its latest CI restore edge case is included
in this coordinated validation. No new package has been produced.

## Defect, hypothesis and fixed scope

The existing twelve-panel development trace contains one completed native fit
refused as `native_rotated_geometry` during conversion to a rectangle. The
detector already emits the actual fitted parallelogram. Converting it to an
axis-aligned box would change its meaning; discarding it loses useful geometry.
Three separate pair-budget refusals are unchanged and remain explicit failures.

The isolated change retains a fitted nonrectangular frame for human review.
Acceptance is structural: preserve actual fitted origin/X/Y endpoints and
confidence after complete bounded enumeration, reject invalid or contradictory
transport, and never supply invented ticks, values, or a bounding-box OCR input.
Owned raster controls additionally exercise image decoding and OpenCV line
detection; supplied-line controls alone are not image-detection evidence.

The fixed twelve development selector, 120-development denominator and 51
reserved exclusions remain unchanged. Current comparison report SHA-256:
`fd4ffedb8a462825995556b8b3ee3059f9facd8f829d53fad578107df1900ba5`.
Sealed budget/use is zero. No private truth scoring, model/threshold selection,
training or unchanged OCR/Mirage rerun is authorized by this change.

## Representation and review behavior

The private App worker response adds optional `AffineGeometry`, containing only
three original-pixel points and the detector confidence. The rectangular
`Geometry` remains null for this variant. The new field is omitted from normal
rectangular responses. Existing clients that do not understand the variant
reject null-rectangle/null-error success rather than interpreting a fake box.
The persisted project and vision schemas are unchanged.

The parent validates exactly one success representation, provider, confidence,
finite bounded input points, the derived fourth corner, axis directions and
nondegeneracy. The fourth point uses the same arithmetic as the producer:
X endpoint plus Y endpoint minus origin. Original rank, candidate completeness,
256 pair-work bound, response sizes, deadlines and CPU limits remain unchanged.
The explicit rectangular candidate chooser is unchanged.

The workspace retains this as a distinct affine proposal, binds it to the
unchanged image/tab state and previews its actual three anchors. All suggested
numbers begin empty and review begins unchecked. The user enters the actual
values, confirms the marked points and visible zero, then applies calibration.
Session/printed-X mode is explicit; changing it during review clears the X
values and review confirmation without rerunning geometry. Apply uses the
explicit affine mapping and existing Undo/Redo/autosave path.

OCR and exact-box tick measurement refuse the affine variant, including direct
service calls. The combined suggestion command does not repeat geometry for an
existing affine proposal. A localized notice explains manual values are needed.
This is automatic geometry assistance, not automatic rotated OCR or complete
calibration. The existing restrictions on affine phase dividers, marker
assistance and full automatic detection remain visible.

## Owned validation

Owned tests cover transport validity, default-field omission, detector-to-frame
coordinates, tilted rasters, review without mutation, explicit input/confirmation,
mapped point values, Undo/Redo, coordinate-mode changes, invalid/stale replies,
and zero OCR/tick/redundant geometry calls. Actual positive and negative
two-degree raster controls passed, including original-byte preservation.

The first full-solution no-restore build stopped with 14 errors before tests:
11 missing existing project restore assets and three CS8602 assumptions in
rectangular test fixtures (`AxisCandidateWorkspaceTests.cs:70,101` and
`ManualAxisDraftTests.cs:122`). The fixtures now assert non-null rectangular
geometry before their original value assertions. Offline restore succeeded
from the existing package cache, without new dependencies or downloads. The
first failed build took 87.230767 s; no tests ran on that attempt. The corrected
full-solution build passed with zero warnings/errors in 70.4962009 s. Focused
App and integration checks passed **140/140**, zero skips/failures, in
11.9111294 s. The single full-solution test passed **3,130**, with **32 expected
optional-input/worker skips** and zero failures across 13 projects, in
144.3469389 s. This includes App 1,269/1,269 and integration 182 passed / seven
skipped. No private/model/real-graph opt-in variables were enabled. The unchanged
malformed-legacy-lattice integration regression now passes.

Commands use the existing package cache, normal host and one compiler slot:

```powershell
dotnet restore GraphAutoReader.slnx --source E:/Graph-auto-reader/tools/nuget-packages --ignore-failed-sources --disable-parallel -p:NuGetAudit=false
dotnet build GraphAutoReader.slnx -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test GraphAutoReader.slnx -c Debug --no-build --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --logger 'trx;LogFilePrefix=full-solution' --results-directory artifacts/native-affine-proposal-check/full
```

Focused tests use `dotnet vstest` against the built App and integration DLLs.
The filter covers `NativeAffineAxisProposalTests`, `AffineAxisSuggestionTests`,
the prior rotated/manual/printed-X/crop/calibration suites, candidate review,
unchanged marker assistance and the two integration regressions. Focused TRX
SHA-256: `cba225fbba17fe1a40cbb3a05d66c1801acae173fa1f444d7c3934a6c37611be`.
Full per-project summary SHA-256:
`e9db2ea3b1a32779c978a70496ac2433ae17918407ef249f09a0964d9b523f81`.
The local all-attempt command/outcome record SHA-256 is
`40f676c94f7881f7696acb747baca298555c69f493098e0265f850a496243ad1`.

The frozen 20-library runtime manifest SHA-256 is
`8d92fce739435ba03671d5dcf925685551005bd08c2fb142dbad2f0ce728edf2`.
App DLL: `35f947a2daa5add97d7a6a0b6e02d5298c8213a19063fd0706526a2d021836d9`;
Axis DLL: `7713bc1903fee9ab379420075e6052eebf7e96d8b918ba704aad65ecaa8bdbd0`;
App EXE: `6cf05cdbe23a5809051674222be0513377e63a05ec3e9282c4f0db5a1a19834d`.
Reviewed OpenCV `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`
was restored and verified after compilation in four existing native outputs.
This is an unpackaged Debug runtime, distinct from delivered 0.4.55.

## Native review check

The frozen source executable was launched through Computer Use. One generated
320 by 240, three-pixel-stroke, positive-tilt raster was imported, then Suggest
axes was invoked once. No geometry was supplied. The actual tilted three-point
overlay and localized affine notice appeared; all proposed numeric fields were
empty, review was visibly unchecked, Apply was disabled, and both automatic
label reading and the combined suggestion command were disabled. Dismiss
removed the proposal and overlay, reenabled Suggest calibration, and left Undo
and Redo disabled. The app then closed normally. The fixture has no printed
numeric values, so this check entered none and did not Apply, save or export.
Mode switching, numeric application and Undo/Redo are covered by owned tests,
not claimed as native actions in this check.

Fixture SHA-256: `c222c03c2772b669dc61c9cec36368a89f012db33ee1bb9491cfe8c38ccc9941`
(730 bytes); local outcome SHA-256:
`af2a4b8a58dcdaf13e10ea2ec386d4d315776fca4701d95222f628fb625df661`.
No new autosave/data files were created. The existing autosave and two layout
files retained their hashes. Startup refreshed the existing 304-byte automatic
OCR device-profile setting; its preference remains Auto, with RX 6600 XT
DirectML selected. No user device choice was made or replaced. The UI control
tool needed stale-dialog/offscreen/active-request recovery, but geometry ran
once and returned successfully. Exact UI inference duration was not measured.

## Fixed development diagnostic

The new opt-in route passed its owned shape, identity and accounting checks
before private execution. Probe compilation used the same serial Debug flags
with `-p:BuildProjectReferences=false`: zero warnings/errors, 9.255 s wall.
`--real-dev-native-affine-geometry-trace-self-test` exited zero in 0.405 s,
empty stderr; JSON SHA-256:
`638dea86e9f83f209b5737c2297851907533fcc8668f2a86dc318f67e212f04c`.
Probe DLL SHA-256:
`308aaf9084efa9cb26fe96185a919bc71da6ef4e228b503cd84dd2f807239ba9`.
Historical trace identities and profiles stay unchanged. The diagnostic Rust
worker remains
`2cc6f263a00544243291f2605031c4df7e147175bd345480bd0a44f92fde0b7a`, distinct
from the packaged worker.

Exactly one geometry-only run used
`--real-dev-native-affine-geometry-trace <corpus> <frozen-App.exe> <reviewed-assets>
<unchanged-Rust.exe> --explicit-opt-in`. All paths were absolute and all runtime
pins were verified before the selector read data. The run started at
2026-11-15 22:01:17.4351643 UTC and ended at 22:01:39.3049106 UTC, **21.8510015 s**
wall, exit zero and zero stderr bytes. Project/overall deadlines remain 45/900
seconds, with unchanged worker CPU limits. Full accounting:

| Result | Count |
| --- | ---: |
| Projects attempted/completed | 12/12 |
| Prepared panels / full-image fallback | 11/1 |
| Panels attempted/finished | 12/12 |
| Rectangular geometry | 8 |
| Affine geometry for review | 1 |
| No geometry, native pair-budget refusal | 3 |
| Unattempted / unfinished / timeout / unsupported / panel error | 0 each |
| OCR / tick scanning / truth scoring / image writes / sealed reads | 0 each |

Native fallback was attempted five times and returned two geometries. The three
refusals retain `native_pair_budget_exceeded`; they were not retried. Overall
reviewable geometry increased from eight to nine of twelve. This is availability,
not correctness scoring. All eight existing rectangle left/bottom/right/top
tuples and their core Rust/native stage fields match the unchanged cached
comparison. Seven complete geometry-plus-tick records match; the eighth prior
record had 20 X and seven Y measured ticks from its full trace, while this
geometry-only run intentionally has no tick scan. No unqualified full-record
or end-to-end latency parity is claimed.

Assignment SHA-256:
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`;
selected inventory:
`03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`;
source digest:
`4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1`.
All match the cached comparison. The private 14,813-byte report SHA-256 is
`b77aaafc2c16e39e526373888c77a0e55b228a97821551d200e83471581d0993`;
run receipt SHA-256:
`a6ed1744b0089c88885b8df11590c0f8d1dde38ace6829de6fe5f2db3c72ddbd`.
Per-panel geometry/metadata remains local and is not committed. No independent
evaluation was used, no original was changed, and no values were applied/exported.

New code is Apache-2.0 project code; no dependency, model or license changed.
The current delivered artifact remains verified 0.4.55. Source validation and
bounded native/development checks pass; superseding hosted CI is pending.

After this record is pushed and CI consumers finish, retire the completed
`affine-manual-calibration-check`, `native-affine-proposal-check`,
`native-affine-trace-check` and `native-affine-ui-check` directories under this
worktree's artifacts root: 65 files, 8,976,424 bytes at inventory. These contain
owned inputs, temporary logs, TRXs, hashes and command receipts, all regenerable
from source and commands above. The current 14,962-byte geometry trace and
80,485-byte interval-cluster report remain active comparison inputs for the
next affine OCR/coordinate-bridge stage; retire them after that comparison and
its outcome integration finish. Runtime dependencies, original data, existing
autosave/settings and the sole verified package are outside cleanup scope.
