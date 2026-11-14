<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Manual axis anchors for OCR review

## Behavior and scope

When automatic axis geometry is unavailable, the user can mark three anchor
positions on the selected original panel: first session at zero, Y maximum,
then final session at zero. This creates a source-bound, review-only draft with
user-provided pixel geometry. It contains no numeric calibration values and no
measured tick positions. The original image, project, points, and calibration
remain unchanged while the draft is prepared.

The user can then read OCR labels, inspect the anchors and labels, and refit a
review proposal. The existing reviewed-value checkbox and explicit Apply action
remain required before calibration. Undo remains available after application.
Invalid clicks, dismissal, tab/source/calibration changes, and cancellation
discard the pending draft. The ordinary manual three-anchor calibration path
still works unchanged. This adds no Rust worker, model, dependency, threshold,
or production approval.

Six focused tests in `tests/GraphReader.App.Tests/ManualAxisDraftTests.cs` cover
review-only creation through Apply and Undo, malformed original-pixel anchors,
tab-change/dismiss invalidation, missing reader and stale calibration, cancel
during a busy operation, and the ordinary manual path.

## Validation status

Two earlier validation attempts did not complete the test suite:

- Test compilation first failed on analyzer CA1859 after `96.2742908 s`; the
  concrete variable-type issue was fixed.
- Sandboxed VSTest could not connect to its test host within 90 seconds. The
  process ended after `106.7073389 s`; its TRX reports 0 tests executed.

The corrected full host App suite then passed **1,014 tests, 0 failed, 0
skipped**, with 46 s reported test duration and 91.8490245 s process wall time.
App/production compilation completed with 0 warnings/errors. The TRX contains
the six focused manual-draft tests: review-only creation through explicit Apply
and Undo, malformed anchors, tab-change/dismiss invalidation, reader/stale
calibration handling, cancellation during a busy operation, and the ordinary
manual path. TRX SHA-256 is
`cd0e1b183834e66a60aca7b452089e022b0883e9c3c76c1304ae0b6ca1856adc`; process
receipt SHA-256 is
`3d857b6ba0569a3cb72870af8c88c9940c644a3568829eb1f9bfd55a14860ca0`.

The App command was `dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false`,
with `OutDir` set to the existing `artifacts/hardware-final-build/`, the TRX
logger, and results under `artifacts/manual-axis-draft-check/`. The probe-only
build used the same configuration with `BuildProjectReferences=false` and
passed with zero warnings/errors in 9.579 seconds.

Native UI appearance and interaction were not visually checked because
Computer Use was unavailable. The source actual-worker prepared-crop check is
recorded below; it does not establish packaged or production behavior. The App
tests do not establish runtime OCR behavior.

## Owned actual-worker source check

The source workflow then ran on an owned `NoTickMarks` fixture
(`e70e983cf790759fbe59163af77af69c8ffc010689e66b99d742fc6f5203aebc`, 3,990
bytes) with user-marked geometry and a prepared crop. The original-pixel crop
offset was verified. The check made no Rust axis-worker call and returned six
estimated label pairs, zero measured pairs. OCR time was `1961.1702 ms`,
including `809.704 ms` initialization and `981.0746 ms` recognition; crop
creation took `33.9718 ms`.

After simulated explicit review, the saved and reopened project recorded a
user-confirmed manual three-anchor calibration with 3 anchors and 0 tick
evidence. The workflow exported and reopened 3 points; CSV SHA-256 is
`249b49a16b036e5be69191a4aea4a867e75d4cdbe0ac6fd31fa2568a23b3edee`. It also
returned 2 marker proposals, both explicitly accepted. The source check exited
0 with empty stderr in `4.8436593 s` process time (`4189.3508 ms` pipeline).
Report SHA-256 is
`d4794cf462afdf5c01a872d56907aec68c9f55c460829840148ebd4767e41318`.

The source App DLL was
`34e1e4a89a8b71386c9acecc86c691e41c0ddc5c6c51cd614db70bda8e393eae`; the probe
was `568bdb140d1163e614364009405d9929ce17faf293401d3cdf6d5bfb91bc4462`.
Nine third-party runtime libraries were checked at 14 destinations. These are
source-check identities, not a packaged-build claim. Temporary outputs remain
through package verification and should be retired after it finishes, this
outcome is pushed, and active consumers finish.

## Packaged 0.4.48 workflow check

The same owned prepared-crop control was exercised against the internal
`0.4.48` package (ZIP SHA-256
`da416932e73d3408b0346400eb1a4098d3c0282f7c4473ca7ee7444447ac11db`). Geometry
was supplied by the user; the Rust axis worker was not invoked. OCR produced 6
estimated label pairs and 0 measured pairs. After simulated explicit review,
the reopened project recorded a user-confirmed manual three-anchor calibration
with 0 tick IDs. Three points were exported and reopened. The check exited 0
with empty stderr in `5.1540688 s` process time (`4410.2178 ms` pipeline).
Workflow report SHA-256 is
`164d86e51a394463dd38a3321548fbde284c5eadc8147a6caf9595dbfd1c6e76`; CSV
SHA-256 remains
`249b49a16b036e5be69191a4aea4a867e75d4cdbe0ac6fd31fa2568a23b3edee`.

This verifies an owned packaged workflow, not independent accuracy, native UI
interaction, or production approval. Visual acceptance remains unchecked.

## Limits

The feature supplies user-positioned geometry to the review path; draft creation
does not invent numeric values or guarantee that OCR can complete calibration.
No private-data accuracy or click-savings claim is made. Version `0.4.48` is
prepared for the integrated internal preview; `0.4.47` remains the retained
package until its replacement is verified and recorded. No public release or
production approval is implied.
