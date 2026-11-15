<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Owned native solver work control

The remaining development failures include three completed-prefix searches that
hit the native 128-pair limit. Existing pruning records found 381 of 384 attempted
prefix pairs valid, so another exclusion heuristic was not justified. This
diagnostic examines resource behavior without changing any production limit.

`OwnedNativeSearchWorkCheck` creates recorded owned line fixtures, with the
global winner deliberately last in a 129-pair search. It compares a complete
128-pair prefix oracle, a bounded refusal, an unlimited full reference and a
complete 129-bound result. A denser 257-pair fixture must refuse at 129 without
publishing a prefix. These are line-solver controls, not automatic image tests.

One run at `2026-11-15T18:24:20.2564079Z` completed at
`2026-11-15T18:24:22.0430970Z`, exit 0, empty stderr, **1.7865892 seconds** wall.
All five calls were attempted: three completed and two expected refusals, with
zero exceptions, cancellations or unattempted calls. The late winner was proven;
complete bounded/unlimited candidate ordering, result and uncertainty hashes
matched. Refusals emitted no snapshots or usable prefix result.

| Fixed solver call | Completed candidates | Wall ms | Process CPU ms | Allocated bytes |
| --- | ---: | ---: | ---: | ---: |
| 128-pair complete prefix reference | 128 | 240.3823 | 218.75 | 11,560,088 |
| 129 pairs, bound 128 | Refused | 8.0351 | 15.625 | 466,952 |
| 129-pair complete reference | 129 | 456.6170 | 140.625 | 11,555,584 |
| 129 pairs, bound 129 | 129 | 521.8658 | 187.5 | 11,553,744 |
| 257 pairs, bound 129 | Refused | 20.4872 | 15.625 | 804,952 |

Solver totals were **1,247.3874 ms wall**, **578.125 ms process CPU** and
**35,941,320 allocated bytes**. Process lifetime peak working set rose from
38,662,144 to 50,380,800 bytes. Allocation deltas are process-wide and peak
working set is cumulative, not isolated per-case memory. JIT/cache/scheduling
effects are not separated, and no throughput claim follows from these samples.

The existing parent half-core/BelowNormal budget applied. Per-call cancellation
was 12 seconds and overall cancellation 60 seconds. The actual native child's
hard watchdog was not used. OpenCV extraction, decoding, IPC and complete native
latency are excluded. This establishes why prefix publication is unsafe and
provides a first solver cost observation; it does not authorize a larger
production limit by itself.

Command: `dotnet <probe.dll> --owned-native-search-work-check`.
Probe SHA-256:
`6823f34cf3e5482ffa40dbb072ba2c4ebad10193cb5ccfe56921222477ad26bc`.
Report, 9,805 bytes:
`922135ef10789a91169ca92687099da24c141804b07f8205b86f37b3618cb718`.
Source base: `0642f21f42894cfc2997dab40dcce4ae932ad480`.

No private input, sealed read, model, threshold or production budget changed.
Source remains Apache-2.0; no dependency or packaged build was added. The raw
`artifacts/native-endpoint-owned-check/search-work.json` remains only for the
next owned resource-bound decision; retire it after that consumer finishes and
the source/outcome record is pushed. The integration uses the same probe build
as the [endpoint checks](NATIVE-RASTER-ENDPOINTS-20261115.md).
