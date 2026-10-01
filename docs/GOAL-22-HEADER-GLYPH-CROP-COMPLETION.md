<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Complete truncated header glyph crops from original pixels

The [frozen diagnosis](GOAL-22-HEADER-GLYPH-CROP-DIAGNOSIS.json) found full ink
components beneath five truncated single-character OCR crops. Those fragments
blocked the existing missing-glyph recovery. The new path measures one complete
component, reads its actual original pixels in a separate batch, and replaces
the fragment only after successful recognition. It never supplies a character.

Human decisions, numeric context and other semantic roles are protected. A
numeric misread cannot enter calibration as a tick. Failed or empty rereads
retain the original fragment; successful replacements carry review warnings.
The existing three-heading band and size limits are unchanged. OCR and workflow
cache identities include the new composition version.

## Fixed workflow evidence

Both configurations use unchanged models, operating points and owned synthetic
inputs. Each runs 31 native panels and 39 CSV panels. The server configuration
uses a freshly measured baseline on the preceding stroke-gap runtime.

| Measurement | Retained V44 / English | Closed V46 / server context |
| --- | --- | --- |
| Native literal-exact text / 453 | 353 -> 353 | 416 -> 419 |
| Correct readings gained / lost | 0 / 0 | 3 / 0 |
| Geometrically matched text / 453 | 397 -> 397 | 435 -> 438 |
| Extra text regions | 104 -> 104 | 116 -> 113 |
| Character errors / 3100 | 625 -> 625 | 247 -> 241 |
| Correct roles / 453 | 365 -> 365 | 403 -> 406 |
| Matched markers / 838 | 742 -> 742 | 746 -> 746 |
| Extra markers | 26 -> 26 | 20 -> 20 |
| Completed CSV sources / 23 | 15 -> 15 | 16 -> 16 |
| Correct CSV values / 706 | 414 -> 414 | 429 -> 429 |
| Correct CSV rows / 724 | 409 -> 409 | 416 -> 416 |

There are 19/5 native/CSV replacements for retained V44 and 28/8 for V46/server.
Most improve crop bounds without changing text. One V44 CSV reading changes
from `Δ` to `A`; one V46 native reading changes from `3` to `B`. Native paired
scoring confirms three new correct condition labels and no lost correct reading.

All accepted marker properties, initial/post-NMS centers, classifications, axis
geometry, calibration objects and text-exclusion membership are unchanged.
The audit reconstructs marker IDs from geometry because 93 panels rebind those
IDs. Pre-refinement decoded proposals differ in 12 panel/configuration pairs;
every changed point lies outside the plot bounds. One V46 panel adds two such
points, increasing both above-threshold and outside-plot counters by two.
No changed proposal survives into retained centers. The bound audit reports
these differences rather than asserting all intermediate diagnostics identical.

All 866 exported rows have finite original-pixel coordinates and valid
calibration. Exported numeric rows and source statuses are unchanged. Wrong-scale,
duplicate and failed-source residual rows remain zero.

## Verification and timing

`Run-HeaderGlyphChecksV3.ps1` builds and tests the OCR and App projects, then
builds the native runner with the reviewed OpenCV library. 617 OCR and 542 App
tests pass; 16 optional OCR tests are skipped. Three builds have zero warnings
and errors. Checks take 122.603 seconds. The current server baseline takes
424.107 seconds; four comparisons take 789.724 seconds.

The first check attempt failed on a return-type syntax error. The second had
four fixture failures because its narrow fragment triggered existing vertical
orientation detection. The corrected square fixture passes without changing the
runtime rule. Both attempts and two audit assumption failures are preserved.
The [outcome](GOAL-22-HEADER-GLYPH-CROP-COMPLETION.json) binds source snapshots,
test results, runtimes, models, licenses, observations and complete local audits.

## Remaining work

Two-heading layouts, vertically offset labels and words merged across semantic
roles remain outside this repair. Retained V44/English/V27/V5 is unchanged.
V46 remains closed development evidence. Accuracy, real acceptance and release
gates still fail or remain incomplete. No dependencies, private/sealed reads,
training, production activation or packaged build were added. New source retains
Apache-2.0 headers. Build 433 remains version 0.4.33.
