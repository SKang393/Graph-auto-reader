<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native axis work accounting and frame-clipped extents

Source baseline: `20edf2f41f13a451e7d1e4bcc9c46aff31aa2885`.
This is an unpackaged source diagnostic, not an automatic-calibration approval.

## Defect, change and scope

The existing exact-pair budget could be spent on positive raw axis projections
whose final frame-clipped extent is zero. Scoring already rejects these pairs.
The bounded path now reuses precisely the same extent calculation before
charging contact work. There is no new tolerance, score, cap or candidate-order
rule. Unlimited enumeration retains its scoring-stage calculation. Owned
controls place 129 impossible clipped contacts around one valid plot, in both
axis directions and both input orders; a tiny positive extent stays eligible.

An optional aggregate observer records line/family counts, cheap work, extent
rejections, exact requests/attempts, executed contact rejections and scored/admitted
pairs. It reports whether enumeration completed and distinguishes cancellation
and budget failure. An over-budget request is not an attempt; contact counts
respect short-circuit evaluation. Observer exceptions do not alter detection.
Normal requests do not allocate the counter object. No locations or identities
are emitted by this observer, and no incomplete candidate list is published.

The new opt-in probe uses the unchanged first-twelve development selector,
unchanged Rust diagnostic worker and reviewed runtime assets. Native fallback
runs once in-process with one OpenCV thread under the existing parent CPU
budget. Twenty runtime files, App/Rust identities and the actual loaded
OpenCvSharpExtern module are hash-checked before corpus payload reads. Historical
diagnostic profiles and the unchanged451/Mirage baseline are untouched.

## Owned checks and complete failure accounting

Normal-host serial Debug commands used existing dependency caches and
`--no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false`.
The full run additionally used `--no-build`. The reviewed OpenCV DLL was staged
after compilation and before the full native-capable suite.

| Check | Passed / failed / skipped | Test time / wall time |
| --- | --- | --- |
| New `FrameClipped` controls before repair | 1 / 4 / 0 | 223 ms / 14.257 s |
| `AxisPairEnvelopePruningTests` after repair | 39 / 0 / 0 | 794 ms / 12.623 s |
| Full Axis, clipped-extent change | 174 / 0 / 2 | About 2 s / 5.265 s |
| Work observer plus envelope controls | 52 / 0 / 0 | About 1 s / 18.258 s |
| Full Axis, observer included | 187 / 0 / 2 | About 3 s / 6.867 s |

The first observer compilation was void: CA1068 at the private `DetectCore`
signature, 5.347 s, zero tests. Moving its cancellation token to the final
parameter repaired the analyzer failure. The two skips are existing optional
Rust-worker tests, not newly excluded failures.

TRX SHA-256 values, in table order:

```text
40985957c15976b22ffd2a64c30eb286c7ae8bb03878949b99575ace6f1cba29
6ec8b90d84664023cb07c87eaa330c98dca93e08db8f0d06180b657541cd26b2
bc87d4bceac0f2506912d41fc9f1ca722541d998e11590f9dabd7b98e347d4f5
86491222c3d77412c962f65a74d3de7dcc51717ec2ff43c8421a8ca391613ff4
784bbf47cb664f67b2b93051919c00110fbbf1ffa043f26622032ce36c4337ff
```

Probe build: 77.317 s; probe-only pin rebuild: 8.871 s. Both had zero warnings
and errors. The owned preflight passed in 1.069 s, exit zero and empty stderr:
observer on/off parity, count invariants, missing-pin rejection and actual loaded
OpenCV identity. It read no private input. Self-test report SHA-256:
`73fbef0296d2d897aa260801807511a88ff992b7652a3f9264f140f2ea27694f`.

## One fixed development diagnostic

```text
GraphReader.CalibrationOcrProbe.exe --real-dev-native-work-geometry-self-test <source-App.exe> <reviewed-assets> <unchanged-Rust.exe>
GraphReader.CalibrationOcrProbe.exe --real-dev-native-work-geometry-trace <authorized-corpus> <source-App.exe> <reviewed-assets> <unchanged-Rust.exe> --explicit-opt-in
```

The single diagnostic finished at `2026-11-15T12:23:33.1984004Z`: exit zero,
empty stderr, 14.0945721 s wall time. All twelve inputs and panels were attempted
and finished: eleven prepared panels and one full-image fallback. Seven returned
geometry and five did not. There were zero unsupported dimensions, other panel
failures, timeouts, unfinished executions or unattempted panels. Five native
fallbacks returned no geometry: three exact-pair budget failures and two complete
but ambiguous searches. Aggregate coverage and failure counts remain unchanged
from the preceding forward-extent report. No new per-case byte-parity claim is
made against that retired raw report.

| Native search group | Searches | Exact requests / attempts | Contact rejections | Scored and admitted pairs | Clipped-only extent rejections |
| --- | --- | --- | --- | --- | --- |
| Incomplete budget prefixes | 3 | 387 / 384 | 3 | 381 | 0 |
| Completed ambiguous searches | 2 | 180 / 180 | 0 | 180 | 0 |

The three incomplete prefixes also rejected 260 raw nonpositive extents.
Their counters describe prefixes only. The two completed lists had 58 and 122
candidates. The new clipping defect is therefore covered by owned tests but
does not explain these five inputs. With 381 of 384 attempted budget-prefix
pairs already passing contact/scoring, further contact pruning is unlikely to
solve this measured bottleneck. Useful observed candidate generation and
explicit ambiguity review are the next engineering targets. The current review
UI has owned interaction evidence only; these counts do not establish that its
rounding, orientation and all-or-empty validation accept either real list.

Axis work totaled 8,362.5758 ms, preparation 3,884.6402 ms and in-process native
fallback 7,090.1032 ms. In-process timing is not comparable to the prior separate
worker timings and is not a controlled performance benchmark.

Assignment SHA-256:
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`.
Selected inventory SHA-256:
`03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`.
Source digest:
`4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1`.
The 51 reserved inputs stayed excluded. Sealed payload reads, OCR calls, truth
scoring, image writes, Apply and exports were all zero. No model, threshold or
private-answer selection occurred; only aggregate results are tracked.

The 18,380-byte report SHA-256 is
`568938e861aa87cfbe6f96d197d92eb66bc7550a60d733e63cdc80d1a93ca756`.
Runtime pins are recorded in `RealDevNativeWorkTracePins.cs`. Principal identities:

```text
App EXE  083d7b10c74057fda6ba0ccc036825ee52b284ea55b145db96659bfc4e837622
App DLL  956ca84ae81d40322872184f7be2960da67e0f63081a3ce33d615c31168c91b5
Axis DLL 74c694b12cfb4ebc6ac87ce651972360832e17177ab88fb408fd4d41c92dfd23
Probe    e3af5959e744589eca88dbfe2fd592b95ccb7dbfdd40c92fabc34ec516ab3d6c
Rust     cfc8e59a08b4aeaad84cc4742aa8a8c2f211a8f575f0d6bcfb6beb5f5a1880fd
OpenCV   c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31
```

## Integration, license and retention

Project code remains Apache-2.0. No dependency, weights, frozen schema,
production model approval or package was added. Full exact-head CI and package
verification are separate integration checks. Automatic calibration acceptance
remains incomplete at 7/12 geometry coverage, with no numeric result claim.

After this outcome and source are pushed, retire the completed raw reports,
stderr, run metadata and TRXs in `artifacts/axis-clipped-extent-check`,
`artifacts/native-work-trace-check` and `artifacts/axis-work-dev-check` after
their integration consumers finish. Keep originals, independent inputs,
reviewed runtime assets and the sole verified package. The existing diagnostic
Rust keeper remains an active input for subsequent source-owned review work.
