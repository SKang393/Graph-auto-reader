<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Development direction, 2026-11-11

The maintainer requested a Rust-based, responsive Windows application and
authorized changing the development plan immediately. This amendment replaces
the earlier C#-only processing assumption. Keep WPF as the native desktop shell;
introduce Rust as the local processing worker. Python remains a development and
training tool. Project files, original-pixel coordinates and export safeguards
remain compatible. No browser shell, required cloud service or telemetry is added.

## Findings and decision

Automatic calibration is implemented. `ProductionAutomaticDetectionAdapter`
associates OCR tick evidence with geometry and fits calibration through
`RobustCalibration` and `SessionLattice`. It is reachable only through the full
approved Analyze pipeline. ManualPreview does not run it, and unrelated marker,
legend, phase and mask readiness currently prevents automatic calibration too.
This coupling delays useful partial automation.

Manual import, calibration, point editing, save, CSV/audit export and fresh-process
reopen have native evidence in [the QA record](MANUAL-PREVIEW-NATIVE-QA-20261111.md).
The saved-form correction at `facfabf76ccf0305153dc2daddabcb9f91ff4e76`
also passed [Windows CI](https://github.com/SKang393/Graph-auto-reader/actions/runs/43109029138).
That source fix is newer than the retained 0.4.37 package.

The [current OCR diagnostic](COMPOSED-OCR-BASELINE-20261111.md) took 93.612 seconds
for 23 images, about 4.07 seconds per source. This is a CPU, one-thread,
optimization-disabled evidence configuration, not a measured best production
configuration. It does not isolate detector, recognition, preprocessing or startup
time. An older full workflow attributed median panel costs of 3,064 ms to marker
centers, 2,152 ms to the composed OCR pipeline, 58 ms to axes and 34 ms to marker
classification. Detector and recognizer records repeat the same OCR pipeline
timing; adding them would double-count it. That historical run also had failures.

The host has a Ryzen 5 5600G, 6 physical/12 logical cores, an RX 6600 XT and an
integrated Radeon. Normal ONNX policy can use all physical CPU cores and selects
DirectML device zero when available. The evidence route instead forces CPU.
Neither policy proves the best device was selected or limits sustained core use.
C# already calls native ONNX/OpenCV, so changing language alone is not evidence
of an inference speedup.

## Ordered implementation batches

1. **Usable calibration assistance.** Add a Rust worker that proposes axis
   positions from original pixels. Expose it in ManualPreview independently of
   model approval. Show proposals for correction; require explicit confirmation
   of actual numeric labels before applying calibration. Geometry alone must not
   create scientific values or make a project exportable. Preserve the previous
   calibration until acceptance. Verify image -> proposal -> correction ->
   confirmed calibration -> manual points -> CSV -> reopen in the native app.
2. **Measured OCR assistance.** Reuse a licensed local OCR adapter behind the same
   worker lifecycle, report separate cold/warm decoding, preprocessing, detector,
   crop recognition, geometry and queue timings. Start with needed tick crops;
   do not run markers, enhancement or full-page semantics merely to calibrate.
   Keep ambiguous or inconsistent labels visible for confirmation. Preserve
   monotonicity/spacing cross-checks and source bindings. Compare actual output
   and end-to-end latency before moving an existing native kernel into Rust.
3. **Hardware-aware execution.** Inventory usable processors and graphics
   adapters, then select a supported backend using a small owned-input benchmark
   and memory headroom. Persist the selection with hardware/runtime identity,
   allow an explicit override, and fall back with a visible reason. Keep one
   foreground image task by default. Separate worker resource limits from WPF.
   Add GPU pacing and resource measurements before enabling GPU by default for
   this path; do not equate GPU presence with faster inference.
4. **Complete editable automation.** Add marker and semantic proposals one stage
   at a time to the same review workflow. Reuse sessions, input buffers and valid
   stage caches. Test cancellation and bounded storage. Finish actual installer
   and portable clean-machine verification before the authorized final release.

The first Rust worker uses a below-normal priority and a Windows Job Object
CPU hard cap of approximately half a logical core of aggregate execution time.
It must fail clearly if the budget cannot be applied. This is an interval-based
aggregate budget, not a guarantee that no core ever briefly reaches 100%.
The GUI is outside the worker's job. Long-running inference will require this
same isolation before claiming the whole application is resource capped.
Microsoft documents the [CPU rate semantics](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information).

The [2026-11-13 assistance batch](RUST-AXIS-ASSISTANCE-20261113.md) completed the
native proposal -> confirmed calibration -> point -> export -> reopen check.
DXGI inventory and explicit adapter selection are implemented and tested;
measured automatic backend selection and UI integration remain pending.
The [recognition-only probe](CALIBRATION-OCR-PROBE-20261113.md) then completed
one owned-image call in 4.119 seconds internally, including 2.344 seconds for
cold session initialization. It returned seven numeric review suggestions and
two nonnumeric strings, with no accuracy claim. The next product step is a
reused reader session feeding editable, source-bound suggestions into WPF.

Measure median and tail latency, cold startup, cancellation and CPU/GPU use on
representative owned inputs. Report failures alongside timings. Do not add new
model accuracy thresholds or run expensive training to compensate for a UI,
coordinate, preprocessing or scheduling defect. The 95% reviewable-error bars
and zero silently exported unvalidated calibrations remain the policy.

## Cactus assessment

Cactus v4.1.0 is a runtime and model family, not an ONNX replacement. Its Mirage
model provides text regions and crop reading as well as general image tasks.
The vendor advertises about 22 MB and 51.3 million parameters. It reports
74 ms on a Galaxy S26 NPU and 107 ms on that phone's CPU, not this Windows host.
No graph-calibration or SCD acceptance evidence was found. Native weights use
`.cact`; the documented outputs need conversion to original pixel coordinates.
See the [official Mirage description](https://cactuscompute.com/blog/mirage) and
[pinned release README](https://github.com/cactus-compute/cactus/blob/bafcf7348607c89f30ff9fde8e59d20b76baa86f/README.md).

The [runtime platform list](https://huggingface.co/Cactus-Compute/cactus)
includes Windows x64, but does not establish Windows Radeon acceleration.
The repository and model card label Apache-2.0; an unresolved
[runtime licensing question](https://github.com/cactus-compute/cactus/issues/189)
distinguishes the prebuilt 4.x runtime from the separately licensed older engine.
Follow-up file-level inspection confirmed the publisher's 4.1.0 manifest binds
an Apache-2.0 LICENSE file to the actual runtime release. That file is 11,358
bytes, SHA-256 `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30`;
the manifest is `32e182f6f811acde1109084ee5f5c9a606098b95cc8774f27ece8bc1a506de3b`.
Mirage's publisher card separately declares Apache-2.0. The older engine's
different license alone is not evidence that it governs these 4.x artifacts.
This supports one bounded local owned-input benchmark, with downloads/cache
inside the checkout and `CACTUS_TELEMETRY=0`. Runtime source publication remains
unresolved and is recorded separately from the confirmed license declaration.
No Cactus component is bundled or approved. Adoption requires measured OCR
quality and latency; core work continues while that comparison is performed.

The [2026-11-13 Windows probe](CACTUS-WINDOWS-PROBE-20261113.md) loaded the
published runtime and Mirage model, but two capped default image calls failed
the publisher's JSON response contract. The diagnostic call emitted
`-nan(ind)` in a text confidence field and took 14.738 seconds. Defer Cactus
integration for this batch and continue the existing licensed OCR reader.
This is an interface failure on one owned image, not an OCR accuracy comparison.

## Storage and completion

Follow [artifact retention](ARTIFACT-RETENTION.md): push source and concise
aggregate outcomes, upload actual packages to repository artifact storage,
then remove completed disposable outputs after their consumers finish. Never
upload private figures, PDFs, annotations or datasets to ordinary Git history.
Keep one current build identity; keep only current runtime models and required
inputs. Do not duplicate model weights, complete environments or old worktrees
for every experiment. Before generating new data, state the expected bytes,
active consumer and cleanup condition. Keep completed disposable results below
1 GB in total; crossing that allowance requires immediate closeout, not another
copy on a different drive.

Full pipeline approval work is paused while these product increments advance.
No model approval, accuracy claim, package replacement or release is implied by
this direction change. The final automatic product remains incomplete.
