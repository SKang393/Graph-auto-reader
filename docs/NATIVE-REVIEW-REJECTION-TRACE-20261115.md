<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native review rejection accounting

## Problem and scope

The [eligibility confirmation](NATIVE-AXIS-REVIEW-ELIGIBILITY-20261115.md)
returned review choices on 4/12 fixed development panels. All seven native
fallback calls returned the same generic ambiguity code. This diagnostic
identifies their actual failing stages before any further eligibility change.
It adds no detector, threshold, budget, selection or output-behavior change.
Its acceptance criterion is observed/unobserved and complete/incomplete
accounting for every attempted native call, plus unchanged results on owned
observer-on/off controls. Sealed-run budget is zero.

## Implementation and owned verification

`NativeAxisCandidateReview` has optional internal callbacks for encoded-image
and supplied-line paths. They report one terminal immutable observation with
stage, completion/cancellation, ranked/checked/eligible/rotated counts, the
first hard geometry rejection and the detector's existing work counters.
Observer exceptions cannot replace normal results, failures or cancellation;
fatal exceptions remain unhandled. Existing signatures and the worker wire
format are unchanged. Incomplete search never publishes a candidate prefix.

The focused normal-host suite passed **42/42**, zero failed/skipped, 866 ms
test time and 4.600654 seconds wall. It covers encoded-image result parity,
mixed supported/rotated lists and order, all-rotated refusal, both budgets,
cancellation, empty evidence, invalid requests, fractional rounding collapse
and observer exceptions. The exact-pair control records 128 attempts and
128 admitted pairs while ranked snapshots remain zero and enumeration is
incomplete. These are owned controls, not real-image accuracy evidence.

The first compile was void: CA1859 required the private snapshot property's
concrete collection type; 85.5620253 seconds, no tests executed. The corrected
production build passed without warnings/errors in 39.9076055 seconds. A final
test-only assertion build passed in 18.0486045 seconds. A proposed bounds
fixture was removed before execution after inspection showed detector clipping
already prevents that case; the actual rounding-collapse control remains.

Commands from the active worktree, with the existing normal-host .NET/cache
environment and reviewed OpenCV restored to both App/test runtime roots:

```powershell
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false -p:BuildProjectReferences=false
dotnet vstest tests/GraphReader.App.Tests/bin/Debug/net10.0-windows/win-x64/GraphReader.App.Tests.dll '/TestCaseFilter:FullyQualifiedName~NativeAxisCandidateReviewTests|FullyQualifiedName~NativeAxisWorkerDetectionFailureTests|FullyQualifiedName~OpenCvAxisProposalTests|FullyQualifiedName~RustFirstAxisProposalServiceTests' '/Logger:trx;LogFileName=focused.trx' '/ResultsDirectory:artifacts/native-candidate-observation-check'
```

The probe-only build with `BuildProjectReferences=false` passed without warnings
or errors in 10.34 seconds. All 20 runtime libraries matched at App and probe
roots. `--real-dev-native-review-observation-self-test` passed in 0.688 seconds,
checking missing-pin rejection and observer parity without private reads.
The actual diagnostic additionally verified the loaded OpenCV binary and one
OpenCV thread before loading the selector.

## One fixed development diagnostic

The new `--real-dev-native-review-observation-trace` mode reuses the fixed
first twelve sorted anonymized development cases and existing Rust-first
fallback routing. It calls the production native encoded-image processor
in-process to receive observations, inside the probe CPU job, with a linked
12-second deadline. Unlike the child worker, native work cannot be separately
killed; the probe's existing overall watchdog remains. Timing is explicitly
not comparable to the prior child-process measurements.

One run started `2026-11-15T14:01:43.7263081Z`, finished
`2026-11-15T14:02:00.4829127Z`, exit 0, empty stderr, 16.7229137 seconds wall.
All twelve cases/panels completed: eleven prepared panels and one full-image
fallback. Zero were unsupported, failed by exception/timeout, unattempted or
unfinished. Review availability remains **4/12**, unchanged from the preceding
profile: four Rust one-choice lists, seven Rust NO_AXIS native fallbacks and
one Rust AMBIGUOUS_AXIS without fallback.

All **7/7 native calls** produced terminal observations:

| Terminal stage | Calls | Observed cause |
| --- | ---: | --- |
| Incomplete enumeration | 5 | Exact-pair budget, 640 attempts and 639 admitted pairs in total; no ranked/returned prefix |
| Complete enumeration, integer representation | 2 | `native_invalid_rounded_geometry`; complete lists contained 180 snapshots in total |

The two complete searches checked 126 snapshots before stopping: 87 eligible,
37 rotated, and two integer-representation failures. Thus one unrepresentable
integer box discarded an otherwise populated review list. The next bounded
repair should exclude that unsupported representation after complete search,
without clamping/expanding geometry, raising a budget or changing ordinary
automatic suggestion. The five budget failures remain a separate limitation.

Preparation totaled 3,910.4633 ms and review calls 10,044.0916 ms. These are
diagnostic timings, not a performance improvement claim. No OCR, truth scoring,
image writes, candidate selection, calibration Apply or export occurred.
All 51 independent inputs remained excluded; sealed payload reads were zero.

Assignment `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`,
inventory `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`,
and selected-source digest
`4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1`
match the preceding confirmation. Historical profile pins and the unchanged451
and Mirage comparisons remain unchanged.

## Identities, acceptance and retention

Runtime was built from the observer patch over `9adee655`; no package was built
from this diagnostic. The pending 0.4.53 package remains pinned to `9adee655`.

| Artifact | SHA-256 |
| --- | --- |
| Native review source | `e3373e5185501ecf7135eeeaf42bd11c64ca206952d3de418ea40f55d5ccd909` |
| Native review tests | `32f8d1404519a8088757fff18fde8a41045e41c2066b914bae554e5a1aa6c23a` |
| App EXE | `4243aec98df86037cc0bf98260caa7b4b828f20aa25e910a64245efa64facb6a` |
| App DLL | `ba0bb13e945344420a58ffae5f00662aa97990cae1b3788a03d310c3f204997d` |
| Axis DLL | `906b1f300c2557bb8bc7f5d475bbf75b0c58ab2bca10569b183b51f33ed27c17` |
| Rust worker | `9c5ac19abc3b08632d82b5590a9e59f2794d94f92f345fbc70741e181808c0d9` |
| OpenCV native | `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31` |
| Probe DLL | `6f76f8794f4619c26b5d2323acbb135364a2e183df0c07ae1f66c785ab4b2b4e` |
| Probe EXE | `6f59cd2b29322579a886bfe278c34cc35cb91ba5d701a628bfca89d4840cac3c` |
| Probe profile | `a4f11940e2c5cff364a02b652426d667d2bc85ee407680f3481678a96703c402` |
| Focused TRX | `391ca98f184cc243e620473dc103eb35ab8e0124176b7cd35794654698af79fd` |
| Fixed-twelve report, 14,857 bytes | `43bf79c2a5143d7be36ee0864143e35660a9857aaec189eeb6159c2a250b9aec` |

Diagnostic accounting and owned parity passed. Full CI/integration is pending
at this outcome checkpoint; no production or real accuracy approval is claimed.
Code remains Apache-2.0, with no dependency/model/license change.

`artifacts/native-candidate-observation-check` contains five disposable test
files totaling 68,098 bytes. Retire them after this outcome is pushed. Keep
`artifacts/native-review-observation-dev-check` only through the pending
integer-representation repair comparison, then record and retire its raw output.
Original inputs, independent evaluation, current runtime assets, user Data and
the sole retained package are outside this cleanup scope.
