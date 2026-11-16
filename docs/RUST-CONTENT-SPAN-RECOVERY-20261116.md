<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Recover a disconnected content projection through the existing strict solver

## Failure and scope

The [fixed development diagnosis](NEXT-DEV-PROJECT-CALIBRATION-20261116.md)
reported `CONTENT_EXTENT / DISCONNECTED`, before any grid candidate was checked.
Source inspection found that a successfully selected content projection stopped
on a disconnected measured span, while a missing content projection could use
the existing strict connected-run solver. This is a routing hypothesis, not a
claim that the private graph has any particular correct geometry.

An owned 512-square control contains a fully observed L with three outward ticks
per axis and a separate disconnected decoy column. Without changing the fixture,
the existing connected-run solver returned the exact planted plot and six ticks,
while normal `propose` returned `NO_AXIS`. The focused red test failed as intended:
0 passed, 1 failed, 85 filtered; exit 101, **4.7285 s wall** (0.01 s test body).
No private answer, image-derived threshold, model or training was used to build it.

## Implementation and guards

`measure_axes` distinguishes only its required-span disconnection with an internal
status. Normal `propose` may send that status through connected-run recovery only
after content-relative selection and while the request-wide bridge budget remains
available. The recovery reuses the existing observed-ink, outward-tick, complete
pair-search and unique-plot requirements. It does not reset bridge accounting or
change a threshold, search window, pair cap or resource allocation.

Primary detection, missing origin/intersection, short spans, insufficient ticks
and projection ambiguity do not enter this new route. The internal status is
never emitted on the wire. Ineligible recovery retains `NO_AXIS`; eligible
recovery preserves the strict resolver's existing result, including
`AMBIGUOUS_AXIS`. Explicit review-candidate enumeration keeps its existing path.

Four new tests cover the exact owned recovery, genuine gaps, multiple qualifying
plots, preserved nonzero bridge accounting, exhaustion before/during recovery,
and non-span/non-content exclusions. Initial full validation passed 88/89 in
**4.9724 s wall**; the sole failure was the existing padded broken-span test's old
exact `NO_AXIS` expectation. It now reaches the strict resolver and still refuses
with `AMBIGUOUS_AXIS`. Only that expected refusal/comment changed afterward.
Final validation passed **89/89**, zero ignored/failed, **3.7706 s wall**
(0.62 s test body).

Command: `cargo test --manifest-path native/graphreader-engine/Cargo.toml
--target-dir artifacts/rust-content-recovery-check/native --offline --locked
-j1 -- --test-threads=1`, with the installed VS2022 x64 environment.

An isolated release worker build passed in **3.1086 s**. The unchanged baseline
worker was retained in its original location. Existing owned transport controls
then passed **8/8**: baseline ordinary, candidate ordinary and candidate diagnostic
responses match on those established controls, and the C# client transport matches.
There were 25 worker calls, two valid parser checks and 12 malformed-response
refusals, zero private reads, empty stderr, no timeout, **1.7346 s wall**.
Command: `dotnet <probe.dll> --axis-decision-self-test <baseline.exe> <candidate.exe>`.
This comparison does not contain the newly recovering fixture; its new behavior
is established by the Rust regression above.

| Identity | SHA-256 |
| --- | --- |
| Baseline worker | `08538da44ff7f680f951c7cbe08c806801b74d9c44cff982bf507ae0f995edca` |
| Candidate worker | `eeb2515a48546c9b50f9c8221f334c028a9dadce34cb1fa32ca7c5d477e87430` |
| Tested local Rust source | `6d98820666defa35029cf1744d81219ca3641966ed708ed766b0a7efcb8e548b` |
| Owned wire report | `43b5f12fca6d1438ca757636738d363fbad8de585d7d698af093c5c1e2b84a27` |
| Frozen candidate runtime profile | `950cf8f66bd6d131435495d40521806e222bc16bc7e283bfc24cb15d32e4c47a` |

## One changed-stage confirmation, fixed before execution

Use the same frozen ordinal-12 original and sole prepared panel, verified against
locator `1aee099f1153ea3774e17cb047a743e6215cae643e0ec69d94868ba25df1e274`
before inference. Only the Rust worker changes; all twenty App libraries, the OCR
asset/manifest, geometry fallback, numeric association and scientific safeguards
stay fixed. The baseline runtime profile remains separate and unchanged.

The diagnostic makes one Rust call through the already parity-checked diagnostic
transport, using its ordinary proposal for the normal downstream path and keeping
its counters. It does not separately repeat primary detection. If geometry exists,
continue ticks/OCR/association once; otherwise retain every unreached stage. Do not
advance the ordinal or change the repair based on private calibration answers.
No candidate sweep, Apply/export, model selection or sealed/independent read is
authorized by this confirmation. Overall bounds remain 90/95 seconds with the
existing half-core parent/child budgets. Complete output stays ignored/private.

## Actual confirmation

The confirmation adapter built with zero warnings/errors in **9.3742 s**. Its
owned accounting self-test passed in **0.3462 s**, with zero private/model/worker
calls. All twenty managed/native App runtime files still matched the baseline
profile. Only the isolated Rust worker changed.

One confirmation ran from **2026-11-16T11:41:25.8978305Z** to
**11:41:30.8355283Z**, exit 0, empty stderr, no timeout, **4.9325 s wall**.
All source/preparation/final-identity stages completed; one project and one
prepared panel were attempted, with zero operational exceptions. Automatic
geometry remained refused: **0/1 usable geometries**, the same outcome as the
baseline. The Rust trace now reaches `CONNECTED_RUNS`, with one row band,
one column band, one pair checked, one connectivity rejection and zero qualified
pairs; final reason is `NO_VALID_PAIR`. The unchanged native fallback was also
called once. Rectification, ticks, OCR and association each remain unattempted
for that panel; their absence is not counted as success.

The input identities and all 27 protected hashes matched. Sealed/independent,
truth and other-project body reads were zero. Total diagnostic stages took
**4,627.6728 ms**. Baseline wall time was 3.9404 s; these single observations use
different diagnostic transport and are not a throughput comparison. No real
accuracy or speed improvement is established. The owned routing defect is fixed,
while this real graph's geometry bottleneck remains.

Confirmation probe SHA-256:
`ffe3c0acb100df43ef8d70a05afbe31d137b00e50382ff7fc9adda54e7ba12c1`.
Private report, 6,810 bytes:
`023fc68d240ad7bdf7eca362e3214f654df697d35fa161bd745c0b83ab13bca9`.
The confirmation is closed; do not repeat it unchanged or tune against its
unavailable private calibration answers.

Source/outcome commit `8516b75e327a00c33114c4a41026b5b1ae1dce1e` is integrated
and pushed to `origin/main`. Exact-head Windows CI
[43860789576](https://github.com/SKang393/Graph-auto-reader/actions/runs/43860789576)
passed from **2026-11-16T11:47:11Z** to **11:58:41Z**, with full product
validation (`full_changed_path`). It passed **3,258 .NET tests**, with 30
explicit skips and zero failures, **89 Rust tests**, 68 packaging checks,
62 version checks, six ledger checks, 12 portable checks and nine localization
self-tests. Both next-project owned self-tests passed with zero private reads.
The new routing behavior is covered by Rust regression tests and the single
local changed-stage confirmation; there is no separate routing-specific CI
self-test. Release/publish jobs were skipped.

Source remains Apache-2.0; no new
dependency, model, production approval or package was added. The sole delivered
build is still 0.4.58. The following image-property inspection is now complete
and is recorded in the linked development diagnosis. After its concise outcome
is pushed, retire the closed raw reports, 996-byte locator, inspection PNG and
isolated Rust build. Preserve original data, active user state, current models
and the package.
