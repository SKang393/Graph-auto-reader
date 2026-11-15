<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Grid subset completion

Source base: `1fe50b58b07ef9be7d23f8252016ad86ce7ee32e`.
This follows [regular-grid finalization](REGULAR-GRID-FINALIZATION-20261115.md)
and the [fixed owned capacity observation](NATIVE-CAPACITY-256-20261115.md).

## Problem and change

When the full interval fails the existing spacing check, the detector enumerates
subset intervals. Even after accepted subsets cover every eligible family,
remaining windows repeat arithmetic and allocations without changing the union.

`AxisGeometryDetector.FindRegularGridFamilies` now returns once that union's
count equals the complete eligible set. Every inserted member belongs to that
set, so remaining intervals can add nothing or change insertion order. The
existing arithmetic, thresholds, ranking, pair budgets and refusal behavior are
unchanged. Cancellation is checked immediately before the new return.

Only `AxisGeometryDetector.cs` and `RegularGridFinalizationTests.cs` implement
this follow-up. Owned tests add two separated regular three-line clusters in
both input orders. Their full interval fails the existing coefficient check,
while the literal old exhaustive oracle includes all six families. Geometry,
grid/divider membership and warnings are checked. Cancellation still publishes
no candidate prefix. Existing boundary and irregular-subset controls remain.

## Validation

The full Axis suite passed **228**, with **4 existing Rust-asset skips**, zero
failures: about one second of tests and **12.3360311 seconds** command wall.

```text
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --logger 'trx;LogFileName=axis-suite.trx' --results-directory artifacts/grid-subset-completion-check
dotnet <probe.dll> --owned-native-search-capacity-check
```

Only the verified freshly built Axis DLL was copied into the existing probe
output. The probe and other runtime libraries were unchanged. One owned run
recorded `2026-11-15T19:36:08.9183274Z` through
`2026-11-15T19:36:11.9362866Z`, measured wall **3.0097613 seconds**, exit zero,
empty stderr. All five cases were attempted: four complete results, one expected
257-pair refusal at bound 256, zero failures/cancellations/unattempted cases.

Cross-run comparison independently verified all five input hashes and raw work
snapshots against the prior report. All four completed candidate-order/result
hashes and uncertainty objects match exactly. Both comparison groups' uncertainty
and normalized-work hashes also match across runs. The refused case retains the
same terminal status and request 257 / attempt 256 counts, with no candidates or
geometry. Within-run oracle parity alone was not used as this proof.

## Resources and limits

| Owned call | Wall ms, before / after | CPU ms, before / after | Allocated bytes, before / after |
| --- | ---: | ---: | ---: |
| Regular 256 reference | 89.7139 / 104.8730 | 78.125 / 109.375 | 1,373,472 / 1,379,664 |
| Regular 256 bounded | 27.3073 / 29.3975 | 15.625 / 15.625 | 1,374,984 / 1,374,984 |
| Irregular 256 reference | 942.3837 / 1,137.7077 | 609.375 / 593.750 | 73,335,120 / 62,061,200 |
| Irregular 256 bounded | 1,019.6161 / 664.8755 | 328.125 / 390.625 | 73,116,984 / 61,720,088 |
| Dense 257 refusal | 21.8902 / 22.8906 | 15.625 / 15.625 | 925,904 / 925,904 |

Totals: solver wall **2,100.9112 to 1,959.7443 ms**; allocation
**150,126,464 to 127,461,840 bytes**, down **15.10%**. Process CPU **increased**
from **1,046.875 to 1,125 ms**, up **7.46%**. The irregular reference was slower;
the irregular bounded call used more CPU despite lower wall time. Final
process-lifetime peak working set was **51,568,640 to 51,982,336 bytes**.

These are single ordered samples with JIT/scheduling effects, process-wide
allocation counters and cumulative peak memory. They establish allocation
reduction, not a general CPU or latency improvement. Both used BelowNormal and
the verified **0.4992 logical-core** cap, twelve-second cooperative case and
sixty-second overall deadlines. OpenCV extraction, decoding, IPC, actual child
watchdog and disposal were excluded. The helper's “B256 diagnostic only” text
describes these solver requests; the separate App normal-search bound is already
256, while explicit review remains 128. This follow-up changes neither.

## Identities and closeout

| Evidence | SHA-256 |
| --- | --- |
| Prior owned capacity report | `dfdc9c504d4a1af0a9f3c91c70618ba4c0c1854dfd53406099878b3ddae2603e` |
| After report, 12,379 bytes | `79b3b08d6bf34521883eb1ec3cfbb61b6d8053780112f6e20c8bcd41d54694bd` |
| Full Axis TRX | `cf29e49ea71973f525fd48693e0b7c3aa486ba3f4062a843fba46f74f2655310` |
| After Axis DLL | `d946441adcef59a717e11d12df740c5300f369a7e873b37344bc2ab6a647df8d` |
| Unchanged probe DLL | `f17e269a7f1dee1c293abdf38d320d938f4b446126df5d1dfec342f007c838d4` |

No dependency or license change; source remains Apache-2.0. No private/sealed
reads, private rerun, scientific-value change or new package. This optimization
cannot clear pair-budget refusals, which occur before grid finalization.

Owned validation passed and source/outcome `a65f180c` was pushed in integrated
head `991b3f3031ac315e8b93692a15de30fcebaa0b29`. Full Windows
[CI 43755505445](https://github.com/SKang393/Graph-auto-reader/actions/runs/43755505445)
passed all 13 .NET suites: 3,081 passed, 30 skipped, zero failed. Rust passed
72 tests, and all applicable policy and packaging checks passed, including
68 release-artifact validation controls. Public release jobs were skipped.
Completed owned capacity and grid outputs were retired after push and
consumer checks; see the [retirement record](INTERVAL-CLUSTER-CONTAINMENT-20261115.md#completed-output-retirement).
Originals, current runtime inputs, user Data and the sole verified package remain.
