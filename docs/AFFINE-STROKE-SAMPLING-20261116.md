<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Owned affine stroke sampling observation

## Failure and fixed scope

The [selected development span audit](AXIS-BOX-REFUSAL-DIAGNOSTIC-20261116.md)
reproduced a three-pixel gap. Relaxing only perpendicular clipping at the same
radius did not reconnect it. That excludes clipping alone as its cause; it does
not establish whether source ink was disconnected or rectification lost support.

`AffineRasterRectifier` bilinearly samples Gray8 before the supplied-box worker
applies its existing strict `<200` ink criterion. The
[eleven earlier black-stroke controls](AFFINE-TICK-OBSERVATION-CONTROLS-20261115.md)
already exercised rectification and the actual worker. They are not rerun.
They did not check gray strokes or independently establish source connectivity.
The new check isolates the current sampling kernel, not geometry detection,
tick measurement, PNG conversion, OCR or calibration accuracy.

Fix **thirteen** in-memory controls before execution: identity with integer
phase and positive/negative two-degree tilt with half-pixel phase, each at
digital widths one and three and gray levels **128 / 192**. Those levels come
from existing strong-gray worker controls. One additional thin gray-128
positive-tilt control has a deliberate nine-step gap. No cutoff, angle, phase
or radius sweep is performed. No private or sealed input is used.

The new renderer draws an eight-connected Bresenham centerline between rounded
original endpoints; width three adds a one-pixel Chebyshev dilation. Independent
component traversal must confirm each positive source's X/Y connectivity and
the negative's disconnected X. This differs from the earlier geometric-distance
renderer; no pixel parity between those fixture families is claimed.

Each case runs the unchanged rectifier once. Record source/derived hashes,
original preservation, identity-pixel parity, source/frame endpoint offsets,
exact-frame and mapped-planted endpoint connectivity, anchor darkness and
axis-row profiles. The existing X-span replay is attempted only after its origin
predicate passes. Y component/profile observations are not a native Y replay.
Positive connectivity loss is a reported observation, not an operational test
failure. All cases and failures remain in the denominator.

The CLI is `--owned-affine-stroke-sampling-check`. It uses the existing Windows
CPU job, 30-second cancellation and a 35-second watchdog. The frozen Imaging
assembly is `27d5044e20f2baaef3ee872d882331cd6d97c555add091c076ce8ae703c05b1d`.
There is no App/runtime rebuild, worker, model, OCR, fit, image-file write,
Apply, export, dependency or license change. Source retains Apache-2.0 notices.

## Current-kernel outcome

The first probe-only build exited 1 in **12.5699388 s**, with three CS0246 errors
for `InvalidDataException` at lines 48, 58 and 69 of the new probe file. Its
missing `System.IO` import was added without changing the fixtures or predicate.
One repair build passed with zero warnings/errors in **9.6817997 s**. No control
ran before that repair. Both attempts used:

```text
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Debug --no-restore -m:1 -nr:false -p:BuildProjectReferences=false -p:UseSharedCompilation=false -p:NuGetAudit=false
```

The single `dotnet tools/GraphReader.CalibrationOcrProbe/bin/Debug/net10.0-windows/win-x64/GraphReader.CalibrationOcrProbe.dll --owned-affine-stroke-sampling-check`
invocation ran from **2026-11-16T02:13:03.2674610Z** to **02:13:05.2309366Z**:
exit 0, empty stderr, **1.956925 s** wrapper / **1.5434725 s** core wall time.
The loaded Imaging assembly matched the frozen hash above. All **13/13** cases
completed, with zero failures, cancellations, invalid source fixtures or
unattempted cases. Every original hash remained unchanged and all four identity
controls preserved their pixels exactly.

| Fixed group | Cases | Observed destination behavior |
| --- | --- | --- |
| Identity, both widths and gray levels | 4 | Anchors and component connectivity preserved |
| Tilted width three, both gray levels/signs | 4 | Anchors and component connectivity preserved |
| Tilted width one, gray 128 | 2 | Exact origin/X endpoint remain ink and whole-raster X connection survives; exact Y endpoint whitens; clipped X-span replay refuses |
| Tilted width one, gray 192 | 2 | All three exact-frame anchors whiten past the fixed ink criterion; X-span replay is unattempted |
| Deliberate thin X gap | 1 | Source and destination X remain disconnected |

Thus four positive controls fail at least one required endpoint/component query.
This is **not** proof that the whole derived stroke fragmented: whitened
endpoints alone make such queries fail. The negative-tilt gray-128 mapped-X query
also fails, but this report did not separately log mapped-endpoint darkness, so
that failure is not classified as an isolated component. Exact and mapped
endpoint offsets are retained in the local report. The current-kernel result
demonstrates loss of required source-ink evidence in owned thin gray fixtures;
it does not establish the cause of the selected real graph's refusal.

The actual job rate was **416 on 12 logical processors**, or **0.4992 logical
core**. Total source gray bytes were **998,400**, derived bytes **1,053,372** and
cumulative parent allocations **8,269,848**, not peak/resident memory. No private,
worker, OCR, model, fit or image-file operations occurred.

| Identity | SHA-256 |
| --- | --- |
| Probe assembly | `8599b37910c9ac46fa7af31dab20bdc14649a514d5e1f58195300325cb209bad` |
| Thirteen-case report | `6aa95bee611c590342a33106042eff085c20fe50018b5939c8e00b5fafdfb62f` |

## One fixed source-ink transport hypothesis

Before any production edit, test one defined preprocessing change on these same
thirteen owned fixtures: apply the existing source `<200` predicate to obtain
binary observed ink, then pass that mask through the unchanged rectifier for
tick measurement. OCR would retain its original grayscale image. This tests the
order of source classification and interpolation; no cutoff is changed.

Keep the current-kernel report as the unchanged baseline. Verify all original
fixture hashes against it; record the binary-input hash separately. Identity
parity for the prototype refers to its binary input, not the original grayscale
pixels. Run the actual frozen tick service once per prototype case and retain
every response: positives contain no ticks, and the deliberately disconnected
case must remain visibly refused. There is no baseline rerun, variant sweep,
private invocation, release or automatic calibration acceptance in this step.

## Source-mask prototype outcome: FAIL

The separate `--owned-affine-stroke-mask-check <absolute-frozen-worker> <absolute-cached-report>`
route pinned the unchanged worker
`922bbc579e7cb593527c3c0a9bb9bb5274f6b164625bb5f2715b1aa49bb68f84`,
the current-kernel report above and the frozen Imaging assembly. It verified
each source hash, angle, phase, digital width, gray level and derived dimensions.
Post-worker original-gray, binary-input and encoded-input hashes participate in
the result, as do the returned image hash, dimensions and exact box. Pointwise
mask identity is not presented as proof of fractional derived-origin behavior.

One candidate probe-only build passed with zero warnings/errors in
**10.2361709 s**. The single invocation ran **2026-11-16T02:33:31.5566655Z** to
**02:33:34.2640121Z**, **2.7021349 s** wrapper / **2.3221033 s** core, empty stderr.
It returned intentional **exit 1** for **four expectation failures**. This is a
failed hypothesis, not a void plumbing run. All **13 native calls completed**,
zero exceptions, timeouts or unattempted cases. The normal response does not
localize an axis refusal's predicate; no extra diagnostic worker run followed.

All four tilted thin cases still returned `AXIS_BOX_NOT_OBSERVED`, although their
three mask-derived anchor pixels were dark. The eight other positive controls
returned bound evidence with zero X/Y ticks. The disconnected negative remained
refused. There were **zero unexpected-tick cases**. Every original, binary and
encoded input stayed unchanged; the four identity outputs matched their binary
inputs. Thirteen grayscale cases produce **seven distinct binary inputs**; the
four failed rows represent **two distinct thin-tilt masks**, not four independent
binary rasters. The original grayscale baseline was neither rerun nor replaced.

Preparation/classification/binding took **16.8087 ms**, rectification plus encoding
**1,439.3786 ms** and native calls **862.0846 ms**. The former includes
**1,131.6802 ms** of recorded pacing waits. Source and binary inputs totaled
**998,400 bytes each**, derived pixels **1,053,372 bytes**, and cumulative parent
allocation **11,478,960 bytes**, not peak memory. Parent allowance remained
**0.4992 logical core**, with independent unchanged worker budgets, five-second
child deadlines and 30/35-second overall cancellation/watchdog bounds.

| Identity | SHA-256 |
| --- | --- |
| Candidate probe | `8144119b7400e2ccf2f716378d4396b8c83e5b3653c5d5e63df3e806a9c2b345` |
| Thirteen-case mask report | `621002242832abb3395ceb2235c4ec83d6697d61042d416664d7b5f5fb077a6b` |
| Compact outcome | `3c497dcad5962060974f29929877e84cc8e0c180106719006f4e3a0bb021d25f` |

The mask prototype is closed **FAIL** and is not integrated into the App. There
is no retuning, private follow-up, OCR alteration or production sampling change.
The remaining native refusal is unlocalized, and the continuous-frame offsets
mean these controls do not prove a general loss of source component topology.
The current-kernel report remains an active unchanged comparator for the
[independent pacing repair](AFFINE-RECTIFIER-PACING-20261116.md). After that
comparison and source/outcome integration finish, raw stroke reports, build/run
logs, staging receipts and prototype output can be retired. No image file,
model, dependency, schema, packaged build or license changed.

The pacing comparison and source-support diagnosis are now closed. Their raw
stroke/prototype outputs were retired with the diagnosis tree on 2026-11-16.
The sole baseline report, SHA-256
`6aa95bee611c590342a33106042eff085c20fe50018b5939c8e00b5fafdfb62f`,
was moved to `artifacts/affine-source-bridge-check/stroke-baseline.json` for one
named next consumer: check original support in the unchanged clipped window
for the existing positive/negative two-degree, thin gray-128 owned controls.
That new property does not reopen the failed source-mask prototype, authorize a
parameter sweep or allow private evaluation. Remove this compact baseline after
the two-control audit's result is pushed and its consumer finishes. The
[retirement record](AXIS-BOX-REFUSAL-DIAGNOSTIC-20261116.md) retains the inventory
and actual-action journal identities.
