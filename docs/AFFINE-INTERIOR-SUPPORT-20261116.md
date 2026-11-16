<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Owned interior support for thin affine axes

## Failure and fixed scope

The [earlier thin-gray controls](AFFINE-STROKE-SAMPLING-20261116.md) establish
lost usable raster support after rectification. Their two centered Bresenham
controls cannot establish an admissible original-pixel bridge: some endpoint
contributors lie outside the existing clipped window. That branch is closed;
neither its private inputs nor its retired baseline is reopened.

This check addresses that specific validation gap with **two new owned
controls**, fixed before execution. It changes no production algorithm, image
threshold, search radius or clipping. Responsible subsystems are affine raster
sampling and the supplied-frame tick-span check. No statistical accuracy bar,
model choice or acceptance threshold is introduced. Private and sealed budgets
are **zero**; there is one invocation of this fixed pair and no parameter sweep.

Both use a 320 by 240 white Gray8 canvas, origin `(55,185)`, X endpoint
`(265,192)` and Y endpoint `(55,45)`. X stroke pixels have `x=55..265` and
`y=185+floor((x-55)/30)`, at gray 192. The Y stroke is `x=55,y=45..185`.
This is a shallow affine shear, not an orthogonal rotation. The matched negative
removes X steps 15 through 23 inclusive. The floor construction keeps the
drawn X stroke on the interior side of the supplied plot boundary. This geometry
is supplied to the diagnostic; it is not automatically detected.

Independently check whole-source positive X connectivity and negative
disconnection. Run the existing rectifier, X-span replay and original-support
observer with the replay window unchanged. Retain both results, including any
failed expectation or unattempted stage. Expected evidence is a derived refusal
with all endpoint ink contributors inside the same window, a completed connected
source proof for the positive, and no connected proof for the negative. The
negative may correctly retain uncertainty after a clipped-boundary encounter.
If the fixed pair is ineligible, report that outcome without modifying it to
force success.

The exact command is the probe's `--owned-affine-interior-support-check` mode.
Its half-core CPU job, 30-second cancellation and 35-second watchdog remain in
force. It writes no images and runs no native worker, model, OCR, fit, selection,
Apply or export. Original/derived hashes, loaded Imaging identity, stage counts,
elapsed time and cumulative allocation are reported. No dependencies, weights,
licenses or public contracts change. Source retains Apache-2.0 notices.

## Outcome

Independent source review found no material issue after removing an incorrect
child-breakaway preflight requirement from this no-child mode, before execution.
The first sandboxed probe restore exited 1 after **6.374 seconds**, emitting
only `Determining projects to restore...`; it did not execute controls. The same
probe-only restore through the normal host succeeded in **3.350 seconds**.

The probe-only build used `dotnet build
tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj
-c Debug --no-restore --no-dependencies -m:1 -nr:false
-p:BuildProjectReferences=false -p:UseSharedCompilation=false -p:NuGetAudit=false`.
It passed with zero warnings/errors in **8.992 seconds**. No App/reference or
package build ran.

The single invocation ran from **2026-11-16T09:10:10.2298282Z** to
**09:10:10.8678616Z**, exit **0**, empty stderr, **0.570 seconds** wrapper /
**91.940 ms** diagnostic time. Both declared controls and all six stages
completed, with **zero operational failures, expectation failures or unattempted
controls**. Both source and derived hashes were unchanged.

| Fixed control | Source X connected | Derived replay | Original support in unchanged window |
| --- | --- | --- | --- |
| Connected | yes | disconnected; gap 26, work 28 | connected, not uncertain; 79 visits / 552 projections |
| Nine-step gap | no | disconnected; gap 26, work 28 | disconnected, uncertain at clipped boundary; 39 visits / 272 projections |

Both had dark origin/X endpoints (gray 192), one ink contributor at each gap
endpoint, zero contributors outside the window, and complete source searches.
The negative's boundary uncertainty was retained. Derived dimensions were
320 by 251. This pair demonstrates a source-supported raster gap without
relaxing the existing clipping or mistaking the matched real gap for continuity.
It does not establish semantic axis identity, measured ticks or calibration.

The verified parent allowance was **0.4992 logical core** (rate 416 across
12 logical processors). Source/derived Gray8 totals were **153,600 / 160,640
bytes**; cumulative allocations were **1,160,632 bytes**, not peak memory.
All **27 protected input/Data hashes** were unchanged. Private, sealed, worker,
OCR and fit counts were zero.

| Identity | SHA-256 |
| --- | --- |
| Probe assembly | `42b5ee928b05f6c422802b9025086d865c7888278e5682020a17007b8f71e79e` |
| Loaded Imaging | `75e4646dc5b55d156f7bdc4fdaa104cffdb82ad7f4c38d135e438629b6a573b5` |
| Two-control report | `8069c2c91e7c71d7d0607f586a976308904e62addfeb497ed3d21e1cf6a457e3` |
| Connected source | `38ca949891e7aadd35d0ed11b5689725d42e013fbf0de9db4cd783147237cea6` |
| Disconnected source | `6ce51f0ca8d82b48fe198709dbfe1a684b2d00be22d277d829dee7c58db15bfc` |

The owned feasibility check passes. The bounded Rust source-support fallback
subsequently passed the exact paired controls; see its
[integrated outcome](AFFINE-ORIGINAL-SUPPORT-20261116.md). Native UI acceptance
and automatic-accuracy evidence remain incomplete. Preserve this compact report
and probe through source integration/CI review, then push its
outcome and retire `artifacts/affine-interior-support-check` and inactive probe
compiler outputs. No private follow-up or parameter sweep is authorized by this
result.
