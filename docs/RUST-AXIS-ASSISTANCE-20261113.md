<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Rust axis assistance and hardware selection, 2026-11-13

## Delivered behavior

ManualPreview now offers **Suggest axes (review only)** without requiring the
full marker/OCR/legend/phase pipeline. A separate Rust process proposes axis
geometry and optional first/last outward x-tick positions. It does not supply
numeric labels. The WPF review card shows original-pixel anchors, three blank
numeric fields, and explicit review confirmation. Apply is unavailable until
the labels and review are valid. Calibrate can correct the proposed anchors.
Existing calibration remains unchanged until Apply.

Proposals are bound to the source image object, hash, panel geometry and current
calibration. Tab changes, cancellation and stale input invalidate them. Review
found a dismissal defect: two correction clicks followed by Dismiss left the
third click able to apply ordinary calibration. Clearing an active proposal now
clears pending clicks and exits calibration mode. The regression verifies that
existing calibration, points, audit events and dirty state remain unchanged,
and ordinary manual calibration still works afterward.

The worker uses bounded binary input/output, a 60-second deadline and process
tree cancellation. It writes no image or result cache. Below-normal priority and
a Windows Job Object limit apply approximately half a logical core of aggregate
CPU time. Failure to set the budget prevents processing. This is not a guarantee
against instantaneous per-core peaks or a cap on other in-process ONNX work.
See [worker build/runtime details](../native/README.md).

Inference now has dependency-free DXGI inventory and an explicit DirectML
selection API. Original enumeration indices, LUIDs and software flags are
preserved. Selected devices are rechecked before use, including cached-session
lookup; invalid or unsupported selections retain CPU fallback. Explicit-device
sessions have separate identities and bypass the existing device-unaware result
cache. Normal CPU inference defaults to one thread, with overrides retained.
This limits parallelism, not core utilization. Evidence-bound OCR remains CPU
only. Automatic fastest-device selection, persistence and user controls are
still pending; inventory is not a performance benchmark.

## Verification

All commands ran in the active phase2 worktree with existing E-local caches.
No private or sealed data, optimizer, model promotion or model download was used
for this feature.

| Check | Result |
| --- | --- |
| Rust unit tests, offline and locked | 6 passed |
| Full Axis tests with actual worker and blocking cancellation fixture | 131 passed, 0 failed/skipped |
| Proposal plus localization tests after dismissal/layout correction | 13 passed, 0 failed/skipped; 110.225 s command wall |
| Full Inference tests | 97 passed, 1 skipped, 0 failed; 8.103 s command wall |
| Full integrated App tests before accessibility repair | 819 passed, 1 failed, 0 skipped; 106.075 s command wall |
| Shell, proposal and localization checks after repair | 23 passed, 0 failed/skipped; 22.554 s command wall |
| Native WPF workflow below | Passed on this host |

The skipped inference test is `ExactClassifierResolvesForCpuAndDirectMl`; this
batch does not establish classifier-model or GPU accuracy. An initial inventory
compile failed CS9191 (`ref` versus `in` for `Marshal.QueryInterface`); it was
corrected before the passing run. The full App run's sole failure was
`EveryShellAutomationIdHasAnExplicitLocalizedOrBoundName`: four new controls
needed explicit localized names. Those names were added, and all 23 affected
shell/proposal/localization checks passed. The unaffected full suite was not
repeated. An earlier sandboxed Axis run could not connect
to its test host within 90 seconds and ran no tests; host-access execution then
passed. No scientific evidence was consumed by either tooling failure.

Commands: `cargo test --manifest-path native/graphreader-engine/Cargo.toml
--target-dir artifacts/native-engine --offline --locked`; Release `dotnet test`
for `GraphReader.Axis.Tests`, `GraphReader.App.Tests`, and
`GraphReader.Inference.Tests`, using `--no-restore -m:1 -nr:false
-p:UseSharedCompilation=false`. Axis process tests set both
`GRAPHREADER_RUST_WORKER` and `GRAPHREADER_RUST_BLOCKING_WORKER`.

## Native workflow evidence

Used the owned six-panel synthetic PNG with SHA-256
`75edb079ccfa8a3a2806bef467ec2dd10d13905bee762fd48ee9be21361be98d`,
with first-panel crop `(0,0,1200,313)`. No image was copied for this check.
The routine App DLL tested had SHA-256
`e36d9e91aceb8f902e3f5ce42b6052d95e6d26f9ca8ff5fb59f31477c8c946c2`;
the reviewed OpenCV DLL was `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`.
The subsequent hardware-policy integration is covered by commands above; this
native check did not exercise GPU selection. The final accessibility-name-only
change was command-verified after this interaction check.

Observed in the native WPF app using Computer Use:

1. Import opened six panels. Suggest axes displayed anchors `(144,260)`,
   `(144,96)` and `(1055,260)`, with all numeric fields blank and Apply disabled.
2. Entered the visible labels 10, 1 and 24. Apply remained disabled until review
   confirmation was checked. Applying reported sessions 1 through 24 saved.
3. Created a manual QA series and one point. The series card count updated to 1.
4. Saved `rust-axis-review.garproj`. Export preview showed one point, one series,
   one phase, zero blockers and zero warnings; export wrote CSV plus two audit
   sidecars. File inspection confirmed one row and original pixel provenance.
5. Closed the app and opened a fresh process. The recent project reopened with
   one point and form values 10, 1 and 24 restored.

This was an interaction/persistence check, not a marker-classification or
automatic-calibration accuracy test. The manual QA series used its default
circle symbol. Export used observation-order x mode, explicitly recorded with
x confidence zero, rather than claiming an OCR-recognized printed x value.

| Output | Bytes | SHA-256 |
| --- | ---: | --- |
| Project | 22,016 | `51a223ed04e65fc17bddcb9f9934afbe2637d92eae6f4c52d921ead63380a76d` |
| CSV | 45 | `9ca891dcff5abff982bd28e894faf5700876f6e54774ee5969b5f74ab6defdcf` |
| Audit CSV | 697 | `2c00806eb636c42dedf531af87bb04762cbfa296ad4d5650688994858addb465` |
| Audit JSON | 1,423 | `f76aa78ac56c88948011166c850dbc9b23066172b55bccf9eb7798f70b6d6736` |

## Timing and distribution limits

The final worker SHA-256 is
`efab65589e8a31f4353a72b50b89f5099455c7055908b9eeaac679175bb66f48`.
Three fresh-process runs on a deterministic in-memory 2048 x 2048 grayscale
image took 73.474, 58.274 and 57.723 ms, median 58.274 ms. Each returned the
expected axes and six-tick endpoints. Timing includes process startup, pipe
transfer and worker processing, but excludes WPF decoding/UI work. Windows CPU
accounting returned 0, 0 and 31.25 ms at this short duration, too coarse for a
useful utilization estimate. These three simple cases are not a tail-latency or
representative accuracy benchmark.

The Rust crate has no external Cargo dependencies. The helper copies the actual
compiler's standard-library notices and compiler/hash metadata. Static CRT
linking removed the worker's separate VC-runtime DLL dependency; inspected
imports were Windows system DLLs only. Standard-library notices have SHA-256
`5647be074c8edf7339fd863055923a8fc80bc5610a8d4661ec3b767b9d392c27`.
Own source remains Apache-2.0; Rust/standard-library and MSVC runtime notices and
redistribution terms remain applicable. No model was added.

This is source and host verification, not installer/portable clean-machine
acceptance or full automatic product completion. Retained package 0.4.37 is
unchanged. No version advance, public release or tag was created for these
routine builds. The next meaningful package must include and verify the worker
and notices in both distribution forms.

Completed TRX, timing JSON and isolated native QA directories were retired
after this record was pushed and consumers finished: 30 files, 1,827,891 bytes.
Keep the original owned
input, current models and active compilation dependencies. The new recognition
probe uses the original image/crop directly and does not depend on the saved QA
project.
