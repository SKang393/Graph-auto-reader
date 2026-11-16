<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Bounded oversleep credit for affine rectification

## Problem and implementation

The [owned stroke observation](AFFINE-STROKE-SAMPLING-20261116.md) recorded
**1,275.2584 ms** of pacing waits within **1,517.1804 ms** of rectification across
thirteen small rasters. Source review found that every active wall-time slice of
at least two milliseconds requested an equal delay, then discarded any excess
actual wait. Scheduling delays during computation also count as active elapsed
time. These are source/accounting observations; the cause of any particular
oversleep, including timer resolution, was not established.

`AffinePacingBalance` now records active wall-time debt and credits actual elapsed
wait. The loop requests only positive debt, while retaining the two-millisecond
accounting check. Credit is capped at **eight milliseconds**, four existing
slices, so a long suspension cannot fund an unbounded subsequent burst. Per-row
and post-wait cancellation, cancellable delay, original buffers, sampling,
coordinates and transforms remain unchanged. The helper has test-assembly-only
internal access. No new dependency or configuration option is introduced.

This is cooperative **wall-time** pacing, not a CPU-time measurement or a CPU
hard cap. It still conservatively charges preemption as active time. Any
enclosing Windows CPU job is unchanged. The separate source-mask experiment
failed and is not part of this production change.

## Validation

One normal-host command restored and ran the complete Imaging suite:

```text
dotnet test tests/GraphReader.Imaging.Tests/GraphReader.Imaging.Tests.csproj -c Debug -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true --logger "trx;LogFileName=imaging.trx" --results-directory artifacts/affine-pacing-check
```

**35/35 tests passed**, no failures or skips, **504 ms** test / **16.1730939 s**
wall. Four new deterministic accounting tests cover equal duty, retained wait
credit, the credit cap, nonnegative requests and invalid durations. Existing
raster/transform/input-preservation and pre-/mid-work cancellation tests also
passed. No App, probe, native worker or solution build was performed. The source
App and probe Imaging assemblies retained hash
`27d5044e20f2baaef3ee872d882331cd6d97c555add091c076ce8ae703c05b1d`.
Independent source review found no material accounting or cancellation issue.

The candidate Imaging DLL alone was then temporarily staged in the existing
owned probe. Its original DLL was restored in `finally` and reverified. The
source App runtime remained unchanged. One
`dotnet GraphReader.CalibrationOcrProbe.dll --owned-affine-stroke-sampling-check`
invocation compared the same thirteen cases with the cached current-kernel
report; the baseline was not rerun. All **13/13 completed**, zero failed or
unattempted, exit 0 and empty stderr. Every non-timing case field matched exactly,
including original/derived pixel hashes, dimensions, coordinate offsets,
connectivity, profiles and refusal counts. The known thin-stroke failures remain
visible; faster scheduling did not change their outcomes.

| Measured scope | Cached baseline | One candidate observation |
| --- | ---: | ---: |
| Rectification | 1,517.1804 ms | 494.6042 ms |
| Recorded pacing waits | 1,275.2584 ms | 249.4882 ms |
| Whole owned batch, inside probe | 1.5434725 s | 0.5217323 s |

Both observations used the same **0.4992 logical-core** job allowance. This is
one candidate observation versus one retained baseline sample, not a repeated
benchmark, general application speed claim or measured CPU duty ratio. The
candidate wrapper, including temporary DLL staging/execution/restoration, took
**1.286972 s**. The receipt spans **2026-11-16T02:42:17.5628576Z** to
**02:42:19.1417876Z**, including its later result checks. Its stdout summary
projection initially displayed null fields because PowerShell selected properties
from an ordered dictionary; the persisted typed JSON was complete and was read
back to verify every result. No probe rerun followed that display issue.

| Identity | SHA-256 |
| --- | --- |
| Candidate Imaging DLL | `3aea070df218db14cdb65f15a64eb2f1732cdbb6e7b22d04adc23d7977831f1c` |
| Imaging TRX | `959f71abfbacb52679d3834346da19b5397071b213d7907097a59a797a93830d` |
| Unchanged probe DLL | `8144119b7400e2ccf2f716378d4396b8c83e5b3653c5d5e63df3e806a9c2b345` |
| Candidate owned report | `8d800fc32e30be8d72e5eec0e249a6b3c4ddbd59b895128147483bbd618f5957` |
| Parity/timing receipt | `4dc447e250f5b31e8828dfca8766a6e1c59700bf468ed58d1313202f7ba0f4d8` |

## Integration and limits

Source and focused validation pass. Exact-head full Windows CI
[43794805502](https://github.com/SKang393/Graph-auto-reader/actions/runs/43794805502)
on `401afb240c59148a08b7180485c32896a5ca9f16` passed in **10 min 50 s**:
**3,184 .NET tests**, **30 expected skips**, **75 Rust tests**, **68 packaging
checks** and **9 localization checks**, zero failures. Imaging passed all 35
tests, App all 1,310, and the existing seven-control affine-span self-test passed
without private reads or worker calls. Both release jobs were skipped.
The sole delivered package remains **0.4.56**. This is a source change, not a new
packaged build or native UI acceptance. No private or sealed input, OCR/model,
training, worker call, image-file output, Apply or export occurred in the pacing
comparison. No dependency, license or frozen contract changed; source retains
Apache-2.0 notices.

After the source/outcome push and full-CI review, Imaging test `bin`/`obj`, the
TRX, owned reports and temporary parity runner were retired at
**2026-11-16T03:23:50.0178030Z**: **191 files / 8,386,909 bytes** across exactly
three directories. The manifest bound every file's size/hash; absolute paths,
reparse ancestry/descendants, untracked status and process references were
checked before removal. The process inspection is point-in-time evidence, not
a universal open-handle guarantee. Ten protected files retained their hashes
before and after, including all four source Data files. The actively rebuilding
probe was excluded from its old snapshot and never included in deletion scope.

Manifest SHA-256:
`448db19ccb44acc324643931d1539463df9fbd2fdf9e9b0a60db8d8d3adbbd4d`;
journal SHA-256:
`6e7fe7712c8a1b2189ee753a83acabe4ed5db94a5f67aa81f26b2c344d62d0bf`.
The candidate Imaging compiler output remains an active build input. All source
App/probe runtimes, original data, selected-frame diagnosis inputs and the single
delivered package remain preserved. The table's TRX/report hashes now identify
retired historical outputs, whose concise result is retained in this document.
