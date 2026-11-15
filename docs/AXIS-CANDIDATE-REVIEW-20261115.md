<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Explicit review of completed axis candidates, 2026-11-15

## Problem and scope

Source starts at `6e3d384d0514f4f153bdcfff8bd7954595532228`.
The fixed 12-panel development geometry diagnostic in
[the forward-extent outcome](AXIS-FORWARD-EXTENT-20261115.md) has seven usable
geometries, three native pair-budget failures and two complete but ambiguous
native searches. Normal Suggest correctly rejects ambiguity, but discarded
completed candidate lists force the user to draw anchors again.

This change adds a separate **Review axis choices** action in ManualPreview.
It is explicit geometry assistance, not an automatically selected calibration.
No real/private input, independent evaluation, sealed split, truth row or
optimizer is read in this batch. Sealed-run budget and usage: zero. The existing
fixed development split and unchanged451/Mirage comparison stay unchanged.

## Implementation and safety properties

- Run the native detector once and return its complete ranked candidate list
  only after successful bounded enumeration. Keep the existing B128 detector
  limits, one worker thread, CPU job budget and cancellation/deadline handling.
  The separate framed response is capped at 64 KiB and 128 candidates. Reject
  the entire result on exhaustion, truncation, invalid bounds, unsupported
  geometry or contradictory fields. Never return a partial list.
- Rust-first normal Suggest and its 4 KiB single-result protocol are unchanged.
  The explicit review action uses the native fallback's complete enumeration;
  Rust ambiguity does not supply a complete list and is not exposed as one.
- Bind the defensive candidate snapshot to the exact tab, original image and
  calibration state. Selection previews three anchors without changing project
  state. There is no default or implicit selection. **Use selected axes** creates
  the existing review draft with empty numeric fields, unknown confidence and
  a visible geometry warning. It does not apply calibration or start OCR.
- Reuse literal label reading, numeric association/refit and explicit Apply
  review. Dismiss, Escape, tab changes, disposal, Undo/Redo and late cancellation
  clear or reject stale candidates. Starting search clears partial ordinary
  three-click calibration so a later canvas click cannot finish old anchors.
- Localize action/status/row names, refresh them on language change, use theme
  brushes and accessible names, and reveal the completed list or review warning
  in the inspector without selecting a row or taking focus.

The structural bar is complete-or-empty candidate output and zero calibration
mutation before explicit reviewed Apply. No model or accuracy threshold changed.

## Validation and full failure accounting

All local runs used Windows x64, .NET 10 Debug, serial compilation, no restore,
no shared compiler, and the normal host VSTest pipe. Test-only compiler outputs
retain version 0.4.52; these are not packaged builds. Command forms:

```powershell
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj --configuration Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj --configuration Debug --no-build --no-restore --filter '<focused filter>' --logger 'trx;LogFileName=<result>.trx' --results-directory artifacts/axis-candidate-review-check
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj --configuration Debug --no-build --no-restore --logger 'trx;LogFileName=full-app.trx' --results-directory artifacts/axis-candidate-review-check
```

The broad filter covers AxisCandidate, AxisSuggestionTests,
NativeAxisWorkerDetectionFailureTests, ShellContractTests,
CalibrationAccessibleTextRefreshes, StartupArgumentsTests and
ResourcesAndLocalizationTests. The final reveal-only filter covers
AxisCandidateReviewTests, ShellContractTests and
CalibrationAccessibleTextRefreshes.

| Run | Result | Time |
| --- | --- | --- |
| Initial compile | Void: CA1861 for an inline constant reason array; changed to static read-only storage | 49.822 s wall |
| Initial focused tests | 63 passed, 3 failed, 0 skipped | 3 s test; 43.013 s build/run wall |
| Assertion API correction compile | Void: CS0117, unavailable `Assert.ThrowsException`; use existing MSTest 4.3.3 `Assert.Throws` | 9.18 s build |
| Corrected focused tests | 66 passed, 0 failed, 0 skipped | 3 s test; 5.763 s wall |
| Full App before theme/reveal refinement | 1,147 passed, 0 failed, 0 skipped | 46 s test; 48.585 s wall |
| Theme refinement focused tests | 67 passed, 0 failed, 0 skipped | 3 s test; 6.715 s wall |
| Final reveal refinement targeted tests | 34 passed, 0 failed, 0 skipped | 2 s test; 5.036 s wall |

The three initial failures were test setup/assertion defects: point-history setup
had not selected its series, cancellation expected TaskCanceledException instead
of the token's OperationCanceledException, and a completed fake service task was
pumped outside STA. They were corrected without weakening the runtime guards.
Successful subsequent compiles had zero warnings/errors (11.348, 33.323 and
30.088 seconds wall respectively).

The first focused native test used NuGet OpenCvSharpExtern after compilation
overwrote a staged reviewed DLL. All later native tests explicitly staged the
reviewed runtime **after** compilation and used `--no-build`:
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`.
The first run is not claimed as verification of that reviewed runtime.

TRX SHA-256, retained only until this outcome is pushed and active consumers end:

| File | SHA-256 |
| --- | --- |
| focused-initial.trx | f91162d90fbc63ab258124a1d955128804e4ca56c98582160c4652bb603f7a9b |
| focused.trx | 1abd41e1bc58c8859bc32cb5a549b2de37eb8157359e21e24f5680fca81ff0c0 |
| full-app.trx | aa70ddecd0ce03097d0bfd80ef0ff61cab7a83c6dba3dd97b4f4fdd45aa798f2 |
| focused-theme.trx | f6eef869b05a9238fb4143be9045568b3a80a97958bedab20fd35e532b8b7c28 |
| focused-reveal.trx | 4d0ae2e054132ea9a233133488e86065d2be01d3a82542c09534d5d7b9a9d583 |

Tests cover actual owned rasters, unchanged ambiguous Suggest rejection, the
128/129 contact boundary, budget/cancel empty results, strict frame/JSON limits,
all-128 response roundtrip, no Production input read, stale source binding,
workspace immutability, native WPF selection/accessibility, language refresh,
Light/Dark brushes and cancellation/edit transitions. Final read-only integration
review found no material issue. Exact-head full Windows CI is still required
before describing the new integrated build as green.

## Actual native check on an owned graph

Generated once using
`tools/GraphReader.CalibrationOcrProbe/New-TickCalibrationFixture.ps1 -GridLines`
with `-OutputPath artifacts/axis-candidate-review-check/owned-grid.png`.
The owned 640 x 480 PNG is 6,122 bytes, SHA-256
`1d258bf5c7be0b7788286f01e64ac8d1ca1eaac8e0b574b927ca1e52011c6894`.
No external graph, font binary or private input was copied. The image contains
printed X 1/2/3 and Y 10/5/0 with grid lines.

The native source app returned six choices with none selected and Use disabled.
Explicit mouse selection and Down/Up changed only the overlay; Undo/Redo stayed
disabled. Use created blank numeric draft fields and an unchecked review box.
Apply stayed disabled. One literal OCR request returned six text regions on CPU;
one Refit formed the expected first/final X 1/3 and Y maximum 10 draft, with an
estimated-anchor warning and source-pixel positions. Apply remained disabled
until review. Nothing was applied, saved, exported or edited as a point.

Native inspection found two display defects and verified their repairs: dark
theme initially had a white list background, and results initially remained below
the inspector fold. The final source app automatically revealed the readable
list and, after explicit Use, the review warning. All three draft fields remained
blank and Apply/Undo/Redo disabled. The app was closed normally; no app window
remained. Source-local QA Data is disposable; shared portable user Data was not
used or changed.

Final source app DLL SHA-256:
`c81c8f6920042aa251e25653c252750c0b57617e7406c06866000ef48e484064`.
The earlier OCR/refit native check used the same backend before the reveal-only
UI change, DLL `20e13909cd8bda6663cf41fbef65f901ca82dd5b1f05485e82dbb398b719ad15`.
These checks establish review interaction on an owned control, not automatic
geometry accuracy, real-graph improvement or a latency benchmark.

## Packaging, licensing and retention

This integrated batch warrants one replacement preview because it combines the
delivered printed-X/localization repairs, bounded geometry improvements and the
new explicit candidate-to-calibration review workflow. The existing hosted
internal-preview workflow is used; no additional self-hosted runner is needed.
Its reviewed OpenCV donor now points to the current ledger-pinned 0.4.52 artifact
14142820090 from run 43666480287. The importer still checks the recorded inner
ZIP hash. That artifact was verified live as unexpired before preparation.

Project source stays Apache-2.0. No dependency, weights, frozen schema or model
approval changed. Existing reviewed OCR/OpenCV/Rust assets retain their licenses
and review-only status. This is an internal preview, not a production release;
automatic acceptance and installer readiness remain incomplete.

At `2026-11-15T11:12:39.3259856Z`, after the preceding outcome was pushed, six
completed diagnostic files totaling 333,511 bytes were removed from the exact
axis-merge-sort-check and axis-forward-extent-check scratch directories.
Originals, the independent split, active models and the sole 0.4.52 package were
preserved. This batch's TRXs, generated control and source-local QA Data/links
will be retired after their evidence is pushed and package verification ends.
Keep 0.4.52 until its single replacement is uploaded, verified and recorded.
