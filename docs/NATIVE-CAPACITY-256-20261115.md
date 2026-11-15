<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Fixed owned 256-pair capacity observation

After the [regular-grid optimization](REGULAR-GRID-FINALIZATION-20261115.md),
one predeclared diagnostic bound of 256 was tested, without a cap sweep or private
answer-based selection. The existing production bound was still 128 during this
observation. All controls are owned recorded lines, not automatically detected
image geometry.

The unchanged helper supplies measurement, failure accounting and identity hashes.
A separate partial adds five plans: regular 256-pair unlimited/bounded, irregular
256-pair unlimited/bounded, and 257 pairs that must refuse at 256. The irregular
case contains two 128-line clusters, spacing 10 pixels, separated by a 1,230-pixel
gap, within a 4096-square frame. It defeats the full-run regular-grid shortcut
and exercises the existing exhaustive fallback. Both complete searches place a
strictly better global winner after the first 128 families.

## Actual run

One probe-only build passed, zero warnings/errors, **9.90 seconds** MSBuild /
**10.6981973 seconds** wall. The fixed run completed from
`2026-11-15T19:12:58.1832618Z` to `2026-11-15T19:13:01.3146821Z`, exit 0,
empty stderr, **3.1312616 seconds** wall. Five attempted: four complete, one
expected refusal, zero failures/cancellations/unattempted cases.

Both bounded completions matched their unlimited references in input, complete
candidate ordering, geometry result, uncertainty, diagnostics excluding elapsed
time, and all work counters except expected bounded-envelope bookkeeping. Both
late winners were proven. The dense control refused at request 257 / attempt
256 with zero published candidates and no usable geometry.

| Owned call | Wall ms | Process CPU ms | Allocated bytes | Lifetime peak WS bytes |
| --- | ---: | ---: | ---: | ---: |
| Regular 256 reference | 89.7139 | 78.125 | 1,373,472 | 41,934,848 |
| Regular 256 bounded | 27.3073 | 15.625 | 1,374,984 | 43,352,064 |
| Irregular 256 reference | 942.3837 | 609.375 | 73,335,120 | 51,060,736 |
| Irregular 256 bounded | 1,019.6161 | 328.125 | 73,116,984 | 51,511,296 |
| Dense 257, bound 256 refusal | 21.8902 | 15.625 | 925,904 | 51,568,640 |

Totals: **2,100.9112 ms** solver wall, **1,046.875 ms** process CPU and
**150,126,464 bytes** allocated. These are one ordered set of observations,
including cold JIT/scheduling effects. Allocations are process-wide and peak
working set cumulative. The irregular fallback still allocates substantially
more than the regular case. No throughput, full-App latency or general
worst-case guarantee is claimed.

The verified half-core/BelowNormal parent budget, 12-second cooperative case
deadline and 60-second overall deadline applied. The actual child watchdog,
active child cancellation/disposal, OpenCV extraction, decoding and IPC were not
executed. Normal-response versus review-choice limits were not exercised in this
solver-only run and require separate App checks before product acceptance.

## Reproduction and identity

Command: `dotnet <probe.dll> --owned-native-search-capacity-check`.

| Item | SHA-256 |
| --- | --- |
| Source base | `11196b00d4afa18c208700ddae456d4236c74b40` |
| Axis DLL | `6fa20c38aa02776da2f000ef6d954df051be41283af8244d4bd931259ebd377f` |
| Probe DLL | `ec26e56873b1bed39aaa2bd6ae590e38fed6148772b98d71ce6f47848c41fe13` |
| Report, 12,392 bytes | `dfdc9c504d4a1af0a9f3c91c70618ba4c0c1854dfd53406099878b3ddae2603e` |
| Regular input | `48a7bc7292ab59abbd2260b9232fe360945f1c8ceb4785ba5d8ae4ba3aae1952` |
| Irregular input | `66098214fe0d0914026d47f228d6b2398f4d0b4dccf8948dfee2663e4e6982a1` |

Private/sealed reads, training, model/threshold changes and image writes were
zero. Source remains Apache-2.0; no dependency, package or version change.
Diagnostic safety/parity checks pass. The normal native worker now uses the
named 256-pair bound. Its 12-second solver/child deadlines, parent deadline,
half-core budget, response limit and review warnings are unchanged. The explicit
candidate chooser remains bounded at 128 pairs and 64 KiB, with no prefix
publication.

App controls verify complete normal searches at 129 and 256, including a winner
after pair 128, parity with unrestricted geometry/confidence, and empty refusal
at 257. The separate chooser refuses 129 without exposing partial choices.
These controls passed in the full **1,207-test App suite**, zero skips/failures,
**49.1906291 seconds** wall. See the [integrated test record](INTERVAL-LABEL-GROUPING-20261115.md).
The solver controls do not establish a spawned noncooperative-child watchdog
test or a general worst-case latency guarantee.

Retain `artifacts/native-search-capacity-check` only through the named normal
worker integration decision and pushed outcome. The prior 129-case solver
reports may be retired after this decision and their recorded outcomes are
pushed. Preserve original, independent and active runtime inputs.
