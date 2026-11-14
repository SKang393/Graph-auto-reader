<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR padding and axis continuation, 2026-11-14

## Changes

The Rust axis worker now treats the connected downward continuation of the Y axis
as part of the axis, not as an X tick. It preserves separated, measured X ticks
and still rejects unrelated long interior strokes as ambiguous. The regression
covers 1-to-3-pixel axis strokes and refuses to invent an intersection tick.

For dynamic-width calibration OCR only, `CalibrationOcrSession` sets a maximum
padding overhead factor of 2. `OcrCropBatcher` splits an existing batch into
contiguous sub-batches when that bound is exceeded. It keeps the pre-split
content widths and sampling coordinates, preserves every crop, and never
allocates more tensor elements than the unbounded batch. Other crop modes and
callers that leave the option unset retain their prior grouping.

## Verification

| Check | Result |
| --- | --- |
| Rust axis tests | 12 passed |
| OCR crop batching tests | 18 passed, 0 failed |
| Integrated calibration App tests | 122 passed, 0 failed |
| Owned CPU crop-batching probe | exit 0; 41.977 s parent wall |
| Owned DirectML crop-batching probe | exit 0; 14.110 s parent wall |
| Owned axis continuation proposal | complete; 6 measured tick pairs; 3,287.4036 ms pipeline; process wall time not retained |
| App worker lifecycle | six labels recognized; warm session reuse; cancellation and child exit verified; restart and disposal verified |

The probe-only comparison used one generated 1600 x 160 image with seven small
glyph regions and one wide caption, eight crops per read, and one CPU
intra-op/inter-op thread. Each provider received four reads in each of two
configurations (eight reads total): one cold and three warm per configuration.
The generated input hash is
`4530e1be239b069226df22f280b54679ec2db7fff43593fa774432bc446ade2b`. Without
the bound, the RGB batch allocation was `8 x 3 x 48 x 2925` (3,369,600 tensor
elements). With factor 2 it became `7 x 3 x 48 x 320` plus `1 x 3 x 48 x 2925`
(743,760 elements, 77.9% fewer). All eight rows remained in the same order.

| Provider and batching | Warm median recognition | Warm median recognition plus measured pacing |
| --- | ---: | ---: |
| CPU, unbounded | 7,653.9695 ms | pacing not measured |
| CPU, factor 2 | 1,865.7967 ms | pacing not measured |
| DirectML, unbounded | 425.4164 ms | 860.3212 ms |
| DirectML, factor 2 | 257.9228 ms | 533.6882 ms |

DirectML was DXGI adapter 0, LUID 82535, AMD Radeon RX 6600 XT. Its comparison
inserts a delay equal to each batch's recognition time; the combined timing
above is recognition plus that measured delay. This is a review-only pacing
diagnostic, not an OS GPU utilization cap. DirectML may also execute kernels on
the CPU.

For baseline versus factor-2 results, text, parsed numeric value, failure code,
and polygon matched for all 32 rows on both providers. Median absolute
confidence change was 0.00228047 (CPU) and 0.00228071 (DirectML); maximum was
0.03077793 and 0.03078366 respectively. Comparing CPU and DirectML outputs,
the same four non-confidence fields matched on all 32 rows in both batching
modes. Median absolute confidence differences were 0.0000007153; maxima were
0.0000050068 unbounded and 0.0000008345 factor-2.

The separate axis proposal used the owned tick control
`artifacts/ocr-padding-check/owned-axis-continuation.png` with source hash
`4e7dcc177588243c4ef546ce65485b90b7d3a2e5d369ca79e7c61d5b130ae390` and panel
hash `63a6347c558469013f1f68d8bf871139ce9fdf77084e954c3a52f8ba8a74dfcb`.
All six measured X/Y ticks paired, including the observed Y zero; proposal
anchors were (140,380), (140,80), and (540,380). The App worker lifecycle result
is `artifacts/ocr-padding-check/cpu-worker-lifecycle.json` (SHA-256
`eb23f36dbe232b957367f0c6717232c9421b2f5701b9df42765cdd62f2cd1c77`). It used
the owned 640 x 480 tick control SHA-256
`c1750cab62d79c96b75310cdb5419edcb91a1e99218b248894a26b731aa43dda`. It
recognized 10, 5, 0, 1, 2, 3 on cold, warm, and restarted workers. Timings were
3.453 s cold, 4.222 s warm, and 7.298 s restart; these variable observations do
not show a uniform warm-call speedup.

The initial split recalculated fractional content width after splitting and
could change resize sampling. The fix carries pre-split widths into crop
creation; pixel-parity tests passed before OCR comparison. The existing real
baseline remains 0/120 and was not rerun. No private corpus was read.

## Commands

Builds completed with zero warnings and errors. Tests used isolated DLLs in
`artifacts/hardware-final-build`; the OCR and integrated App runs were 18/18 and
122/122. Assets are from the retained 0.4.42 review-assistance package.

```powershell
$out = Join-Path (Get-Location).Path 'artifacts/hardware-final-build/'
$assets = 'E:\Graph-auto-reader\artifacts\dev-portable\builds\0.4.42-20261114T040100317Z-0558b3fa\review-assistance\ocr'
$env:CARGO_TARGET_DIR = Join-Path (Get-Location).Path 'artifacts/native-engine'
cargo test --manifest-path native/graphreader-engine/Cargo.toml --locked --offline --jobs 1
dotnet vstest "$out/GraphReader.Ocr.Tests.dll" --TestCaseFilter:"FullyQualifiedName~CropAndMaskTests"
dotnet vstest "$out/GraphReader.App.Tests.dll" --TestCaseFilter:"FullyQualifiedName~Calibration|FullyQualifiedName~AxisSuggestion"
& "$out/GraphReader.CalibrationOcrProbe.exe" --ocr-padding-check $assets
& "$out/GraphReader.CalibrationOcrProbe.exe" --ocr-padding-gpu-check $assets 0 82535
& "$out/GraphReader.CalibrationOcrProbe.exe" --tick-proposal-check (Resolve-Path artifacts/ocr-padding-check/owned-axis-continuation-request.json) (Resolve-Path "$out/GraphReader.App.exe") $assets (Resolve-Path "$out/native/graphreader-engine.exe")
& "$out/GraphReader.CalibrationOcrProbe.exe" --worker-check (Resolve-Path artifacts/ocr-padding-check/owned-tick-request.json) (Resolve-Path "$out/GraphReader.App.exe") $assets
```

Regenerate the owned base with `New-TickCalibrationFixture.ps1`; the continued
variant adds a black one-pixel line from (100,380) to (100,410), without
antialiasing, in a separate PNG. Rebind its request to the new image hash.
Package paths above identify the tested historical input. After replacement,
use the current verified package only if the model/manifest/license identities
still match. Enumerate DXGI again instead of assuming historical adapter LUIDs.

The OCR, App, and probe checks are headless synthetic diagnostics. No sealed or
private corpus was read, and no model, threshold, provider default, export
format, or license changed. No model training or accuracy claim is made.

## Evidence identities

| Evidence | SHA-256 |
| --- | --- |
| CPU comparison report | `d5b9009b20189b730a4188a1f659f48ec015af95b99f850b95e14dadcda668d2` |
| DirectML comparison report | `7d36d9a9de5b67789f7bd9288a84485c2043e244abaa5956ebc88d7ea00d2b47` |
| Axis continuation proposal | `4eea560fd26c153769cc1e709cb639b09e36b5dfd21d3f5aadd60cae7fed7b77` |
| App worker lifecycle result | `eb23f36dbe232b957367f0c6717232c9421b2f5701b9df42765cdd62f2cd1c77` |
| OCR test TRX | `230c5ce927a7980e3e599d0f64b1b2303a07ef957cb3de9de1f53230fea28860` |
| Integrated App test TRX | `055bbdabc44eb49f0bb9f2d954716852edaddf80629380253f31d8cd7da33f1b` |
| CPU probe binary | `86dee7a972b1c553849e902780d982da28b2766578bdbd71ae2f08de502888a7` |
| DirectML probe binary | `7a6e1a058f8af6f1e223b9d7e270fb3fa872af81592603d0dc822749652f352d` |
| OCR model | `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` |
| OCR manifest | `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a` |

This source batch is based on `d7fe8b1046102af7d2bedb636b45d4d464951db7`.
After the 0.4.43 packaged check and pushed record `46b6d039`,
`artifacts/ocr-padding-check` was retired: 34 files, 312,971 path bytes.
Current runtime inputs remain available in the retained package.
