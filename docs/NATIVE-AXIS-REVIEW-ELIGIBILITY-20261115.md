<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Keep supported review choices after a completed native search

Source baseline: `9e694ca5c0418b7ffcb9c90fe0de741d3c38d67c`.

## Defect and isolated change

The [Rust review diagnostic](RUST-AXIS-CANDIDATE-REVIEW-20261115.md) returned
complete review choices on 4/12 fixed development panels. Seven declared Rust
NO_AXIS results reached native fallback, all of which returned ambiguity.
That aggregate does not identify the cause of each native failure.

Source review found an independent, reproducible defect: after successful
bounded enumeration, one unsupported tilted candidate discarded every supported
axis-aligned candidate in the completed list. Completeness is established before
this representation check. Requiring every enumerated option to be representable
does not protect against incomplete enumeration.

An owned 800 by 400 recorded-line control supplies one clean L and one tilted L
to the geometry detector; it does not claim image-to-line detection. Before the
repair, both input orders confirmed two completed snapshots and one eligible
clean box, then failed because review returned OPENCV_AXIS_AMBIGUOUS. Neither
control reads private input or numeric truth.

The adapter now omits only candidates rejected by the existing
`native_rotated_geometry` rule, after the complete search succeeds. Every
supported option remains in its original ranked order with contiguous indices.
All-rotated input still returns no choices. Nonfinite, out-of-bounds or invalid
rounded geometry still invalidates the request. Budget failure, cancellation,
incomplete enumeration, the 128 raw-candidate cap, 64 KiB response cap and client
validation remain unchanged. Ordinary Suggest and Rust selection are unchanged.

No threshold, rank, tolerance, model or scientific value changed. This is a
representation-stage repair for explicit human review. Acceptance is structural:
retain every supported choice from a completed bounded search, and never return
a prefix from a failed search. Sealed-run budget/use is zero. The fixed twelve
development inputs and all 51 reserved exclusions remain unchanged.

## Verification checkpoint

The red build passed with zero warnings/errors in 81.532 seconds. Both red
cases failed at the expected review-result assertion, not at their snapshot or
eligibility preconditions: 2 failed, 127 ms test time, 2.901 seconds command.
The repaired build passed with zero warnings/errors in 34.538 seconds. The
focused native suite passed 39/39 in 754 ms, 2.319 seconds command. It includes
mixed one/two supported options in both orders, all-rotated refusal, malformed
geometry, complete two-panel results and existing budget/cancellation guards.

The full App suite then passed 1,156/1,156 with no failures/skips on the same
outputs, without another build: 49 seconds test time, 52.640 seconds command.
Reviewed OpenCV SHA-256 `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`
was verified in both source App and test output. `git diff --check` passed.

| Evidence | SHA-256 |
| --- | --- |
| Red TRX | `e43adb9c58fa04e801be32cdc4917b542b03ddebeec94a1afc31b05c90480758` |
| Focused green TRX | `557460c0abc13b2a67b4493340a9e67b1c4717272cdc8e5a7f3d8c76707c9fcb` |
| Full App TRX | `13863c4200239b1a3c8c83efdefdea1cbe82c0b957c3191b1fd74e5f8c994bbd` |
| Frozen source runtime map | `f54bf5ffafdba624223f368d875f364db91f2dc53d90b2f70ab70e28c7ee1ac0` |

## One fixed development confirmation

The distinct `--real-dev-native-eligibility-geometry-trace` route reuses the
previous review trace implementation and frozen first-twelve selector. Its new
literal runtime map preserves the earlier profile and pins. The probe-only
build used `--no-restore -m:1 -nr:false -p:UseSharedCompilation=false
-p:NuGetAudit=false -p:BuildProjectReferences=false`: zero warnings/errors,
14.944 seconds. All twenty source runtime libraries were hash-checked and staged
to the existing probe output. The owned-only self-test passed in 0.523 seconds,
exit zero and empty stderr, before any private read.

```text
GraphReader.CalibrationOcrProbe.exe --real-dev-native-eligibility-geometry-self-test
GraphReader.CalibrationOcrProbe.exe --real-dev-native-eligibility-geometry-trace <authorized-corpus> <source-App.exe> <reviewed-assets> <unchanged-Rust.exe> --explicit-opt-in
```

One actual run finished at `2026-11-15T13:40:34.9194222Z`, exit zero, empty
stderr, 12.2452 seconds wall. All twelve inputs and panels finished: eleven
prepared panels and one full-image fallback. No input/panel was unattempted,
unfinished, unsupported or failed by exception/timeout.

Availability was unchanged: four Rust one-choice lists, seven Rust NO_AXIS
results followed by native OPENCV_AXIS_AMBIGUOUS, and one Rust AMBIGUOUS_AXIS
without fallback. The same four panels remained available, with zero gained
or lost availability. Native review still returned no list on all seven calls.
Review work totaled 9,338.2229 ms, median 763.1037 ms per panel; preparation
totaled 1,640.5713 ms. Single-run timing differences are not a speed claim.

The owned defect is fixed, but this confirmation does not establish a real-input
coverage improvement or show which other native gate rejected those lists.
Specific rejection-stage accounting is the next diagnostic target; do not repeat
this unchanged run or assume another threshold/representation change will help.

Assignment, inventory and selected-source hashes exactly match the preceding
report. All 51 reserved inputs remained excluded. Candidate selections, OCR,
truth scoring, image writes, Apply, exports and sealed payload reads were zero.

| Confirmation identity | SHA-256 |
| --- | --- |
| App EXE | `c62e0aff97f086955402be9f54ce526b6edc8be7cdce65c58fd62f89bade41fd` |
| App DLL | `3bbeef0c6562335cf988296169a25e1c52d9e12e739b94ba6c52f2c73be891c4` |
| Axis DLL | `25af744f72d9815dc8f930046b589a1bee12a6ecce58f7221a9557bafd752faa` |
| Rust worker | `9c5ac19abc3b08632d82b5590a9e59f2794d94f92f345fbc70741e181808c0d9` |
| Probe DLL | `8e565c21f68a6540a9484c97556e1b01f95ff9c88944021f552707f27c1ea149` |
| Profile source | `a25f2f44eb74c2bed38bb0740421f741eb20c96dde318ad4201d4f76748c9c12` |
| Owned self-test | `5220cfbb5e57a618ed029a3e503e45d794efa278a0528714d0355e11e275551b` |
| Fixed-twelve report, 8,375 bytes | `a6f50804fdf5d82a407d24d45df2ffa174193f78912743cf50bd87e45572875e` |

Full Windows CI [43720654959](https://github.com/SKang393/Graph-auto-reader/actions/runs/43720654959)
passed on exact source `9adee655d526d62f936e208063eaaa2e4296291c`, completing
at `2026-11-15T13:55:10Z`. All 13 .NET suites passed: 2,960 passed, 30 skipped,
zero failed, including all 1,156 App tests. Rust passed 63 tests. Version policy
(62), build-ledger (6), portable retention (12), localization, bounded OCR and
the remaining evidence/packaging checks passed. Public release jobs were skipped.
This includes the mode-localized shell contract and both language-refresh
regressions; the earlier failed `b33d732f` run does not describe this source.
No package or production approval follows from CI alone. All source remains
Apache-2.0 with no added dependency or model.

## Retention

After the preceding source/outcome reached origin/main in `9e694ca5`, routine
cleanup retired 40 completed files totaling 3,778,623 bytes at
`2026-11-15T13:33:54Z`: three WPF harness TRXs, 32 initial Rust review scratch
files and five axis-review TRXs. Exact worktree scopes were
`artifacts/wpf-harness-repair-check`, `artifacts/rust-review-owned-check` and
`artifacts/axis-candidate-review-check`. The last retains only `owned-grid.png`,
SHA-256 `1d258bf5c7be0b7788286f01e64ac8d1ca1eaac8e0b574b927ca1e52011c6894`.
An initial byte-total guard stopped deletion because one TRX size had been
omitted from the sum; the corrected exact inventory passed. CIM inspection was
unavailable, and the process/path fallback found no consumers of those scopes.

The two-panel owned PNG, current development comparison, new eligibility TRXs,
runtime assets, user Data and sole 0.4.52 package remain active. No original,
independent input or unrelated task output was removed. Retire this batch's raw
checks after its outcome is pushed and downstream consumers finish.
