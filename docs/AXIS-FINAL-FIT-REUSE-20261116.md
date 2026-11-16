<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Reuse an identical refined line-family fit

## Problem, scope and proof

`AxisGeometryDetector.BuildFamilies` fit every refined member sequence again,
even when its dictionary already retained exactly that ordered sequence. The
second fit repeats weighted sums, covariance, interval sorting, coverage and
RMS computation, then loses the existing strict-less-than RMS replacement.
This is source-visible redundant work. It is not a new explanation for the
closed private geometry refusal or a way to reduce distinct pair counts.

Initial fitting and full refinement still run for every hypothesis. Compute the
existing member-ID key from the refined sequence before the final fit. Reuse
the existing family only when every ordered observation reference is identical.
That makes all fitting inputs and arithmetic identical to the retained fit;
the old strict RMS comparison would keep the same first object. Different
members, different ordering and joined-ID key collisions retain the original
final fit and RMS comparison. Added comparison/skip paths poll cancellation.
There is no new memo table, retained input collection, threshold, work cap,
geometry ranking or public contract. All changes remain Apache-2.0.

The acceptance requirement is exact output parity and preserved negative and
cancellation behavior on owned controls. Allocation and timing observations are
reported separately. No private or independent input, training, model choice
or new scientific value is involved. This cannot repair the real graph's
pair-budget refusal or establish automatic-calibration accuracy.

## Fixed owned comparison

Before production editing, freeze the existing Debug Axis DLL and detector
source. Build one disposable console profile against that DLL, linking the
existing `CpuBudgetJob`. Keep the same runner, Debug configuration, JIT defaults,
fixtures and half-core budget for the candidate, changing only the loaded Axis
DLL. Both processes verify **0.4992 logical core**, BelowNormal priority and
30/35-second cancellation/watchdog limits. No OpenCV extraction, App, Rust,
image or inference call occurs in this profile.

Each process uses one tiny two-axis warmup, then one synchronous solver call
per fixed case. The 4096-square duplicate control has 128 horizontal segments
from `(100 + .01*i, 300)` to `(700 - .01*i, 300)`, `i=0..127`, plus the vertical
segment `(100,50)` to `(100,300)`. The distinct control instead places those
horizontal segments at `y=120+20*i`, from X100 to X700, plus a vertical segment
from `(100,100)` to `(100,3000)`. IDs are `h-000` through `h-127` and `y`;
strength/stroke width are one, with solid recorded-fixture evidence. Each runs
once in normal and reversed input order. No private image informs these values.

Both baseline and candidate completed **4/4**, zero failures or unattempted
cases. All four input identities and complete non-timing payloads match
exactly: the request, result, uncertainty, all ranked candidates and all work
counters. Only `Diagnostics.Elapsed` is zeroed for comparison. JSON serialization
is outside timing/allocation measurement. No baseline or candidate call was
repeated to improve a result.

| Fixed case | Solver wall ms, before / after | Allocated bytes, before / after |
| --- | ---: | ---: |
| Duplicate, normal | 35.0721 / 26.9735 | 1,674,112 / 1,240,280 |
| Duplicate, reversed | 29.2772 / 24.1091 | 1,667,536 / 1,233,704 |
| Distinct, normal | 5.7138 / 5.8915 | 545,184 / 545,184 |
| Distinct, reversed | 5.0882 / 6.1057 | 545,184 / 545,184 |

Duplicate-rich allocation fell by **433,832 bytes per call**, approximately 26%.
Distinct-case allocations are unchanged and both single wall samples increased.
CPU samples have 15.625-ms granularity: duplicate normal 46.875/15.625 ms,
duplicate reversed 31.25/31.25 ms, distinct normal 0/15.625 ms and distinct
reversed 0/0 ms. These are descriptive single observations, not a latency
distribution, universal speedup or private-input performance measurement.
Process wall times were **0.7407 s** baseline and **0.5828 s** candidate.

## Correctness and build validation

Ten family-level cases compare against the pre-change orchestration, using the
unchanged fit/predicate math: duplicate, distinct, initially different sets that
converge during refinement, colliding joined-ID keys and empty refinement, each
in both orders. They compare member references, family order, binary64 scalar
bits, line and direction. Two more cases cover pre-start/in-flight cancellation
and preserve the caller's token. Independent read-only review found no material
semantic, cancellation or evidence issue.

The profile restored/built in **1.5988/4.0986 s**; Axis tests restored/built in
**2.2059/11.6540 s**, with zero errors/warnings and existing local package caches.
The targeted run passed **14/14** in **4.2574 s** including wrapper time
(376 ms VSTest); the full Axis suite passed **272/272**, zero skipped/failed,
in **6.5526 s**. No failed or void build/test/profile attempt occurred. An XML
metadata serializer initially emitted empty maps; reading the preserved TRX
attributes corrected the receipt without rerunning tests.

```text
dotnet build tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj -c Debug --no-build --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
```

The targeted filter selected `AxisFamilyFitReuseTests` and the existing
empty-refinement/non-original-coordinate/cancellation controls. The full suite
reused its existing Rust test workers without rebuilding them, supplied through
`GRAPHREADER_RUST_WORKER` and `GRAPHREADER_RUST_BLOCKING_WORKER`. All 53 Rust-class
cases passed, including parser, transport and live cancellation checks; this
does not mean 53 child launches. Reviewed OpenCV
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31` was verified
and staged only into Axis.Tests. Source App binaries/Data and the ordinary Rust
baseline stayed unchanged. No dependency or model was added.

| Evidence | SHA-256 |
| --- | --- |
| Baseline Axis DLL | `8773a9f6330adecedbace5243de895948c3855aa1f0d616d74388f05fb52aa53` |
| Candidate Axis DLL | `2cc9bfe9d5b94322052196707745cd44b28aee8200ae7fbfd678f2f690fd8d8e` |
| Identical loaded profile DLL | `5a4b721de1c723635a90496cd68032d80a196ba4c3bc7b11baf1bb1a7a4578f0` |
| Baseline report | `61ef600705d82a58d0f04fe69e3762df03b5f21d0f21afd65a5620b995b22bc2` |
| Candidate report | `52b0d4d30ac51b3bcdb1615124ec098895e7707d284d0980bccc17b58cac7581` |
| Full Axis TRX | `2e61667a715a11e09e6572f745e6b13e178eebbe90a7b6c3120a03a197f1c211` |

## Integration and retention

Source/outcome `14c4a57a3c33bd3f8ef8570b728f95d3a364c1cd` is integrated and
pushed to `origin/main`. [Full Windows CI 43869408586](https://github.com/SKang393/Graph-auto-reader/actions/runs/43869408586)
passed from **2026-11-16T12:45:09Z** to **12:55:41Z**: **3,270 .NET passes**,
30 explicit skips, zero failures; Axis contributed **272/272**. All **89 Rust
tests**, **44 CI-scope assertions**, 68 packaging checks, 62 version checks,
six ledger checks, 12 portable checks and nine localization self-tests passed.
The run used full product validation; release jobs skipped. Owned correctness
and scoped source integration pass.
The sole delivered package remains 0.4.58. This source optimization adds no
native UI, clean-machine, model or automatic-calibration acceptance claim.
After the outcome/source and full-CI record reached `origin/main` at
`57b9f1a915241e97c475dcd6bd76a4d4d7adfb73`, guarded retirement completed at
**2026-11-16T13:19:08.1352960Z**. `artifacts/axis-final-fit-check` and the
completed Axis.Tests `bin`/`obj` trees are now absent: **238 files,
75,812,274 bytes** removed. The temporary run plus test output stayed below
its 100-MB budget. No tracked files, reparse paths, Data descendants or active
consumers were found. All **507 protected file hashes** remained unchanged,
including every current-package file, shared and Debug Data, owned native-QA
inputs, source Debug App/current Rust worker, protected runtime checkpoint
and the two unrelated primary-checkout edits. Original corpus/model locations
and other tasks' work were outside the three exact deletion scopes.

The subsequent App status regression check uses a normal Release build. The
old ordinary Rust baseline's closed consumers no longer require its generated
target; that build may update it to current source. Its earlier profile/test
identity above remains historical evidence, while the source App Debug runtime
and sole packaged build keep their separate preserved identities.
