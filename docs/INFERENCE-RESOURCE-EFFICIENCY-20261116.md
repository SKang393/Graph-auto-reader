<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Inference key hashing and idle tensor retention

## Scope

The calibration OCR worker already reuses its process, reader and model sessions.
Source inspection found two separate costs inside that existing design: each
tensor float was appended individually to SHA-256, and every distinct returned
tensor shape remained pinned until session disposal. This batch addresses those
mechanisms without changing OCR models, provider selection, cache semantics,
calibration evidence or scientific values. No private or sealed data is needed.

## Identical keys with bounded hash appends

`InferenceCacheKeyDeriver` now appends at most 16,384 floats, or 64 KiB, per call
on little-endian systems. It reads the same underlying float32 bytes without a
tensor-sized temporary copy. The explicit per-float little-endian conversion
remains for other platforms. Names, dimensions, model/provider identity,
parameters, contract version and resulting key format are unchanged.

Compatibility tests compare the complete key with the prior canonical stream at
lengths 0, 1, 17, 16,383, 16,384, 16,385 and 32,771. They preserve input bits and
cover positive/negative zero, finite extrema, infinities and distinct NaN
payloads. Name, shape, output, model, parameter and provider variants still
produce distinct keys. All **107 Inference tests passed**, with **one existing
optional provider skip**, zero failures, **3 s** reported test time and
**15.1552645 s** command wall time:

```text
dotnet test tests/GraphReader.Inference.Tests/GraphReader.Inference.Tests.csproj -c Debug -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true --logger "trx;LogFileName=candidate-inference.trx" --results-directory artifacts/inference-key-hash-check
```

One fixed owned comparison used the same compiled probe with the unchanged
baseline Inference DLL, then the tested candidate DLL. The probe verifies and
locks the loaded DLL, performs one 32-value warmup and one derivation each for
`[1,3,48,320]` and `[16,3,48,320]`. Values are the fixed finite sequence
`((index % 2049) - 1024) / 1024f`. Construction and before/after buffer hashing
are outside the timed call. There is no model loading, provider discovery,
inference, worker, image, private or sealed activity. The same 0.4992 logical-core
budget and 30/35-second cancellation/watchdog limits apply.

| Tensor batch | Baseline key derivation | Candidate key derivation | Thread allocation, both |
| --- | ---: | ---: | ---: |
| 1 | 2.4796 ms | 0.6007 ms | 4,352 bytes |
| 16 | 38.9846 ms | 1.6001 ms | 4,352 bytes |

Both executions completed **2/2**, with **zero failures/unattempted cases** and
exactly three derivations including warmup. Both complete keys and before/after
value-buffer hashes matched. Total tensor input was 3,133,440 bytes. Baseline
wrapper/core time was **0.469565 / 0.0865427 s**; candidate wrapper/core time
was **0.6288422 / 0.0528847 s**. Wrapper timing includes different setup costs
and is not the optimization metric. These are single observations of key
derivation, not OCR or application latency estimates.

The single probe-only build took **9.3154911 s**, with zero warnings/errors and
no project-reference rebuild. After the baseline, the Inference test build
produced the candidate. Only its DLL was temporarily staged for measurement;
the original probe dependency was restored afterward. The probe executable and
runner sources did not change between runs. The four source Data files retained
their hashes. There was no baseline rerun.

| Evidence | SHA-256 |
| --- | --- |
| Baseline Inference DLL | `c89228df3dbd034db7351dac962d9d917b48560203188b445e62c7421d213bb4` |
| Hash-only candidate Inference DLL | `a1465f153ff220d68f40e64b09afb67e00bb702d85309181278c96d220359cd1` |
| Same probe DLL | `bbbb8a8d547d666f6b4b7dd7441b7692de23f7a34b3d8f489f49d2ca01284bbd` |
| Baseline report | `2787f52cca3a5108a72dee81be421a821e346fd102d7094c7e8db0f0f1453b5f` |
| Candidate report | `7de58aca888b2307d560f8f7c0cb0a78ca2934ef983fe5b2915589d45e21be0d` |
| Comparison receipt | `38ddbb412a8bda5d02fb1f6db74822adaccfcc3e2d237563a5dadd3e21c76c995` |
| Hash-only Inference TRX | `986ded8f9161ce1911d4f8b85dc1efcfca1b80f1984124a823010406021bee3e` |

## Idle tensor bound

The unchanged pool's owned failing-before control reused all nine distinct
shapes `[1,3,48,width]`, with widths 320 through 576 in steps of 32. Actual rented
float lengths were five arrays of 65,536 and four of 131,072: **3,407,872 bytes**
retained. The expected entry-bound assertion failed once, with **1 failed test**,
91 ms reported test time / **6.0872048 s** command wall time. This demonstrates
shape retention, not an observed 16 MiB breach, out-of-memory failure or delay.

Each session now retains at most **eight idle tensors** and **16 MiB** of actual
pooled buffer capacity. These are fixed resource limits, not values selected
against model accuracy or private inputs. The idle list evicts least recently
used entries until both limits fit. Renting removes the entry; returning clears
the requested region. Eviction disposes the native tensor and returns its array
outside the lock. Shapes with keys longer than 256 characters and individually
oversized buffers remain usable but are not retained idle. Empty shape buckets
cannot accumulate. Active leases survive pool disposal; new rents are refused,
including a creation that races disposal.

This bounds this pool's idle pinned buffers and metadata. It does not bound
active inference memory or the shared ArrayPool's retention of unpinned arrays.
No additional recognition-result cache or public configuration was introduced.

The full Inference project passed **112 tests**, **one existing optional skip**,
zero failures, **2 s** reported test duration / **11.9633539 s** command wall.
After review, two coverage improvements added cumulative byte eviction below
the entry limit and direct reads of live native tensor data after pool disposal.
Only the changed pool suite was rerun: **6/6 passed**, zero skips/failures,
**137 ms** test duration / **6.7366954 s** command wall. Other controls verify
same-shape reuse, clearing, actual rather than requested byte accounting,
oversized bypass and concurrent distinct leases. Existing real CPU identity-model
tests in the full suite still verify output and same-shape reuse. The new pool
controls invoke no model or private image.

```text
dotnet test tests/GraphReader.Inference.Tests/GraphReader.Inference.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --logger "trx;LogFileName=green.trx" --results-directory artifacts/tensor-pool-bound-check
dotnet test tests/GraphReader.Inference.Tests/GraphReader.Inference.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter "FullyQualifiedName~ReusableTensorPoolTests" --logger "trx;LogFileName=targeted.trx" --results-directory artifacts/tensor-pool-bound-check
```

| Evidence | SHA-256 |
| --- | --- |
| Combined Inference DLL | `ae1d01b60ffd0bc5df8f237e7c841efb843ddf26866fbb7403cbff9fbd0359a7` |
| Full Inference TRX | `42a56484d046d28d5414ecf563a29ff593fa5eb7a343a156c7a54383e73a8a86` |
| Final pool controls TRX | `66f34c2303a2660b2220f85023d54bd08b1ba1060f632a70b54d531be9593ccf` |

The hashing comparison was not repeated after this independent pool repair.

## Integration and retention

Source/outcome commit `e99a9aecd9dc25f3afbb4bb39b2ff87e9f85db12` is integrated
into `origin/main`. The combined checkpoint `4db78d87bcd24eac4e92dc96e7201f0ab2f615f2`
passed [full Windows CI](https://github.com/SKang393/Graph-auto-reader/actions/runs/43806598211):
**3,220 .NET tests passed**, **30 skipped**, zero failures, plus **80 Rust**,
**68 packaging** and **9 localization** checks. Inference passed **112**, skipped
**two**, with all six new pool controls executed. The skips were the opt-in
production classifier store test and DirectML initialization when that provider
was absent. The local hardware run had only the store skip. This CI result does
not approve any model or complete end-to-end digitization.
No model, dependency, persisted schema, provider policy, package, release or
license changes are needed. Source carries Apache-2.0 notices. The sole delivered
package remains 0.4.56, and native QA remains blocked by the stale desktop stop.
After the coherent source/outcome push and CI review, retire completed outputs
under `artifacts/inference-key-hash-check` and `artifacts/tensor-pool-bound-check`
and inactive test/compiler outputs. Preserve current runtime models, source Data,
originals, owned QA inputs, the protected runtime checkpoint and sole package.
No paid credits or reset redemption were used.
