<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Calibration OCR probe

`--tick-proposal-check <owned-request.json> <absolute-App-exe> <absolute-assets> <absolute-Rust-worker>`
runs the Rust geometry worker and the actual OCR child against the same owned
crop, then reports tick-label pairing and a review-only calibration proposal.
It uses detected geometry rather than the request's diagnostic plot. Nothing is
applied, saved as scientific data or exported. The parent has an 80-second hard
deadline and writes no image. An incomplete proposal is a reported outcome,
not an automatic-calibration accuracy pass.

`--automatic-device-check <absolute-App-exe> <absolute-assets> <absolute-empty-settings-root>`
uses the real in-memory owned benchmark and checks automatic profile persistence,
identity invalidation and an explicit CPU override. The supplied settings directory
must already exist and be empty; it receives the test settings file. The parent has
a 100-second hard deadline. No image is written. This is a review-only execution
and consistency check, not an accuracy result or production approval.

Diagnostic only. One explicitly identified owned synthetic source is cropped in
memory. Component polygons in the existing axis-role geometry are recognized by
the pinned English reader. Every result requires review. No calibration, numeric
defaults, targets, model selection, persistent inference cache or dataset scan.

`--self-test` uses in-memory ink only and does not open the reader or source.
`--request <json>` reads these required fields:

```json
{
  "scope": "owned-synthetic-review-only",
  "image_path": "<absolute original source path>",
  "image_sha256": "<explicit source SHA-256>",
  "source_crop_xywh": [0, 0, 1200, 313],
  "plot_ltrb": [105, 96, 1095, 260],
  "model_path": "<retained en_PP-OCRv5_mobile_rec.onnx>",
  "manifest_path": "<retained recognizer.json>",
  "license_path": "<PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt>",
  "notice_path": "<PaddlePaddle-PP-OCRv5-Models-Notice.txt>"
}
```

The example crop/plot is the explicitly measured owned QA first panel, not a
default. Plot coordinates are relative to that crop. Returned polygons include
the crop offset and refer to the full original source. The reader, manifest,
license and notice have exact pins in `Program.cs`; arbitrary models are rejected.

Build the project in Release using the existing local package cache. No solution
or package build is required. Root must coordinate builds with any running App.

```powershell
$exe = '.\tools\GraphReader.CalibrationOcrProbe\bin\Release\net10.0-windows\win-x64\GraphReader.CalibrationOcrProbe.exe'
& $exe --self-test
# After an explicit owned request has been prepared and execution authorized:
& $exe --request $requestPath
```

The process applies and queries a Windows Job hard cap of at most half one
logical core and BelowNormal priority before OCR. ORT uses CPU only, one intra-op
thread, one inter-op thread and one queue worker. Model/session initialization is
timed separately from image decode, components, crops and recognition. The host
has cooperative cancellation at 170 seconds, a hard process deadline at 175
seconds, at most 128 candidates and at most 1,000,000 output bytes. A caller should
also terminate its process tree after 180 seconds. Output is one JSON object on
stdout; no source crop or model copy is written.

`--worker-check <request.json> <absolute-App-exe> <asset-root>` exercises the
real App client against that same explicitly owned source and crop. It encodes
the panel only in memory, checks a cold request, a same-process warm request,
a 50 ms cancellation and child exit, then a cold restart and disposal. If the
third request finishes before cancellation, the check fails rather than claiming
cancellation. It records observed label text as review-only diagnostic output.
The probe itself does not apply the CPU job in this mode; the child applies and
verifies its own budget before inference. The combined deadline is 360 seconds,
with a 365-second hard watchdog. This check does not approve a model or report
accuracy. Run it only on an explicitly identified owned synthetic source after
execution has been authorized.

`--hardware-check <absolute-owned-request.json>` runs separate hidden probe
children sequentially: CPU first, then at most two nonsoftware DXGI adapters
using their original index and LUID. Each child checks the same pinned source,
model, manifest and notices and compares candidate identity with CPU. Each
uses one session, one CPU inference thread and one in-flight request; the
existing Windows CPU job is applied before work. The GPU diagnostic refuses
fallback to a separate CPU session; a DirectML session may still execute some
kernels on CPU. Failed or unsupported providers are recorded as failures.

Each provider runs one cold and three warm recognition rounds. GPU batches
are followed by a delay at least as long as the measured batch duration; this
is pacing, not a hard GPU utilization limit. The report includes timings,
process memory, available DXGI local-memory budget snapshots, and per-round
text/value/polygon differences against CPU. It is an owned-input consistency
diagnostic, not an accuracy claim, provider selection, model approval or
production change. Each child has a 45-second parent deadline and the parent
has a 150-second hard deadline. No image or model copy is written.

`--gpu-worker-check <owned-request.json> <absolute-App-exe> <asset-root> <DXGI-index> <LUID>`
runs the same actual-client lifecycle check against an explicitly identified
hardware adapter. It requires the successful responses to report DirectML;
CPU fallback is not counted as a GPU check. It records the full runtime identity,
resource budget and separate pacing timings. Cancellation still must end the
owned worker, and the restarted worker must initialize a fresh session. It does
not change saved application settings or run a provider-selection benchmark.
