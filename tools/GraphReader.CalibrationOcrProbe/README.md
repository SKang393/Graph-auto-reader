<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Calibration OCR probe

`--ocr-padding-check <absolute-reviewed-assets-root>` compares the existing
dynamic batching with the opt-in factor-2 padding bound on seven owned digit
regions and one wide caption region generated in memory. It verifies the
reviewed reader, manifest, license and notice hashes, uses separate CPU-only
one-thread sessions under the existing parent job, and reports initialization,
one cold and three warm read timings, tensor sizes, per-ID text/value/failure/
geometry parity, and confidence differences. It writes no images and makes no
accuracy or production-activation claim. The parent has a 120-second deadline
and a 125-second hard watchdog.

`--ocr-padding-adapters` lists current nonsoftware DXGI indices, LUIDs, names
and available process-budget queries without changing device settings.
`--ocr-padding-gpu-check <absolute-reviewed-assets-root> <DXGI-index> <LUID>`
runs the same owned comparison on that exact DirectML session, requires at
least 256 MiB of reported process-budget headroom and rejects whole-session CPU
fallback. It reports recognition and pacing separately; DirectML can still use
CPU kernels, and pacing is not a GPU utilization cap. CPU mode is unchanged.

`--real-dev-calibration-check <absolute-corpus-root> <absolute-packaged-App-exe> <absolute-assets-root> <absolute-Rust-worker> --explicit-opt-in`
is one fixed diagnostic of all **120 frozen real-dev projects**, with no sealed
payload reads. Its denominator is fixed before results. The App 0.4.46 DLL,
packaged executable, Rust worker and existing reader/manifest/license hashes are
checked before payload reads. All 21 supporting libraries have fixed delivered
hashes; all 14 managed libraries must also match at their actual loaded paths.
Restage the packaged libraries after a probe-only compile. No App rebuild or
replacement candidate is implied. The adapter requires the verified
white-background helper and fails closed before selection if it is absent.
The 0.4.46 binding follows ZIP verification and completed owned packaged checks:
dense measured labels, an unresolved X with a retained Y draft, and tickless
prepared-crop calibration, marker acceptance, export and save/reopen. Its fixed
120-item confirmation is recorded in
[the aggregate outcome](../../docs/REAL-DEV-CALIBRATION-446-CONFIRMATION-20261114.md).
The historical 0.4.45 run is reproducible from source commit `c20d0577`.
The historical 0.4.44 run is reproducible from source commit
`11b24b0f`.
The historical 0.4.43 run can be reproduced from source commit `46b6d039`.

The existing metadata selector and locked Engauge reader revalidate membership
and opened-handle paths before reading each source. A single aggregate digest
binds first-observed project and embedded-image hashes; individual hashes,
paths, IDs, texts, polygons and predictions are never emitted. One source is
held at a time, without copied images, datasets, cache or per-case output.
The diagnostic parent receives the existing half-core Job cap and below-normal
priority before private preprocessing. This mode alone sets and queries
`JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK` on the parent's extended job limits.
Rust and CPU OCR children escape that parent job and establish independent
caps instead of multiplying nested CPU quotas. Other probe modes retain their
non-breakaway default. The parent cap covers parent preprocessing, not the
combined CPU of all independent child jobs.
One persistent CPU OCR session is reused; cancellation can restart
the same fixed reader for a subsequent case, not select another candidate.

Inference receives only original embedded pixels and Rust-detected geometry.
Manual anchors are used afterward to report continuous absolute affine graph
residuals normalized by the manual-anchor graph ranges, without a pass threshold.
The existing complete proposal count and residual distributions remain measured-tick-only. The adapter additionally reports complete review drafts using estimated label positions, their separate residuals, and reasons across all incomplete OCR review drafts. It requires the review-draft API in the pinned App assembly before private selection. These drafts remain review-only and are not production approval.

The 0.4.46 adapter passes measured X ticks into the OCR request and reports separately the availability of validated X and Y review drafts, including complete proposals. Its X-only, Y-only, both and neither counts cover evaluated OCR responses; it also reports any OCR responses not evaluated. Availability does not claim correctness or saved clicks. The new App API and verified 0.4.46 package identities are required before any private selection; those identities have been rebound to the verified ZIP.

Every unsupported, failed or incomplete case remains in the 120-project
denominator. The JSON reports reason counts, coverage, separate cold/reused OCR
latencies and sampled parent/combined memory. Sampling does not prove a hard
memory ceiling. Output is limited to 1 MB; no images or datasets are written.
Cases have a 45-second deadline, with 900 seconds overall and a 905-second hard
watchdog. Partial deadline results explicitly report incomplete status.

This corpus has known development exposure and imperfect manual calibration;
it lacks tick-transcription truth. Results are not independent OCR accuracy,
model selection, an automatic calibration application or production promotion.
`--real-dev-calibration-self-test` checks owned aggregate/residual/privacy
behavior without opening any corpus payload or invoking inference. The real
mode requires explicit root review and execution authorization; it is not a
routine test or an automatic part of the build.

`--real-dev-geometry-properties-check` takes the same six arguments and explicit opt-in as the calibration check. It uses the same locked 120 real-dev inputs and delivered identity checks, but calls only the Rust geometry worker, never OCR. Its aggregate report gives image and detected plot dimension statistics, geometry failures and measured X/Y tick-count buckets. Empty tick lists mean unavailable or ambiguous, not that printed ticks are absent. The same privacy guard, child budget and deadlines apply; no per-case output or images are written. The owned calibration self-test also exercises its aggregate report without corpus reads.

`--cpu-budget-self-test` starts one owned child without images, corpus or models.
It queries the parent's breakaway flag, verifies the live child is outside that
parent job, and checks the child's independently queried cap and below-normal
priority. The child holds its job until acknowledgement; no busy-loop benchmark
or timing-sensitive performance gate is used. Parent/child deadlines and owned
process cleanup are bounded. The stopped nested-quota attempt is plumbing-void,
with no valid metric or selection result. The fixed candidate, model, 120-case
denominator and zero sealed budget remain unchanged for the plumbing retry.

Microsoft documents [parent-relative nested CPU quotas](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information)
and the [extended-limit silent-breakaway flag](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information).

`--marker-workflow-check <owned-request.json> <absolute-App-exe> <absolute-assets> <absolute-Rust-worker> <absolute-empty-output-root> [prepared-crop]`
checks the owned control through actual geometry, OCR, explicit simulated review,
marker suggestions, export and save/reopen. The optional `prepared-crop` embeds
the control in a larger original and verifies its source-coordinate offset and
unchanged parent. This is headless service evidence, not UI or accuracy approval.
It follows the review-draft API, reports measured and estimated positions
separately, and verifies that persisted calibration is user-confirmed manual
calibration with no tick-measurement evidence IDs. Existing owned coordinate
tolerances are unchanged.

`--panel-crop-check <absolute-empty-output-root>` runs the real manual crop
service on an owned 2560×2200 image and checks a 2048×2048 crop, original
pixels and hashes, pre-cancellation, and save/reopen. It measures process CPU,
wall time and a 10 ms STA dispatcher heartbeat; the application crop path has
no hard CPU cap. The probe cancels at 25 seconds and has a 30-second watchdog.
The output directory receives the synthetic source and project, limited to
64 MiB; the result is a performance check, not an accuracy claim.

`--marker-exemplar-check <absolute-App-exe>` runs the actual capped marker
worker on a 1280 x 800 project-owned connected-symbol image generated in memory.
It reports proposals, control-center errors, budget and timing only. No image
file is created, no proposal is applied, and this is not an accuracy test.
It then observes the next owned child process, cancels its request, requires
`OperationCanceledException` and verifies that exact process exits within two
seconds. A final normal request reports restart results. Finishing before the
child can be observed fails the lifecycle check rather than claiming cancellation.
The parent cancels at 43 seconds and has a 45-second hard watchdog.
The optional `transparent-background` argument encodes the same black strokes
over transparent black pixels to exercise the worker's alpha normalization.
`New-TickCalibrationFixture.ps1 -InwardXTicks -TransparentBackground` similarly
creates an owned calibration control with inward X ticks and a transparent
background; white compositing restores the original control pixels.

`--tick-proposal-check <owned-request.json> <absolute-App-exe> <absolute-assets> <absolute-Rust-worker>`
runs the Rust geometry worker and the actual OCR child against the same owned
crop, then reports tick-label pairing and a review-only calibration proposal.
It uses detected geometry rather than the request's diagnostic plot. Nothing is
applied, saved as scientific data or exported. The parent has an 80-second hard
deadline and writes no image. An incomplete proposal is a reported outcome,
not an automatic-calibration accuracy pass.
The current owned probe follows `CreateReviewDraft`, including explicitly marked
label-position estimates only for axes without measured ticks. These estimates
still require the application's review and Apply step. Use a current App DLL
that implements this API; historical packaged controls used their recorded
adapter versions. `New-TickCalibrationFixture.ps1 -NoTickMarks` creates an owned
tickless control; `-WhiteMarginPixels 640` translates the ordinary control
inside white margins without resampling it.
`-DenseXTicks` places the three owned X labels at pixels 140, 160 and 180 to
reproduce merging of neighboring values. The current owned probe supplies
Rust-measured X ticks to the matching-build worker; absent measured ticks retain
the generic grouping path. Calibration rejects internal whitespace as a numeric
literal and can retain a separately validated partial axis without applying it.

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
