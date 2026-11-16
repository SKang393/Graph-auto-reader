<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Original-pixel support for affine tick review

## Defect and bounded repair

The fixed [owned interior-support pair](AFFINE-INTERIOR-SUPPORT-20261116.md)
demonstrated a connected original thin gray axis whose rectified span is
disconnected. A matched actual source gap remained disconnected under the same
clipped search. This is evidence for a local sampling repair, not a private
accuracy result. The earlier centered-control and private branches remain closed.

Add original-pixel support only to affine label review. Inside one Rust worker,
first run the existing derived span search. If an ordinary internal gap cannot
be bridged, and its existing work budget is still available, verify connectivity
between the original contributors of its two endpoints. Every contributing ink
sample must lie inside the unchanged derived search window after mapping.
Search only eight-connected original ink inside that window. Never extend its
radius, move geometry, change the `<200` criterion, accept missing endpoints or
overlong gaps, or replenish the request-wide budget. Both axes and both searches
share the existing work cap. Budget exhaustion still refuses the whole request.

Original support supplies no tick positions or scientific values. The existing
derived-pixel tick scanner and its gap exclusion ranges remain authoritative.
The normal proposal/global detection route and existing GRT1 modes are unchanged.
Manual-partial semantics retain only independently observed spans. Numeric
review, source identity, Apply/Undo and export safeguards remain mandatory.

## Compatibility and resource boundary

The additive private worker modes are `--measure-affine-ticks` and
`--measure-affine-ticks-manual-partial`. They accept GRA1: a 96-byte header with
the existing exact derived box, bounded Gray8 source dimensions/length, and six
little-endian doubles describing original-to-derived affine pixel centers.
Derived pixels then original pixels follow; extra/truncated data is rejected.
Each raster retains the existing dimension/pixel bounds. TICKS1 output and the
old modes remain compatible. No persisted project or public vision schema changes.

Validate finite orientation-preserving transforms and inverse box bounds/axis
lengths. A fixed **1e-7 source-pixel** allowance addresses floating-point
roundoff only in those input checks. It never changes samples, source support,
window containment or returned positions. No tolerance search is performed.

The C# client snapshots both rasters and the transform before scheduling, uses
one bounded worker with existing cancellation/output/deadline handling, and never
downgrades after protocol failure. The App binds a support receipt to the original
Gray8 hash, dimensions and transform in addition to the derived receipt and
existing source-bound frame. Legacy adapters continue derived-only behavior.
Review retains the compact binding; the extra original raster is transient.
No model, dependency, license or production approval changes.

## Fixed verification scope

Use owned controls only, with **zero private/sealed reads** and no candidate or
threshold selection. Require the connected/disconnected pair to stay separated,
old behavior to pass, both axis orientations and partial refusal to remain
correct, actual returned ticks to come from derived pixels, and malformed input,
source binding, cancellation and shared-budget controls to refuse correctly.
Run the Rust suite, relevant Axis client tests and App review tests after the
integrated source review. Preserve baseline failure counts and all new failures.
No package, native UI or representative accuracy claim follows from unit checks.

## Integrated checks and full failure accounting

The source batch is based on `148a03c5db7e05061bacca25be5dfc6bc7ab684a`.
The existing package remains **0.4.58**; no package was created for this batch.

| Check | Outcome | Command wall time |
| --- | --- | --- |
| Rust, offline locked, single test thread | 85 passed, zero failed/ignored | 6.348 s |
| Native release helper | Successful; worker hash below | 2.855 s |
| Full App suite | 1,342 passed, zero failed/skipped | 108.394 s |
| Initial Axis suite | 256 passed, two failed, zero skipped | 15.834 s including restore |
| Repaired cancellation fixture, targeted client class | 28 passed, two failed, zero skipped | 8.020 s |
| Final full Axis suite | 260 passed, zero failed/skipped | 11.635 s |
| Initial probe-only compilation | Six errors, zero warnings; no case ran | 6.697 s |
| Corrected probe-only compilation | Zero errors/warnings | 9.795 s |

The initial Axis failures were a fixture defect: its blocking child lacked the
required `GRAPHREADER_BLOCKING_READY` receipt path and exited before cancellation.
After readiness/PID synchronization was repaired, both affine cancellation tests
exposed a real client defect: the thrown exception carried the internal linked
deadline token. The shared handler now rethrows caller cancellation with the
caller token while internal expiry still returns `WORKER_TIMEOUT`. The final
suite checks live cancellation, exact token identity and child termination in
all four legacy/affine strict/partial modes. Neither failed attempt is discarded.

Probe compilation found five old two-element deconstructions of the now
three-element working-frame result and one missing `System.IO` import. The five
historical consumers now explicitly discard the source-support result. None of
their private or legacy diagnostics was executed. Full App verification preceded
the final cancellation-only repair; final-source hosted CI remains required.

Commands, from the active source worktree, with the existing local NuGet/cache
configuration and exact Rust/blocking-worker environment hooks:

```text
cargo test --manifest-path native/graphreader-engine/Cargo.toml --target-dir artifacts/native-engine --offline --locked -j 1 -- --test-threads=1
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --nologo
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --nologo
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Debug --no-restore --no-dependencies -m:1 -nr:false -p:BuildProjectReferences=false -p:UseSharedCompilation=false -p:NuGetAudit=false --nologo
GraphReader.CalibrationOcrProbe --owned-affine-original-support-worker-check <absolute-worker> <absolute-owned-baseline>
```

The two-case worker comparison ran exactly once at
`2026-11-16T09:46:25.0723913Z`, finishing at `09:46:25.6934339Z`.
The runner verified cached baseline status, case ordering, dimensions and both
source/derived hashes before each actual C# to Rust GRA1 request. Its loaded Axis
assembly matched the final tested client. No old worker or private case reran.

| Fixed control | Cached derived-only span | New original-supported worker | Worker-call time |
| --- | --- | --- | --- |
| Connected shallow shear | Disconnected internal span | Exact box, zero X/Y ticks | 53.6733 ms |
| Actual gap, same geometry | Disconnected internal span | `AXIS_BOX_NOT_OBSERVED`, no evidence | 34.4604 ms |

All **two declared/attempted/completed** cases passed, with **zero operational
failures, expectation failures or unattempted cases** and exactly **two native
calls**. Each of five stages completed twice. Overall diagnostic time was
**174.8317 ms**, wrapper wall time **0.5866748 s**, exit zero, no timeout and
empty stderr. These are observed durations, not a paired speedup benchmark.
The parent allowance was **0.4992 logical core** across 12 logical processors;
each sequential child retained its own existing cap and five-second deadline.

The connected original and derived hashes remained
`38ca949891e7aadd35d0ed11b5689725d42e013fbf0de9db4cd783147237cea6` /
`c851078d211adad30a4e50c72842412703994389c1068e2bb7037dd9ebbb4170`;
the disconnected pair remained
`6ce51f0ca8d82b48fe198709dbfe1a684b2d00be22d277d829dee7c58db15bfc` /
`fbb3bf4f193d53c79d0047d27f441369af075ae3bbaeded6085a6f286afdb376`.
All **27 protected input/Data hashes** were unchanged. Private/sealed reads,
OCR, numeric fit and image-file writes were all zero.

| Identity | SHA-256 |
| --- | --- |
| Native worker | `08538da44ff7f680f951c7cbe08c806801b74d9c44cff982bf507ae0f995edca` |
| Probe assembly | `47b1e9d96d641cdc383cb56b4c9e03d66ec2e0ff34146134fe01e6cd104c8996` |
| Final loaded Axis | `9b750546133943c454b28c1aba74f8d6239a5a86dcc4be05de707f73dbab422c` |
| Loaded Imaging | `f3371ca48ebb8fb0409594e6c971fe7449c08dc135de5c9ad6b578e46eba62cf` |
| Cached owned baseline | `8069c2c91e7c71d7d0607f586a976308904e62addfeb497ed3d21e1cf6a457e3` |
| Worker comparison report | `4db404d454b8b963a7d65f22fd0f0f5f9a03c6ab7abe7d259bbb56ce876c4b7b` |

The Imaging assembly identity differs from the baseline compilation, but exact
input and output pixel parity was required and passed. No historical DLL was
copied into the new process. An independent read-only review found no material
issue in the runner or integrated support path.

## Status and retention

Source/outcome commit `d26c6e42f2f974e31a07b8b877837b74e3992032` is verified on
`origin/main` and fast-forwarded into the main checkout. The two unrelated user
edits remain byte-identical. Exact-source [Windows CI 43843206008](https://github.com/SKang393/Graph-auto-reader/actions/runs/43843206008)
passed, job `133216769690`, with `full_product_validation=true` and
`scope_reason=full_changed_path`. Its log spans `09:49:21.5315579` to
`09:57:16.1741025 UTC`, about **7m55s**. All **3,258 .NET tests** and **85 Rust
tests** passed with zero failures. The **30 explicit .NET skips** comprise
Inference two, Integration seven, Markers three, OCR sixteen, PDF one and
SuperResolution one. App ran all **1,342** and Axis all **260** tests. Packaging
passed **68** controls, version policy **62**, portable retention **12**, ledger
generation **six** and localization self-tests **nine**. Both release jobs were
skipped. No workflow rerun or dispatch was used.

The bounded repair passes these owned checks. This pair supplies geometry and
contains no tick labels; it is **not automatic calibration or numeric accuracy
evidence**. Native desktop QA remains blocked by the tool's stale Escape state
despite recorded authorization. Production model, representative acceptance,
installer/clean-machine and final-product gates remain separate.

After the coherent source/outcome push and full CI review, guarded retirement
completed at **2026-11-16T10:00:55.0142508Z**: **528 files / 270,911,007 bytes**.
The eight exact scopes were `artifacts/affine-interior-support-check`,
`artifacts/affine-original-support-check`, the probe's `bin` and `obj`, and the
App/Axis test projects' `bin` and `obj`. Canonical paths, complete inventories,
file hashes, absence of tracked files/reparse points and inactive consumers were
checked before deletion; all targets were confirmed absent afterward.

All **27 protected files** and the sole verified **0.4.58 ZIP** remained
byte-identical. Originals, shared/source Data, current runtime assets, the
active correction checkpoint and the four native-QA images remain retained.
The first read-only consumer check could not access CIM in the sandbox; the
normal-host check succeeded with zero consumers before any deletion. No process
was stopped. The comparison command and raw-report hashes above are historical
records, not promises of retained inputs. Tiny retirement metadata remains only
until this completion record is pushed, then is also removed.
