<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Cached X-label component diagnosis

The [endpoint confirmation](NATIVE-RASTER-ENDPOINTS-20261115.md) recovered
measured ticks, but one development X row remained a compound recognition region.
This investigation measures component geometry without repeating recognition,
axis detection, value fitting or any private candidate comparison.

## Owned axis-ink hypothesis

A 160x100 black/white fixture used glyph rectangles `(30,75,3,8)`,
`(50,75,3,8)`, `(70,75,3,8)`, measured ticks `[31,51,71]`, and baseline Y65.
The full connected baseline/tick component had bounds `(20,65,61,19)`, exceeding
the unchanged 24-pixel component-width cap. Actual grouped and ungrouped detector
calls filtered it and retained the same three label boxes. A separate short
`(38,65,7,19)` broken-baseline fragment survived and merged all four components
into `(30,65,43,19)`. That merged row starts at the baseline, unlike the cached
development row below it. The fragment observation does not establish its cause.

The one diagnostic test passed in **84 ms** / **21.0818097 seconds** build-plus-run.
Temporary logs and the fragment-only path were then removed. A durable full-axis
regression verifies unchanged label boxes and input pixels. The focused X/Y
grouping suite passed **29/29**, zero failures/skips, in **243 ms** test time /
**9.4024568 seconds** wall. No production OCR behavior changed in this batch.

## Bounded component measurement

`RealDevCachedXRowProperties` binds the exact endpoint report, schema, runtime
and fixed first-12 source digest. It reads only the cached target's role and
geometry fields, never its recognized text or numeric values. It reuses the
locked image-only XML extraction, PNG validation and one crop mapping, then the
production ungrouped component detector with unchanged thresholds. It never
constructs manual calibration or point truth.

Output is sensitive local component geometry. Cut coverage counts are explicitly
row-overlap approximations, not an authoritative merge-decision trace. Properties
are withheld unless every fixed source verifies. The implementation has separate
source, crop and component attempt/completion/failure/unattempted accounting.

One probe-only build passed with zero warnings/errors in **7.13 seconds** MSBuild
/ **7.7446695 seconds** wall. All 20 frozen runtime libraries were verified and
staged. The owned self-test passed in **0.3056161 seconds**, covering report
tampering, digest-gated withholding, component/cut geometry, full-baseline
filtering and cancellation. The missing-arguments check returned expected exit 1,
sanitized `CACHED_X_ROW_PROPERTIES_FAILED`, in **0.1956377 seconds**. Both used
no private data.

The single authorized property run completed from
`2026-11-15T18:53:37.0774822Z` to `2026-11-15T18:53:38.7262590Z`, exit 0,
empty stderr, **1.6485372 seconds** wall. All **12/12 sources**, **one crop** and
**one component extraction** completed, with zero failed or unattempted stages.
The assignment, inventory and combined source digest matched the frozen endpoint
trace. Independent and sealed payload reads, axis calls, OCR recognition calls,
truth scoring and image writes were zero.

Aggregate properties: **120** total detected components, **38** overlapping the
target row, **zero** overlapping components extending to/above the baseline.
Of **19** midpoint cuts, **10** cross glyph components; no measured tick column
is covered by those row components. This locates an alignment mismatch between
the current tick-centered grouping assumption and observed inter-tick glyph
placement. It does not supply numeric labels or justify snapping a label to an
edge tick. A generic owned interval-label reproduction is the next consumer.

## Reproduction, identity and retention

```text
dotnet test tests/GraphReader.Ocr.Tests/GraphReader.Ocr.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter "FullyQualifiedName~AxisInkGroupingReproductionTests|FullyQualifiedName~AxisLabelGroupingReproductionTests|FullyQualifiedName~MeasuredYTickGroupingTests"
dotnet <probe.dll> --real-dev-cached-x-row-properties-self-test
dotnet <probe.dll> --real-dev-cached-x-row-properties
dotnet <probe.dll> --real-dev-cached-x-row-properties <cached-endpoint-report> <corpus-root> <frozen-App-exe> <reviewed-assets> <frozen-Rust-worker> --explicit-opt-in
```

Source base: `308d7a823afbec316a73f4e2e16b528f27ca9c2f`. Frozen runtime pins
remain those of the endpoint trace; the separate later owned solver comparison
replaced only its probe-local Axis DLL after this diagnosis finished.

| Item | SHA-256 |
| --- | --- |
| Probe DLL | `8f34d8d7d38cdd805ec79d325a62375b243f98fb0d55539f71bcb4f2c8623937` |
| Diagnostic source | `f037552d7ec363fe514019309f829f54d7391b6c41b2827cced37b09cb6a0aae` |
| Original owned diagnostic TRX | `11af87889f7002e975fe3a7bc4b6145d6b23e2f2cf4de3fa6a8f262bd6a41aa4` |
| Focused grouping TRX | `006f677768c0cc06bdb10f4f94fedb4adfa09f2385a2eaaa266754eb7b3bc0c2` |
| Owned property self-test | `04fc4d2bdd86f237c7bde54e198681b27814b0d04538304aefd069afab9f69d5` |
| Property report, 6,036 bytes | `9ed9d8d816e23872ac2fbd08b9470436b7d62e7d6a535db01cb806db2fa7f4f8` |

Source remains Apache-2.0; no new dependency, model, license, public contract,
packaged build or production approval. Diagnostic accounting passes; automatic
calibration remains incomplete. Full Windows CI
[43750130103](https://github.com/SKang393/Graph-auto-reader/actions/runs/43750130103)
passed on `11196b00d4afa18c208700ddae456d4236c74b40`: 13 .NET suites,
3,043 passed / 30 skipped / zero failed, 72 Rust tests, and applicable
policy/packaging checks including 68 packaging verification tests. Release jobs
were skipped. This does not establish a native UI or production calibration pass.

Retain the local property report only until the owned interval-layout
reproduction consumes this diagnosis and the aggregate outcome is pushed. Keep
the endpoint trace as the baseline for a meaningful changed-stage comparison.
Retire completed self-test/diagnostic TRXs after push and integration consumers.
Originals and independent inputs remain intact; no private geometry is committed.
