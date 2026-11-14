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
That source fix was newer than the retained 0.4.37 package at the initial audit;
it is now included in the delivered 0.4.39 preview.

The [initial OCR diagnostic](COMPOSED-OCR-BASELINE-20261111.md) took 93.612 seconds
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
two nonnumeric strings, with no accuracy claim. The subsequent
[calibration-label assistance batch](CALIBRATION-LABEL-ASSISTANCE-20261113.md)
connected the retained reader to editable, source-bound WPF suggestions.
One owned panel took 3.221 seconds cold and 1.593 seconds with the same session;
cancellation, restart, disposal and explicit native value confirmation were
checked. These are review-assistance observations, not automatic calibration
accuracy or production approval. The next execution step is bounded hardware
measurement and a visible, persisted backend choice; GPU presence alone must
not change the conservative OCR default. The subsequent
[bounded hardware comparison](OCR-HARDWARE-CHECK-20261113.md) measured warm
recognition including GPU pacing at 1.190 s on CPU, 0.235 s on RX 6600 XT and
0.537 s on integrated Radeon, with matching outputs on one owned panel. GPU
cold startup was slower. Saved device overrides, explicit CPU fallback and
pacing are implemented and checked with the actual GPU worker. Native display
was inspected; native saved-choice/reopen verification is waiting after the
user stopped Computer Use (REV-011). The subsequent
[automatic device-profile batch](OCR-AUTOMATIC-DEVICE-20261113.md) adds a
cancellable idle comparison using an in-memory owned fixture, identity-bound
profiles and explicit overrides. Its actual CPU/GPU comparison and headless
profile persistence passed on this host. The next independent step is measured
Rust tick positions paired with OCR labels for a reviewed calibration proposal;
label centers alone must not become calibration anchors. The
[measured-tick proposal batch](CALIBRATION-TICK-PROPOSALS-20261113.md) now
combines both workers through one Suggest calibration action with explicit
review. Its complete-label control returned a proposal in 3.10 s; an incomplete
owned panel remained uncalibrated in 3.90 s. Native display/interaction remains
queued. The integrated [0.4.39 preview](HOSTED-INTERNAL-PREVIEW-20261113.md#hosted-delivery-0439)
is uploaded, downloaded, verified and retained. Its packaged complete control
took 3.833 s and its incomplete control stayed uncalibrated in 4.103 s.
The [marker example assistance batch](MARKER-EXEMPLAR-ASSISTANCE-20261113.md)
matches a user-selected original-pixel example inside the confirmed plot and
requires individual acceptance through existing point editing. Its 911 App
tests passed; a 1280 x 800 control took 0.649 s for the first request and
0.545 s after verified cancellation. This reuses the existing native correlation
kernel and keeps rejected model training paused. The integrated
[0.4.40 preview](HOSTED-INTERNAL-PREVIEW-20261113.md#hosted-delivery-0440)
is uploaded, downloaded and verified; its actual packaged marker request took
0.572 s and cancellation took 0.097 s. Native interaction remains queued.
The [reviewed batch action and complete owned workflow](MARKER-BATCH-WORKFLOW-20261114.md)
now pass: 24 focused tests, 64 additions in 13.876 ms with one Undo/autosave,
and actual geometry/OCR/marker-to-export/reopen in 4.937 s process wall time.
The same batch record now includes the first/last-session and Y-zero boundary
fix: original image context may cross the plot, with all candidate centers
inside it. Thirty focused tests and the actual boundary-marker workflow passed;
the latter took 4.889 s. The subsequent
[connection-context correction](MARKER-CONNECTION-CONTROLS-20261114.md) improved
the fixed changing-slope control from 0/4 to 4/4 at the same 0.9 threshold;
horizontal and hollow controls also returned 4/4 with no false proposals in the
four controls. Thirty-seven focused tests passed. A near-limit 4.096-megapixel
worker request took 0.976 s and cancelled in 0.097 s. The integrated
[0.4.41 preview](HOSTED-INTERNAL-PREVIEW-20261113.md#hosted-delivery-0441) is
uploaded, downloaded and verified; its packaged four controls reproduced the
expected results. These checks do not establish representative accuracy, native
interaction or final production approval. The
[Prepare crop increment](PANEL-CROP-WORKFLOW-20261114.md) now creates an
uncalibrated child while retaining its source image/PDF, authenticated parent
lineage and reversible coordinates. Existing automatic overlap checks remain
intact. Oversized panels direct users to Crop panel before marker work starts.
The measured 4-megapixel crop took 43.978 ms; actual cropped Rust/OCR/marker
review through export/reopen took 5.537 s on an owned control. Native interaction
is still waiting under REV-011. The integrated
[0.4.42 preview](HOSTED-INTERNAL-PREVIEW-20261113.md#hosted-delivery-0442) is
uploaded, downloaded and verified. Its packaged cropped workflow took 5.292 s;
source CI passed 2,626 .NET tests, 10 Rust tests and 68 packaging checks. The
[fixed real-dev diagnostic](REAL-DEV-CALIBRATION-DIAGNOSTIC-20261114.md) then
completed all 120 projects in 209.084 s: geometry on 36, 298 numeric readings,
and zero complete calibration proposals. Reused OCR median was 4.132 s;
recognition dominated its cost. No sealed payload was read and no training ran.
Prioritize diagnosing geometry coverage and recognition cost, with repairs
reproduced on owned/synthetic inputs and no private-answer tuning. Preserve
scientific cross-checks and the manual fallback. Complete native interaction
checks when Computer Use resumes, and fix observed workflow defects in parallel.
The subsequent [owned padding and axis checks](OCR-PADDING-AND-AXIS-CONTINUATION-20261114.md)
repair two source-reproduced defects without using private answers: a continued
Y-axis no longer discards separately observed X ticks, and dynamic calibration
OCR splits excessive padding while preserving each crop's source sampling.
On one mixed-width owned fixture, warm recognition fell from 7.654 to 1.866 s
on the capped CPU and from 0.860 to 0.534 s including pacing on the RX 6600 XT.
Compared text, values, failures and geometry matched; confidence can change.
An owned continued-axis panel produced a complete six-tick proposal in 3.287 s.
These controls do not revise the real-dev result or establish general accuracy.
The [export receipt correction](EXPORT-RECEIPT-ISOLATION-20261114.md) also keeps
completed export summaries and pending warning acknowledgements bound to the
reviewed tab when selection changes during export.
The [varied Auto workload check](OCR-AUTOMATIC-DEVICE-20261113.md#varied-workloads-2026-11-14)
now alternates two owned layouts within its existing four reads. All 23 focused
tests and the actual CPU/two-GPU comparison passed. CPU remained selected:
8.804 s total versus 8.447 s on RX 6600 XT, below the unchanged 15% switch
margin. Startup, worker IPC and pacing are included. Geometry-failure guidance
now points to cropping with labels and visible zero or manual calibration.
The integrated [0.4.43 preview](HOSTED-INTERNAL-PREVIEW-20261113.md#hosted-delivery-0443)
is uploaded and verified; its packaged continued-axis control took 3.909 s
process wall time. Source CI passed 2,644 .NET tests, 12 Rust tests and 68
packaging checks. The [fixed delivered confirmation](REAL-DEV-CALIBRATION-443-CONFIRMATION-20261114.md)
completed the same 120 real-dev inputs in 122.567 s with a 2.705 s reused OCR
median, but still zero complete proposals. Geometry returned on 36; a separate
geometry-only diagnosis found 23 without usable X tick arrays and 13 without
usable Y arrays. Those counts overlap and do not prove printed ticks are absent.
Prioritize source-reproduced geometry gaps, with owned regression controls,
instead of further reader optimization or training. Preserve the frozen reader,
scientific cross-checks, explicit review and manual fallback.
The [inward-tick and transparent-image batch](INWARD-TICKS-AND-TRANSPARENCY-20261114.md)
repairs owned reproductions of three gaps: inward ticks, a leftward baseline
continuation discarding measured Y ticks, and transparent pixels becoming black.
All 24 Rust tests and 48 focused App tests passed. Transparent and opaque graph
controls returned identical complete calibration proposals; the leftward-axis
control changed from no usable Y ticks to six paired ticks in 3.992 s. Actual
transparent-marker execution, cancellation and restart also passed. These are
source-level owned controls, not a revision of the recorded real-dev coverage.
The integrated [0.4.44 preview](HOSTED-INTERNAL-PREVIEW-20261113.md#hosted-delivery-0444)
is uploaded and verified: source CI passed 2,646 .NET tests, 24 Rust tests and
68 packaging checks. Its packaged transparent/inward/continued-axis control
took 3.884 s. The [fixed real-dev confirmation](REAL-DEV-CALIBRATION-444-CONFIRMATION-20261114.md)
still returned geometry on 36/120 and complete proposals on 0/120, with the same
reason counts. It took 163.869 s overall; the separate reused OCR median was
2.308 s. These fixes have not improved this corpus's complete-proposal coverage.
Continue source-reproduced geometry repairs on owned controls. Keep native UI
acceptance queued and preserve explicit review and manual calibration.
The [white-margin and label-position draft batch](LABEL-POSITION-DRAFTS-AND-WHITE-MARGINS-20261114.md)
adds a guarded Rust fallback only after `NO_AXIS` and a separate review draft
when an axis has no measured ticks. The latter labels estimated positions
persistently, preserves scientific checks, and still requires explicit review
and Apply. All 31 Rust tests and 64 focused App tests passed. Owned controls
returned six measured pairs through 640-pixel white margins and six estimated
pairs without tick marks, in 3.397 s and 3.476 s process time respectively.
The tickless anchors differ by one pixel from the printed axis; this is a
review aid, not measured-tick accuracy. Preview 0.4.45 is uploaded and verified;
its owned tickless prepared-crop workflow completed explicit simulated review,
marker acceptance, CSV export and save/reopen in 4.643 s, retaining three points
and user-confirmed manual calibration provenance. Source CI passed 2,654 .NET
tests and 31 Rust tests.
The [fixed 0.4.45 confirmation](REAL-DEV-CALIBRATION-445-CONFIRMATION-20261114.md)
still returned geometry/OCR on 36/120, zero complete measured proposals and zero
complete estimated review drafts. It took 160.359 s overall; reused OCR median
was 2.314 s. Neither result establishes real calibration readiness. Stop treating
additional isolated controls as evidence of improved corpus coverage. The next
batch will retain independently validated single-axis review results when the
other axis is incomplete, and use measured tick separation for the owned
reproduction where nearby X labels merge into one OCR crop. Keep every
scientific check and explicit review; do not tune thresholds or models against
private answers. Native visual acceptance remains waiting.
The [partial-axis and label-grouping batch](PARTIAL-AXIS-REVIEW-AND-TICK-LABEL-GROUPING-20261114.md)
now passes 1,001 App tests and 859 OCR tests, with 16 existing model-dependent
OCR checks skipped. An actual owned dense-label run changed from one merged
`1 2 3` reading interpreted as 123 to six measured pairs and a complete review
proposal in 3.299 s. Without measured ticks, the ambiguous text remains
nonnumeric while the valid Y draft is retained. The tickless prepared-crop
workflow still exported and reopened three manually confirmed points in
4.735 s. Preview 0.4.46 is uploaded and all 477 extracted files match its ZIP.
Its packaged dense-label check returned six measured pairs in 3.649 s, and the
prepared-crop workflow exported and reopened three points in 4.889 s. Source CI
passed 2,693 .NET tests and 31 Rust tests. The
[fixed delivered confirmation](REAL-DEV-CALIBRATION-446-CONFIRMATION-20261114.md)
still has zero complete measured or estimated calibrations across 120 cases.
It exposes three X-only and two Y-only drafts, without an accuracy or saved-click
claim. Reused OCR median is 2.294 s. Automatic calibration remains incomplete.
The [gridline source repair](GRIDLINE-AXIS-RECOVERY-20261114.md) now resolves an
independently reproduced owned failure using unique outward tick evidence,
bounded search and rejection of multiple possible panels. All 37 Rust tests
pass; the actual Rust/OCR control returns six measured pairs in 3.193 s.
It is not yet packaged. Do not infer that it explains private failures or tune
thresholds/models against private answers. Native acceptance remains waiting.
Defer phase-divider assistance: current manual insertion is already one click,
while a new detection/review action would add work for ordinary visible dividers.
No new worker protocol or model is justified without a demonstrated benefit.

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
