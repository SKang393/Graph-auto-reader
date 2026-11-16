<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve observed ticks from one manually marked axis

## Problem and scope

The exact-box tick worker refused the entire result when either supplied axis
span was unreadable. Even after a user marked a tilted frame, this discarded
valid measured ticks on the other axis. The workspace already supports separate
X/Y evidence and review drafts; the producer prevented that partial information
from reaching it.

This change is restricted to current, user-defined affine geometry in the manual
preview workflow. Automatically proposed affine geometry, rectangular paths and
the ordinary worker mode keep their existing behavior. It does not repair
resampling, infer a missing axis, accept a disconnected span or complete
automatic real-graph calibration. The prior source-support investigation remains
closed; no private comparison or additional image inspection was performed.

## Implementation and acceptance bar

The explicit `--measure-ticks-manual-partial` mode uses the existing GRT1 request
and exact-box TICKS1 response. Each axis must independently pass its unchanged
full-span ink check before any ticks from that side are scanned. An unavailable
side is represented by the existing empty list. The exact origin must still be
observed. Both spans missing or any shared bridge-budget exhaustion refuse the
whole result. The bridge budget is not reset between axes. Fully observed spans
may legitimately contain no ticks; successful `- -` is not a fabricated axis or
numeric value. Mixed mode flags remain invalid.

`RunManualPartialAsync` shares the existing immutable request snapshot, bounded
transport, cancellation and exact-box parser. The App service exposes a separate
method; existing adapters default to strict measurement. Workspace routing uses
it only for `IsUserDefined && Provider == "manual"` inside the affine path after
its currentness check. The existing image hash, dimensions, box, receipt and
stale-result checks remain shared.

The acceptance bar is to retain the observed side's exact ticks, produce no tick
evidence for the failed side, preserve strict-mode results and require the same
explicit review before Apply. A partial literal-label draft still needs its
missing values supplied. Absence of measured ticks is not proof that no labels
are readable: the existing OCR fallback may independently return estimated
positions. This change does not upgrade those estimates to measured ticks.

No threshold, model, dependency, persisted project/vision schema, scientific
default or export behavior changes. Source retains Apache-2.0 notices.

## Validation

One isolated MSVC Rust test/build wave used offline, locked dependencies and one
compiler job:

```text
cargo test --manifest-path native/graphreader-engine/Cargo.toml --target-dir artifacts/manual-partial-ticks-check/native --offline --locked -j 1
cargo build --manifest-path native/graphreader-engine/Cargo.toml --target-dir artifacts/manual-partial-ticks-check/native --offline --locked -j 1
```

All **80 Rust tests passed**, zero failures. The five new tests cover both
one-axis directions, origin/both-span refusal, full-span Gray/RGB parity,
successful no-tick evidence, shared-cap failure and mixed flags. Existing strict
and diagnostic tests remain green. Test-command wall time was **11.3947952 s**
(5.06 s compilation, 0.19 s test execution); worker build wall time was
**5.8215777 s** (1.47 s compilation), both exit 0. The isolated Debug worker is
`0b099c8a5d53096d729bb0cb8d2fd65318d5c127fa0c36a3254a47e3b030d32e`.
The earlier diagnostic and source App workers remained unchanged during this
wave.

One sequential integrated wave set `GRAPHREADER_RUST_WORKER` to that isolated
worker and ran each complete project once:

```text
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true --logger "trx;LogFileName=app.trx" --results-directory artifacts/manual-partial-ticks-check
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj -c Debug -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true --logger "trx;LogFileName=axis.trx" --results-directory artifacts/manual-partial-ticks-check
```

App passed **1,322/1,322**, zero failures/skips, **56 s** reported test duration /
**141.4331873 s** command wall time. Axis passed **238**, failed **zero**, skipped
**one**, **3 s** reported tests / **12.6769837 s** command wall. The sole local
skip was the existing `CancellingActiveWorkerStopsTheRequest` control because
its blocking-worker fixture was not supplied; full CI supplies it. All new
controls ran, including actual PNG service/worker and Gray/RGB one-axis results.

The tests verify manual-only routing, missing-value/review gates, stale/cancelled
results and invalid receipts. Generated controls and fake literal-label responses
are not native desktop, real-image, OCR accuracy or complete digitization
acceptance. Ten protected files, including all four source Data files, four owned
native-QA images, the latest pointer and sole package, retained their sizes and
hashes before/after the wave. The ordinary App build refreshed its generated
assemblies and native dependency after the frozen investigation had closed.

| Evidence | SHA-256 |
| --- | --- |
| App TRX | `19213d2a42e2b3b0f383794d6f2c0004b22c689cd6cf5274b25f953f2fe33313` |
| Axis TRX | `64bdee85b16d1e78f0b393f194147a07b734288de4ae143736985239846fbd7b` |
| Managed command receipt | `684bc59256621ed8b72082097c145728309f64b4606d17eb4f9930aaddbe6d80` |

## Integration and retention

Local source validation passes; exact-head CI remains pending. The delivered package stays
**0.4.56**, and native QA remains blocked by the desktop helper's stale Escape
state despite renewed user authorization. No packaged build is required for
this source batch. Preserve current source Data, original inputs and that sole
package. After the coherent source/outcome push, CI and active consumers finish,
retire this batch's test outputs and isolated worker; concise evidence remains
here. No paid credits, sealed evaluation or reset redemption were used.
