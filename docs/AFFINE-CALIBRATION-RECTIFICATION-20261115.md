<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Read tilted-axis evidence through a reversible derived raster

Baseline: `4fcf0b69e7a39e032fb862327b55d5aad9cdd3d1`, with full
[Windows CI](https://github.com/SKang393/Graph-auto-reader/actions/runs/43767186610)
verified successful.
The delivered package remains 0.4.55. No additional package is planned for an
isolated intermediate change.

## Failure and isolated change

The [fixed development geometry diagnostic](NATIVE-AFFINE-AXIS-REVIEW-20261115.md)
returned eight rectangles, one affine frame and three pair-budget refusals.
The affine frame now supports explicit review, but the current OCR/tick path
rejects it because it requires an axis-aligned scalar frame. No OCR was run for
that diagnostic. This is an identified representation limitation, not evidence
that a different OCR model or recognition threshold is needed.

Implement a bounded in-memory rectifier using the existing reversible matrix
math. Its three supplied pixel anchors define the rectified axes. Preserve
the full input extent so outside-axis label margins remain available. Run the
existing tick measurement, literal OCR and sequence checks in that explicitly
derived coordinate space. Keep their raw receipts intact, bind them to the
original image and exact transform, and map displayed polygons, tick locations
and fitted calibration anchors back to original pixels before review/application.
Never interpret a derived scalar coordinate as an original pixel coordinate.

The original image, frozen project/vision schemas, OCR model, confidence bars,
geometry ranking, pair cap, worker resource budgets and native release identity
remain unchanged. Rectification is bounded to the current 4096-dimension and
16,777,216-pixel limits and fails explicitly if its output would exceed them.
It does not silently shrink text. CPU work stays off the UI thread and checks
cancellation. The raster loop uses a cooperative 50-percent activity target
with cancellable waits between small work slices. This is not a process CPU
hard cap; decode and encode remain separately bounded operations, and existing
tick/OCR children retain their job limits. Derived rasters are transient. Four
small owned PNG inputs may use the existing file-import API, shared by both
test paths and retired after their completed outcome is pushed.

## Acceptance and evidence boundary

Owned controls cover positive/negative tilt, shear, identity, border margins,
coordinate round trips, cancellation and malformed/oversized input. Original
bytes must stay unchanged; transforms and mapped anchors must remain valid.
Existing literal, monotonicity, spacing, observed-zero, stale-response and
explicit-review checks stay in force. A refusal is visible and accounted for;
no unvalidated calibration may silently reach export.

Separate supplied-geometry/stub-OCR controls from actual raster geometry,
tick measurement and existing-model OCR. Use the former for coordinate and
binding defects and the latter for bounded integration evidence. Record raw
attempt failures, counts, runtime and limitations. No model/threshold selection
or production-approval claim follows from this source change.

The 120-development/51-reserved partition and fixed comparison inputs stay
unchanged. Sealed budget/use is zero. No private inference is authorized until
the owned controls and exact runtime preflight pass. Any subsequent development
confirmation must exercise the changed stage only, reuse cached unchanged
evidence, retain every failure, and avoid private truth scoring or tuning.

## Ownership and validation

Imaging worker owns the new pure raster rectifier and its owned tests. App
worker owns the coordinate bridge and workspace/VM behavior. The lead owns
shared UI/localization, integration, CI, outcome documentation and cleanup.
No parallel writers share a file. Build once after the integrated source slice,
then run focused coordinate/calibration checks and the required solution tests.
Native review and a bounded owned OCR check follow only on frozen outputs.

## Current implementation and validation

The App bridge keeps the raw derived OCR and tick receipts. Its private proof
binds the original image/suggestion, derived hash, dimensions, transform, request
snapshot, fit geometry and response. Complete, printed-X, entered-session and
partial fits are computed in derived coordinates, then all three anchors are
mapped back together. Public display uses original-panel coordinates and the
existing save path applies the crop-to-source translation once. Refitting
selected/corrected cached labels makes no new OCR or tick request. Review stays
explicit, including after correction. Culture refresh updates labels and rows
without resetting the draft. Oversized/invalid rectification has a localized
recoverable failure that preserves manual entry.

Owned source review caught and corrected two test-construction issues before
compilation: a nominal out-of-bounds fixture had actually used the last valid
pixel, and the rectifier needed the same negative-determinant handedness as its
producer. It also separated original anchor/tick-center bounds
`[0,width-1]`/`[0,height-1]` from OCR polygon-edge bounds `[0,width]`/`[0,height]`.
No tolerance or coordinate clamp was introduced. Native resampled measurements
carry a visible warning and mapped 2D positions, not a direct original-scan claim.

The first full-solution Debug build used
`dotnet build GraphAutoReader.slnx -c Debug --no-restore -m:1 -nr:false
-p:UseSharedCompilation=false -p:NuGetAudit=false`.
It exited with five test-source errors, zero warnings, in **132.1948646 s**;
no tests or inference ran. App and Imaging compiled. Two new App-test calls
needed explicit `CalibrationOcrPoint` types to disambiguate the point/anchors
overloads. Imaging tests had two local-name collisions and MSTEST0044 for an
obsolete `DataTestMethod` attribute. The mechanical corrections retain the
original assertions/cases. The corrected full-solution build passed with zero
warnings/errors in **17.6274363 s**. The focused run then passed **265/265**,
zero skips/failures, in **19.6589207 s**: App 256, Imaging seven and integration
two. Reviewed OpenCV `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`
was restored and verified in four existing outputs. The single full-solution
run passed **3,152**, with **32 expected optional-input/worker skips** and zero
failures across 13 projects, in **124.1445545 s**. App passed 1,284/1,284 and
Imaging 31/31. Private, sealed and external model/data opt-ins remained disabled.
No compilation/runtime output is a new packaged application.

Focused TRX SHA-256:
`cb394eeb250bf85773b419b7a39a8ae1ebbfd302506275130f293a9f8236b51d`;
full summary SHA-256:
`a9813d498b86c9a7af0144d0560d77eab2ce0d3f0d5e860b41d9ced013a5ebc2`.
Frozen 20-library manifest SHA-256:
`b30d8e5f45c031b78c5f85bd689700f9e1208c9697e95bdf8100be3d83a592db`;
App DLL: `144fdf02d0223eeb662a1b00a67a5e203e3057710334ed64f0f4521927a557fc`;
Imaging DLL: `2e3af193741cbb9d24143f77c4555f723aeb9fd97541062a9157473478131e64`;
App EXE: `9c3057f6598b0e5d963719119c9f45b5372f67e2806f82d98debefd8f3ef97a6`.
Rust remains the verified diagnostic `2cc6f263a00544243291f2605031c4df7e147175bd345480bd0a44f92fde0b7a`.

The owned integration probe declares four fixtures before inference: upright
and positive/negative two-degree session axes, plus positive two-degree printed-X
axes. Each has six planted numeric labels. Automatic geometry and explicitly
supplied geometry are separate paths over the same input file. Planted
calibration anchor points are distinct from axis-extent endpoints. The probe
checks all 20 App/probe runtime files, loaded assemblies, model/manifest/notices,
receipt provider and unchanged input/project state.

The one owned worker run, `--owned-affine-calibration-check <output-root>
<App-exe> <assets-root> <Rust-exe>`, ran from **2026-11-15 22:41:29.0396437
to 22:41:50.7407991 UTC**, exited zero with empty stderr, and took
**21.6928051 s**. All four cases and **eight of eight paths** completed. Each
returned six literal numeric rows and six measured associations, with no
estimated positions. The upright automatic path used Rust geometry; all three
tilted automatic paths used the native affine proposal and rectification. Both
printed-X paths produced the intended printed-X review draft while retaining
the existing invalid-session-range reason. Original input hashes, workspace and
calibration state stayed unchanged. CPU provider and model/runtime identities
passed preflight. The largest measured original-anchor residual was
**1.0000000000000173 pixels**; this is reported evidence, not a newly selected
acceptance threshold. No Apply, export, private inference or sealed evaluation
occurred in this run.

The probe-only build passed with zero warnings/errors in **7.69 s**. Probe DLL
SHA-256: `b2915a65a40cf303c2b0358e9006fce03426771c9a4f6d57511680aca8252727`.
Result SHA-256: `8552fbc26872532fc16f47840df9ffc854f99730e8570020c06e5c22c8f9776a`;
run receipt: `a07f6f68e514e54339d27f02c19b9b7bc09517f1963edf00197d0c9e4a566577`.
A read-only PowerShell summary parse error was corrected without repeating
inference. The four owned input PNGs total **24,926 bytes** and remain active
inputs for the pending native review check. Test logs and worker outputs remain
only until the integrated record is pushed and their current consumers finish.

Native review is waiting: the first window-list call for this slice reported a
physical Escape stop, before any launch or input. Renewed access is requested in
[REV-011](REVIEW_QUEUE.md#rev-011---finish-native-preview-verification). No
native behavior of this new bridge is claimed. Code, CI and headless diagnostics
continue independently.

## Changed-stage development confirmation

The single explicit diagnostic used
`--real-dev-affine-calibration-trace <cached-geometry-report> <corpus-root>
<App-exe> <assets-root> <Rust-exe> --explicit-opt-in`. It verified the existing
geometry report SHA-256
`b77aaafc2c16e39e526373888c77a0e55b228a97821551d200e83471581d0993`
and all twelve locked image-only source hashes before inference. The source
digest remained
`4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1`.
Only the one previously returned affine panel was prepared and inferred. The
other eleven project outcomes remain cached, not newly passed or remeasured.
The prior geometry-only report had no affine coordinates, so this selected panel
necessarily ran geometry once and matched the cached crop/dimensions first.

The run started **2026-11-15 22:54:46.2982378 UTC**, ended at
**22:54:51.5825296 UTC**, exited zero with empty stderr, and took
**5.2776812 s**. All six pipeline stages returned, with zero exceptions,
timeouts or unattempted stages and zero invalid original-mapped polygons.
That is execution accounting, not six successful scientific stages:

| Stage | Observation | Time (ms) |
| --- | --- | ---: |
| Source verification | 12/12 image-only sources, frozen digest matched | 715.8003 |
| Panel preparation | Selected cached crop/dimensions matched | 475.1243 |
| Geometry | Rust ambiguous; native affine review proposal returned | 786.0928 |
| Rectification | Reversible frame produced, original unchanged | 536.9097 |
| Measured ticks | **AXIS_BOX_NOT_OBSERVED**, zero bound X/Y ticks | 57.6835 |
| CPU OCR | Five literal rows, two numeric rows | 2230.6266 |
| Association | Two estimated pairs; insufficient pairs | 11.1173 |

**Complete calibration failed** on this selected panel. There was no complete
session draft or printed-X draft. Native ambiguity review reasons remain
visible. No scientific values were fabricated, and no Apply, export, truth
scoring, sealed read or private image write occurred. This is a component trace,
not native UI acceptance or a new twelve-case calibration success rate. Do not
repeat it unchanged. Any diagnosis of the exact-box refusal must first use
owned controls for stroke width, subpixel position and affine resampling;
thresholds, candidate ranking and resource caps remain fixed.

The final probe-only build passed with zero warnings/errors in **6.78 s**.
The owned selection/tamper/missing-pin/stage-accounting self-test exited zero
with no private reads, and all twenty runtime files matched the frozen manifest.
Probe DLL SHA-256:
`c773062bcab00410af11adb5d45c66a6a6328dfce6c44554cb77f37a515138bb`.
Private report SHA-256:
`3f0399ecfad1e77e2239af6609e5383a2241aa8e87f04882a732c8457983ea19`;
run receipt: `44ae3d74ddb69aaed90af5a5df41d7858f540ecf01577c643d2b09c2696007ba`.
Intermediate probe compilations reported CS1061 (list treated as polygon
wrapper), CA1854 (dictionary access) and CA1861 (constant array allocation).
Corrections were limited to probe plumbing; no private inference preceded the
final successful build. The three private output files total **3,837 bytes**.

No dependency, model, license, schema or production-approval switch changed.
New source retains Apache-2.0 notices. Current validation outputs remain only
for integration and the named native-review consumer. Record this outcome on
origin/main before retiring completed logs and private trace copies.

Status: local solution and owned worker checks pass; the changed-stage real
calibration remains incomplete. New-head full CI and native review are pending.
