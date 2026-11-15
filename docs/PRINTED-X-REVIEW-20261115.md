<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Reviewed printed X calibration

Source base: `6c450ab8b3ca9b1965444c107f146121d31f73c8`.
This is an unpackaged source batch. The sole retained package remains 0.4.52.
The maintainer revoked the former 4% cutoff: checkpoint at 0% weekly remaining
for the dot-coordinated reset, without independently consuming a reset or paid
credits.

## Problem and scope

The preceding fixed twelve-project development trace returned seven OCR panels,
63 tick pairs and no complete session drafts. Two panels reported invalid X
range despite a usable Y draft. A positive-integer first session is appropriate
for the session workflow, but cannot represent every actual printed X range.
The owned zero-origin image reproduced that boundary with six correctly read
labels and measured ticks. It previously required manual entry or an explicit
entered-session fit.

The change adds a separate, explicit reviewed-coordinate choice. It does not
change the session acceptance rule, model, OCR confidence/association thresholds,
or independent split. No private truth is used to select a parameter or candidate.

## Behavior and compatibility

- Standard review may expose `PrintedXReviewDraft` only after all included
  literal pairs, unique associations, monotonic spacing, all-inlier fitting,
  observed Y zero and positive Y maximum pass the existing checks. Zero,
  negative and fractional first X values remain literal values. Strict automatic
  and explicitly entered-session proposal paths never return this new draft.
- The existing `IsComplete`, session fields and legacy failure reasons remain
  unchanged. The UI offers **Use printed X values**, then requires a fresh anchor
  and label review before Apply. Nothing is applied by OCR or by choosing the mode.
- Numerical edits clear proof and review. Evidence edits/refit reset the mode
  and computed values. Tab/source changes and dismissal invalidate the draft.
  The existing entered-first-session alternative remains available after refit.
- The current domain contract already has `Manual` anchors with graph/pixel
  coordinates and an optional session lattice. Three explicit manual anchors
  with a null lattice persist this mode without a new field, enum or schema.
  They are user-confirmed, have no OCR evidence IDs, and do not manufacture
  session 1. Reopening rejects malformed or ambiguous anchor shapes.
- Save/reopen, manual form edits, tab changes and Undo/Redo retain the mode.
  Old readers that only recognize session-anchor kinds cannot reconstruct this
  calibration as a valid session calibration. No schema migration is needed.
- Both manual and production-manual export choose the existing printed-session
  coordinate export path. Fitted point X values carry `Estimated` provenance;
  observation indices remain separate positive ordinals. No absolute session
  origin or first-observed-session assertion is added.
- The existing production automatic pipeline cannot yet consume a manual
  null-lattice calibration. It returns the localized, structured
  `WORKFLOW_PRINTED_X_DETECTION_UNSUPPORTED` failure before either production
  execution branch mutates the project. Retained production export evidence
  cannot silently override this new mode.

## Component and workflow verification

The combined Debug build and relevant App test batch completed in 126.9710676 s:
**154 passed, one failed, zero skipped, 155 total**. No compiler warning/error
was emitted. The failure was in the new test: it concatenated CSV and JSON audit
artifacts and expected two rows, although each artifact contains the same two
rows. The assertion now verifies both artifacts independently. Its targeted
rebuild/recheck passed **1/1**, zero skips, in 17.0862125 s. The unchanged 154
passing checks were not rerun.

The new cases include zero/negative/fractional endpoints, strict/entered-session
separation, malformed/outlier/duplicate/missing-zero rejection, estimated-position
flags, explicit UI choice/review, proof invalidation, and Undo/Redo. The workspace
check executes save/reopen and both manual and production-manual exports of X=0
and X=2.5 with separate observation indices 1 and 2 and estimated provenance in
each CSV/JSON audit artifact. It also checks the production detection guard.
These component controls deliberately supply geometry and literal OCR rows.

```text
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter "FullyQualifiedName~CalibrationTickProposalTests|FullyQualifiedName~CalibrationTickViewModelTests|FullyQualifiedName~CalibrationLabelViewModelTests|FullyQualifiedName~AxisSuggestionTests|FullyQualifiedName~PrintedXWorkspaceTests|FullyQualifiedName~Localization"
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj --no-restore -m:1 -nr:false -p:BuildProjectReferences=false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter "FullyQualifiedName~PrintedXWorkspaceTests"
New-TickCalibrationFixture.ps1 -OutputPath <owned-zero-x.png> -ZeroOriginXLabels
```

## Native Windows check

Computer Use opened the current Debug source executable with isolated portable
Data. Five hardlinks reused the retained package's OCR assets; no model copy was
created. The reviewed OpenCV library was restored after compilation. The first
sandbox hardlink attempt was denied; the authorized normal-host retry succeeded.

The owned 640x480 image was imported normally. **Suggest calibration** actually
detected the axes, measured ticks and read all six literal labels on CPU:
X 0/5/10 at pixels 140/340/540 and Y 10/5/0 at pixels 80/230/380. No geometry was
supplied to that inference. The draft offered the printed range while leaving
Apply disabled. Choosing it filled 0/10/Y10, changed the visible field labels,
and retained an unchecked review box. Confirmation enabled Apply. Apply reported
the reviewed 0-through-10 range and estimated point-X status, with zero review
issues. Undo restored the uncalibrated form and one issue; Redo became available.
The owned test window closed normally. GUI save/export was not performed;
the component checks above cover those operations.

| Evidence | SHA-256 |
| --- | --- |
| App executable | `648912cd16835729e8b0ae440441041170547b5bf0ec8ce032616a80550c99ff` |
| App DLL | `f09b4e7e2121e8362157dfd516c8e46d2be7bcf5cf9a7eda438b643b5201fd87` |
| Owned PNG, 4092 bytes | `966cbe6d3b3eb7f09f6e8a35ddcc7daac7086886284608a637ae61567dbafdc8` |
| First TRX | `f46622c05afa298df072bac429fb775d697e967db2a13dcdfc754b94786a2c68` |
| Corrected workspace TRX | `dd8011fdaa183528f3f67a88e822691c460b76794c3a20520ef8ec3b07124a6e` |

## Prior association diagnosis and retention

The completed directional-association metadata consumer inspected geometry only.
Across three unpaired panels, all nine unassociated numeric-marked regions had
unique nearest ticks but lay beyond the unchanged font/current directional bound;
eight were at most four pixels high. Three duplicate-associated tiny regions had
different vertical positions, so the evidence did not justify merging them. One
invalid-numeric region was a 675x15 crop spanning 34 measured ticks; its cause
remains unproven. No confidence, geometry or association threshold changed.

The old `directional-tick-association-check` folder contains 19 generated files,
303,282 bytes. Its consumers are complete; retire it after this closeout is pushed.
Retire this batch's generated reports, owned image, isolated GUI Data and temporary
OCR hardlinks after recording/pushing the final development result and finishing
their consumers. Preserve originals, independent data, the current headless
runtime dependency, shared user Data and the sole packaged build.

## Development confirmation and acceptance

The probe-only build initially stopped on analyzer CA1859 in
`AddPrintedXAggregate` after 11.5799007 s, before any data access. Returning its
concrete dictionary type fixed that build issue. Review also corrected the pure
self-test to inspect the outcome's payload. The final probe build passed with
zero warnings/errors in 12.8345191 s. Its owned self-test passed in 0.6955462 s
with zero private reads. No unchanged inference was repeated to fix plumbing.

One fixed development confirmation completed all twelve projects and twelve
panels in **47.9499202 s**, exit 0, empty stderr: eleven prepared panels and one
genuine full-image fallback. Seven returned OCR, five failed geometry; there
were no OCR failures, timeouts, unsupported dimensions, unfinished or unattempted
panels. The same 51 reserved projects remain excluded. Source, assignment and
inventory digests match the preceding directional-association trace.

**Two of twelve panels now expose a reviewed printed-X draft**, both using
measured ticks without estimated positions. Legacy complete session drafts stay
at zero, with zero partial X and two partial Y drafts. There are still 117
regions, 72 numeric rows and 63 paired ticks. All seven OCR panels have byte-for-
byte equivalent serialized geometry and literal-row metadata to the unchanged
prior result. This is a representation/review improvement, not recognition
accuracy or an applied calibration. No data values were exported by this check.

The unchanged overlapping legacy reasons are: insufficient pairs 4; unpaired
labels 3; inconsistent spacing, invalid session range, nonmonotonic labels and
missing observed zero 2 each; duplicate association and invalid numeric label
1 each. Five native attempts still return no accepted geometry: two envelope
budget limits, two exact-contact budget limits and one multiple-candidate result.
One other panel retains the existing estimated-position warning.

Timing sums: preparation 2311.0606 ms; geometry 15,459.0252 ms; OCR 28,320.8103 ms;
pairing 72.1292 ms. Median OCR call was 3258.1554 ms and the longest 8346.0108 ms.
This run used Debug source assemblies; the preceding recorded run used Release
source assemblies. The two wall times are not a controlled speed comparison.
No speed improvement is claimed. Model, worker resource limits and thresholds
remain unchanged.

| Confirmation evidence | SHA-256 |
| --- | --- |
| Probe DLL | `e160fc684633bdcc5f0e36ec28c7a3ff85df3a82ac3412ab9a37194979ee6c56` |
| Pure self-test | `f71fb34bde167ef9ceeaaccafbdab025b67a605f7ff14580f661da003e1e55b3` |
| Development report, 55,377 bytes | `bab4a8076ae6c3d2e4654a5c54a36a77e1ef469053f8817d11c65f0456cfd834` |
| Source digest | `4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1` |
| Split assignment | `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a` |
| Selected inventory | `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d` |

```text
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:BuildProjectReferences=false -p:NuGetAudit=false -p:OutDir=<existing-headless-runtime>
GraphReader.CalibrationOcrProbe.exe --real-dev-printed-x-trace-self-test
GraphReader.CalibrationOcrProbe.exe --real-dev-printed-x-trace <authorized-corpus> <current-source-App.exe> <retained-452-ocr-assets> <current-source-Rust.exe> --explicit-opt-in
```

The generated batch folder contains eleven files / 293,969 bytes. The isolated
Debug GUI Data contains three files / 5696 bytes. Both consumers are finished;
these outputs, the five temporary OCR hardlinks and their temporary portable
marker are eligible for exact-path cleanup after this outcome reaches main.
The earlier 19-file association folder is likewise finished. The current
headless runtime and compiler dependencies remain active.

**PASS for reviewed printed-X calibration, component/native checks, and the
fixed development confirmation.** End-to-end production acceptance remains
incomplete, especially the five geometry failures and the ten panels without
this draft. The next work remains geometry/label preparation diagnosis on owned
controls and permitted development examples, without weakening fit checks.
No sealed data, private truth scoring, production promotion, package, dependency
or new license is involved. Existing Apache-2.0 source/model notices and original
inputs remain unchanged.
