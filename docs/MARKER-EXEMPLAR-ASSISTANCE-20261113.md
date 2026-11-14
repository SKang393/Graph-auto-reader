<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Marker example assistance

## Problem and scope

Connected-component extraction joins filled markers to their connecting lines.
The existing production template selector deliberately rejects such components.
A manually chosen example avoids that seed-selection problem without loading the
rejected V27 detector or adding another training run. This is review assistance,
not automatic marker-stage approval or a change to the 95% evidence policy.

The user selects a placed point in its series, adjusts a square example from
5 to 65 original pixels, and requests similar positions. The example outline
and candidate positions are transient. Each accepted position goes through the
existing manual AddPoint, undo and autosave path; no candidate is inserted by
the matcher. Existing move-point editing remains available after acceptance.

## Processing and bounds

The private App-to-App protocol binds source and panel hashes, immutable original
image identity, dimensions, confirmed calibration, selected point/series,
coordinates and crop size. It does not change a frozen persisted schema.
Workspace state is rechecked after hashing, after the worker returns and before
acceptance. Requesting matches does not mutate calibration or project points.

The search rectangle follows the current manual calibration's independent X/Y
transforms. It does not require unused anchor coordinates to be identical.
The square crop is not clipped to fit. Match locations preserve the selected
point's exact offset inside that integer crop, including fractional coordinates.
The matcher does not claim to recover the geometric center of a symbol.

One hidden child handles one request, using the existing Windows job cap of at
most half one logical core, BelowNormal priority and one OpenCV thread. Matching
uses the existing normalized-correlation operation and 0.9 similarity threshold,
at one scale inside the calibrated rectangle. The production helper's defaults
are unchanged. Similarity is not a calibrated probability. This uses the
existing native OpenCV kernel; Rust continues to provide the axis/tick stage.
There is no Python, new dependency, model load, image cache or cloud request in
this marker path.

Inputs are bounded to 4,194,304 pixels, 4,096 pixels per dimension and 32 MiB of
encoded data. At most 128 candidates are returned; overflow is a visible
failure. Constant crops are rejected. Matching has a five-second hard watchdog,
the child has twelve seconds and the caller fifteen seconds including startup.
Cancellation targets only the owned child; cleanup waits boundedly for exit.

## Verification

The complete App suite passed **911/911**, no skips, in 37 seconds (40.067 s
command wall time). Twelve added tests cover original bytes and stale bindings,
connected glyphs, fractional seed offsets, bounds, cancellation, wire validation,
Production refusal, preview-only behavior, explicit Add, remaining proposals,
duplicate rejection and undo/redo. Find switches from point-editing mode back to
Select; it remains unavailable during unfinished calibration.

Final App/probe build passed with zero warnings/errors in 28.167 s; test build
passed in 13.445 s. Initial builds found one nullable reference and missing
namespace imports in the VM partial and test file. These were repaired before
the passing suite. No failing runtime test was bypassed.

Actual child checks used reviewed OpenCV SHA-256
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`.
Each in-memory owned image contains three filled circles joined by a one-pixel
line and a different hollow-square distractor. One circle is the selected seed;
the other two are expected matches. No proposal is applied to a project.

| Control | First request | Native matching | Cancellation and child exit | Next request | Result |
| --- | ---: | ---: | ---: | ---: | --- |
| 128 x 80 | 554.558 ms | 37.201 ms | 91.183 ms | 537.856 ms | 2 matches, both zero-pixel error |
| 1280 x 800 | 649.433 ms | 172.487 ms | 88.833 ms | 544.795 ms | 2 matches, both zero-pixel error |

The larger control keeps a three-pixel marker radius and 13 x 13 example,
increasing search area rather than glyph size. Its restart matching took
71.742 ms. Both checks reported one OpenCV thread and a verified 416/10000 job
rate on 12 logical processors, equivalent to 0.4992 core. The cancellation check
first observed the actual child PID, then required task cancellation and that
child's exit within two seconds. No owned process remained. Whole probe wall
times were 1.621 s and 1.657 s including cancellation/restart. These controls
establish execution and lifecycle only, not representative accuracy or tail latency.

Identities before version-only preparation of 0.4.40:

- App DLL: `9c77beaf1ebd42484e96a5c7b8f8729b2c644f0dd376924ae9e17552de743352`.
- Initial/final Probe DLL: `757054290f67fe76d17d5e930f3a842317fdd8ff7abafb8e10065ed4c0b13b27` /
  `9b8262a70735d0f530e13515a63cb701eae8f0f997790b621bd6decbe589fb4b`.
- Small input/result: `e33ee9eaebeb91fdf4b9f1050e40d66947f89acd091a409d077832c30e5e247c` /
  `b2cd5714e0fa50464beb64a825dcc4c601651caf371ee8b42422b5acd499ebbf`.
- Larger input/result: `ec3477bd99e4078fb2fa81375d543691270f046c6c0a6ca003715dd741de8042` /
  `f785a53ea03e045fa1ef70d05d7e930db29b046f458d9c7cf6ba07817431fb37`.
- App TRX: `9dfebc86d4349d0cc19cd45e568cf7c06f6b53996ce4fbb4a7d52129d4ca13c6`.

Build commands used Release, `--no-restore -m:1 -nr:false -p:NuGetAudit=false
-p:UseSharedCompilation=false`, existing checkout-local .NET/NuGet caches and
`OutDir=artifacts/hardware-final-build/`. First build
`tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj`,
then `tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj` with
`BuildProjectReferences=false`. Run `dotnet test` on that App test project with
the same output directory, `--no-build --no-restore`, and TRX output under
`artifacts/marker-exemplar-check`. The actual diagnostic command is:

```powershell
artifacts/hardware-final-build/GraphReader.CalibrationOcrProbe.exe --marker-exemplar-check E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\hardware-final-build\GraphReader.App.exe
```

Only the probe was rebuilt for the larger control (5.933 s). Preflight detected
that this copied the ordinary NuGet OpenCV DLL and stopped before execution.
Restaging the reviewed DLL and checking the unchanged App hash preceded the
successful run. App tests were not repeated for the diagnostic-only fixture
change. Native interaction remains waiting under REV-011 after the user stopped
Computer Use. The [0.4.40 preview](HOSTED-INTERNAL-PREVIEW-20261113.md#hosted-delivery-0440)
has now been uploaded, downloaded and verified. Its actual packaged matcher
returned the two expected larger-control centers in 571.506 ms, cancelled in
96.896 ms and restarted in 556.601 ms. No installer, public release, model
approval or complete automatic pipeline is claimed.

## Limitations and retention

Changes in marker size, connecting-line angle, background, overlap or damage
can reduce similarity or create false matches. The user must review candidates.
Matching is CPU-only; the separately measured device profile applies to OCR.
No private corpus, model training, sealed split or threshold selection is used.

After source/outcome commit `19edf74d927ec6fad839c31031292c885facf02f` reached
main, `artifacts/marker-exemplar-check` was retired: 19 files, 1,298,278 bytes.
The probe's owned pixels exist only in memory. The isolated compiler output
is an active input to packaged-worker verification and the next batch-edit
integration check. Retire its disposable results after those consumers finish.
The user's earlier QA App and its dependencies remain protected.
