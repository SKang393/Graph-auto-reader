<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Regular-grid finalization work

The [fixed owned solver control](NATIVE-SEARCH-WORK-CONTROL-20261115.md)
showed that completing a 129-pair regular-grid search allocated about 11.55 MB,
whereas refusing an incomplete prefix skipped finalization. Source inspection
located repeated allocation/scanning of every overlapping grid window, cubic in
the number of eligible divider families. This was a solver cost, not evidence
that pair scoring itself required those resources.

## Correction and equivalence

`FindRegularGridFamilies` now checks the full sorted interval first, retaining
the exact existing `Average`, centered `Sum` variance and CV comparison. When
that interval passes, the old start-zero loop would eventually insert every
family in the same sorted order. No later interval can add a new member. The
shortcut therefore returns the same union and insertion order. When it fails,
the original exhaustive algorithm and arithmetic remain. Cancellation now
reaches filtering, projections, gaps, variance, window loops and insertion.

No recognition or geometry threshold, ranking score, pair limit, chooser limit,
model or public contract changed. Irregular worst-case work remains cubic;
this optimization alone does not justify increasing the production 128-pair cap.

## Validation and failure accounting

Eleven new cases compare a literal pre-change window oracle against public
detector results on regular grids, irregular grids with a regular subset, and
the immediately adjacent doubles below/at/above the existing CV boundary, in
both input orders. They check membership, divider results, geometry and warnings;
one also verifies cancellation in the new full-run calculation.

The initial full Axis run took **11.9950909 seconds**, with 223 passed, two
failed and four Rust-path-dependent skips. Both failures were test assertions
comparing an owned coordinate 470 with its fitted 470.00000000000006. The test
coordinate comparison now allows 1e-9 roundoff; set counts and the CV oracle
remain unchanged. The corrected full run passed **225**, skipped the same four,
and failed zero in **1 second** test time / **8.0069644 seconds** wall.

One unchanged five-case solver-control invocation completed from
`2026-11-15T18:54:56.8261643Z` to `2026-11-15T18:54:57.8468534Z`, exit 0,
empty stderr, **1.0204417 seconds** wall. Five attempted: three completed,
two expected budget refusals, zero exceptions/cancellations/unattempted cases.
All five input/status identities and all complete candidate/result/uncertainty
identities matched the saved baseline. The late winner remained proven; no
refused prefix produced usable geometry.

| Owned call | Before / after wall ms | Before / after allocated bytes |
| --- | ---: | ---: |
| Complete 128-pair prefix oracle | 240.3823 / 74.2339 | 11,560,088 / 694,792 |
| 129 pairs, bound 128 refusal | 8.0351 / 7.6501 | 466,952 / 464,936 |
| Complete 129-pair reference | 456.6170 / 8.2430 | 11,555,584 / 687,920 |
| Complete 129-pair bounded | 521.8658 / 6.4621 | 11,553,744 / 696,288 |
| 257 pairs, bound 129 refusal | 20.4872 / 23.6927 | 804,952 / 804,696 |

Totals: solver wall **1,247.3874 to 120.2818 ms**, process CPU **578.125 to
125 ms**, allocations **35,941,320 to 3,348,632 bytes**. These single solver
samples include JIT/scheduling effects and process-wide allocation accounting.
They exclude image decoding, OpenCV, IPC and the native watchdog. No application
latency or general irregular-input speed claim follows. The same verified
half-core/BelowNormal parent budget applied; private and sealed reads were zero.

## Commands and identities

```text
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet <probe.dll> --owned-native-search-work-check
```

Source base: `308d7a823afbec316a73f4e2e16b528f27ca9c2f`. The owned solver
helper is unchanged. The new probe dispatch contains an independent image-property
diagnostic which was not invoked during this solver comparison.

| Item | SHA-256 |
| --- | --- |
| Optimized Axis DLL | `6fa20c38aa02776da2f000ef6d954df051be41283af8244d4bd931259ebd377f` |
| Probe DLL | `8f34d8d7d38cdd805ec79d325a62375b243f98fb0d55539f71bcb4f2c8623937` |
| Initial failed TRX | `bce31649a2f9f47aae82724721e4c5ce01f50a31977b585009e7ee65f8db0f07` |
| Corrected TRX | `fefbf31157d38a21aad0a1ae8dcab0d92911bbad435007e986a05db55b5ff3a7` |
| Solver after report, 9,777 bytes | `7e8d3ac8c17447fc37cc3388a0bcc79b22a69dae227267801b499f47934caf84` |

No dependency/license change; source remains Apache-2.0. No package or version
increment. Local equivalence/resource checks pass; full Windows CI is required
after integration. Automatic calibration remains incomplete.

Retain solver before/after reports only for the next bounded owned capacity
decision. Retire completed TRXs after this outcome reaches `origin/main` and
integration consumers finish. Preserve original, independent and active runtime
inputs and the sole 0.4.54 package.
