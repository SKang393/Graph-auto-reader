<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Owned content-span recovery through calibration

## Fixed question before execution

The [Rust content-span correction](RUST-CONTENT-SPAN-RECOVERY-20261116.md)
recovers its newly authored disconnected-decoy fixture in Rust unit tests.
The eight existing wire controls deliberately excluded that new fixture, so
the recovered result has not been traced through panel preparation, actual
literal OCR and numeric association. Existing broad tilted/dense-label matrices
already have evidence; this check does not repeat them.

Starting source is `23910496a6ad158bde89a0c17900fcba059df14d`. Test exactly one
fixed owned image through workspace import/preparation, automatic Rust geometry,
measured ticks, the existing literal reader and review-only fitting. Expected
geometry and values are an output oracle only, never an inference input.
No private source, sealed evaluation, training, new model or threshold change
is involved. Sealed-run budget is zero.

## Frozen input recipe

Reuse `content_projection_decoy_pixels` in
`native/graphreader-engine/src/main.rs` exactly: a white 512 by 512 raster,
one-pixel horizontal axis x=80..440 at y=420, vertical axis y=250..420 at x=80,
outward X ticks at 140/260/380 with y=421..425, and outward Y ticks at
280/330/380 with x=74..79. The disconnected decoy is x=20 at even rows
0..398 plus the pixel (20,420). All these strokes are black.

Overlay owned Segoe UI 16 labels centered at X positions (140,447), (260,447),
(380,447) with strings `1`, `2`, `3`, and Y positions (52,280), (52,330),
(52,380) with strings `10`, `5`, `0`. Verify actual glyph bounds before any
inference; do not alter this recipe after observing its output. No font binary
is copied. Record the single rendered PNG checksum before running workers.

## Stage and resource boundaries

- Bind the current compiled App/worker and required assemblies by checksum
  before execution. Reuse the existing 0.4.58 reviewed OCR assets without a
  second model payload: reader `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`,
  manifest `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a`.
- Import/preparation must produce the intended single panel. Report its crop
  and coordinate mapping. The one traced Rust call supplies the actual
  workspace suggestion and exposes the existing diagnostic route.
- Require `CONNECTED_RUNS` plus the planted geometry and tick arrays in the
  correct mapped coordinate space before OCR. If preparation removes the decoy,
  changes the exercised route or yields different geometry, report
  **UNEXERCISED** and stop. Do not redraw or call another backend to obtain a
  positive result. Native fallback is unattempted on this stopped control.
- At most one actual geometry call and one OCR call, followed by the existing
  association/refit code. Record complete, failed and unreached stages, all
  returned labels and proposal failures. Never Apply, save scientific state,
  promote a model or interpret availability as representative accuracy.
- Preserve the half-core budget, a 55-second operation deadline and a
  60-second watchdog. Bound PNG output to 32 KiB, raw run output to 2 MB and
  new compiler output to 200 MB. The fixed helper may be built normally against
  existing compiled dependencies; no package is created.

## Single completed execution

The attempt ran once from **2026-11-16T13:57:23.9280007Z** to
**13:57:27.6746404Z**, exit **0**, empty stderr, **3.7404104 seconds wall**.
The helper reported 3.3767297 seconds and a **0.4992 logical-core** parent cap.
All seven stages completed; zero failed or unreached stages. There were no
void runtime attempts or fixture redraws.

| Stage | Time |
| --- | ---: |
| Runtime identity verification | 114.5332 ms |
| Fixed fixture generation/check | 238.3968 ms |
| Import/preparation | 493.5343 ms |
| Automatic geometry and packed ticks | 272.2871 ms |
| Literal OCR | 2,152.2040 ms |
| Association/refit | 33.9290 ms |
| Source preservation check | 0.3815 ms |

Preparation returned the full 512 by 512 panel with zero crop offset. One
workspace geometry attempt reached one Rust client attempt, returning
`CONNECTED_RUNS` (trace enum 4), no reason (enum 0), one qualified pair and
the exact planted box and three X/three Y ticks. The provider was `rust`.
The Rust receipt already carries observed ticks, so separate tick measurement
and native fallback were unattempted by design, not silently treated as passes.

One workspace label attempt reached one CPU OCR client attempt. All **six
returned labels** exactly matched the **six planted strings**, with the proper
XTick/YTick roles and no row or response failure. All **six pairs** used measured
ticks. The review draft was **complete**, with **zero estimated positions** and
no fit reasons. No expected geometry, ticks or numeric values entered inference.
No Apply or export occurred. Source SHA-256 stayed unchanged; workspace
preservation establishes retained project/calibration reference identity,
not a deep serialized-state comparison. Independent result review confirmed
these denominators and limits.

This is a successful integration check on **one owned control**. It is not a
paired speed comparison, representative real-graph accuracy, model promotion,
native UI acceptance or a production-approval result. Closed real-development
refusals remain unchanged in their respective outcomes.

## Commands, build accounting and identities

Normal local restore passed in 3.959569 seconds:

```powershell
dotnet restore tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -p:Platform=x64 -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true --nologo
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Release --no-restore -m:1 -nr:false -p:Platform=x64 -p:BuildProjectReferences=false -p:UseSharedCompilation=false -p:NuGetAudit=false --nologo
```

The probe-only build ran twice, both with zero warnings/errors: 10.1326558
seconds initially and 9.2989438 seconds after adding provider/tick-routing
report fields before execution. There were no failed builds. Existing App
Release dependencies were reused. Each normal build copied NuGet OpenCV;
only the probe copy was replaced by the reviewed `c96f91b3...` binary, then
all 20 runtime library hashes were checked against the frozen binding.
No models or source App Debug runtime were copied or modified.

The actual command was the new README mode, invoked with these absolute
paths under `E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2`
except the retained asset path:

- Executable: `tools/GraphReader.CalibrationOcrProbe/bin/x64/Release/net10.0-windows/win-x64/GraphReader.CalibrationOcrProbe.exe`.
- App: `tests/GraphReader.App.Tests/bin/x64/Release/net10.0-windows/win-x64/GraphReader.App.exe`.
- Assets: `E:\Graph-auto-reader\artifacts\dev-portable\builds\0.4.58-20261116T082429970Z-4279db15\review-assistance\ocr`.
- Rust: `artifacts/native-engine/release/graphreader-engine.exe`.
- Output: `artifacts/owned-content-span-calibration-check`.
- Manifest: that output directory's `runtime-pins.json`, followed by its
  separately checked SHA-256 in the table below.

The working directory was the active worktree; `TEMP`/`TMP` pointed to the run
directory's `temp` child. `DOTNET_CLI_HOME` used the existing checkout-local
cache. Stdout/stderr were captured once in `result.json`/`stderr.txt`.

| Item | SHA-256 |
| --- | --- |
| Fixed owned PNG, 2,333 bytes | `2a8b612aa33cb899dd4dc08689693ee53b57641be7a1f15c9c7fb1875ffa53b1` |
| Helper source | `60c490684348a9031589f8136d8ceb48b20ba052156a0dbd004cdb324d61e4da` |
| Program source | `329bfdbf75a62f5dd8f71eb7fcf4c4df33ef40f336adea1e5e8ca5bce3e1c6cc` |
| Probe DLL | `bfc7a672e5b576336c41caf2ae25b2af4aa4bad6327fa48fa0b56f5fd3d2091d` |
| Runtime manifest | `b1ee70e7dff10cce88b92f4045b9127d57eab694aaa3d34310ba71cc6f004d03` |
| App EXE | `8d664cca64c925836ce2303fa75a546210682c0981e9fc7b4406f64bdfa7c39c` |
| App DLL | `11a9e247e97dbf9eeb5c630ebf7d2631e699a8a76a17cfe1d2e79899467896a6` |
| Rust worker | `eeb2515a48546c9b50f9c8221f334c028a9dadce34cb1fa32ca7c5d477e87430` |
| Result JSON | `7f4b4db02e95f867512fbbb4e22475479caa39bac75ff84f3538ee8bbc24fea1` |
| Invocation receipt | `584861480f1bc6f291dfad6894ec555b257932a25ef791da70ee2d27ad852e97` |

## Closeout

The owned control passes its integration question. Source/outcome integration
and full CI for the new helper remain pending. No new dependency, model,
font payload or package was added; source is Apache-2.0 and the existing
reviewed OCR notices remain in use. Preview 0.4.58 is unchanged.

All local execution/review consumers have finished. After the source and compact
outcome are pushed and CI finishes, retire its PNG, binding manifest, raw reports
and probe compiler outputs along with the closed preceding App Release/test
outputs. Rebuild the helper and generate a matching runtime manifest from the
listed schema to reproduce on a deliberately selected runtime; the retired
manifest is historical evidence, not a current dependency. Preserve the sole
package, current models, Debug/Data, owned native-QA inputs, protected runtime
checkpoint, original research files and independent evaluation boundaries.
