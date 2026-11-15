<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Fixed 0.4.51 prepared calibration diagnostic

## Purpose and frozen candidate

The bottom-corner role correction has an owned failing-before regression and an
[actual packaged-worker narrow/wide proof](BOTTOM-CORNER-WORKER-451-DIAGNOSTIC-20261114.md).
This separate route ran one authorized fixed aggregate confirmation, not model
or threshold selection. No completed private run was repeated or used for tuning.

Candidate source: `e54917917d1acdc3aed2c556f3d2faf0bcbff253`.
Retained package root:
`E:/Graph-auto-reader/artifacts/dev-portable/builds/0.4.51-20261114T235320548Z-e5491791`.
All 21 runtime identities were read from that verified package and fixed in
`tools/GraphReader.CalibrationOcrProbe/RealDevPrepared451CalibrationCheck.cs`.
The existing shared verifier also holds exact asset/notice streams and checks
loaded managed bytes before private payload selection.

Key SHA-256 identities:

- App EXE: `144d4cfab07d81429de6366a76a211fe515fa8227578b9c857ed76e5cca6efed`
- App DLL: `5be01923609dfc144f407db353bc7097f47c773bc44140b87af04d387ce041ff`
- Rust: `efed45d5c1de473599cf94ace56aa1e1b3bfd40228289db23ecce755b66e9794`
- OCR model: `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`
- Manifest: `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a`
- Apache model license: `c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4`
- Model notice: `8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729`

## Executed command and unchanged protocol

```text
GraphReader.CalibrationOcrProbe.exe --real-dev-prepared-calibration-451-check "E:/Graph-auto-reader/data/manual data" <package-root>/GraphReader.App.exe <package-root>/review-assistance/ocr <package-root>/native/graphreader-engine.exe --explicit-opt-in
```

`package-root` is the exact retained directory above. The executable was
`<active-worktree>/artifacts/corner-tick-worker-451-check/tool/GraphReader.CalibrationOcrProbe.exe`.
Hidden `Start-Process` redirected stdout/stderr with a 920-second external watchdog.

The fixed denominator remains all 120 real-dev projects, using the existing
frozen assignment/inventory checks and locked source reads. The shipped panelizer
runs before the shipped Rust-first/native-fallback axis service and persistent
CPU OCR worker. Every returned panel is attempted without truth-based selection.
Only genuine no-panel detection permits full-image fallback. Crop offsets map
panel coordinates to source coordinates exactly once. Missing, unsupported,
failed, incomplete and timed-out work remains in source/panel accounting.

Output remains aggregate-only, below 1 MB: source identity digest, distinct image
and project counts, panel counts, reasons, geometry/OCR/numeric coverage, complete
proposals, timings and sampled memory. No private case identifiers, paths, text,
boxes, predictions, truth residuals or pixels are emitted. There is no Apply,
export, training, model/threshold change, sealed read or promotion claim.

The existing independent half-core parent/worker budgets, BelowNormal priority,
45-second source and 900/905-second overall deadlines remain unchanged. Historical
449/450 CLI routes, exact pins and defaults remain intact. No new dependency or
license is introduced.

## Validation and outcome

The serialized probe-only build into
`artifacts/corner-tick-worker-451-check/tool` passed in 7.7910078 seconds with
zero warnings/errors. All 21 pinned runtime libraries were restaged and checked;
the EXE, Rust, model, manifest and two license/notice hashes also matched.
No models or whole package were copied. Probe DLL SHA-256:
`1ec09e635a8a116fac12d9dc6cd1bf164b41416e9cf0cbef8ced2e4e7a0ac277`.

`--real-dev-calibration-self-test` exited 0 in 0.7445316 seconds with empty stderr,
four owned funnel checks, two affine checks, forbidden output rejection,
capability rejection before selection, fixed denominator 120 and zero payload
reads. Its report SHA-256 is
`d022bb4a4eeea324aa7e10f30b30d5640dc8259ae2bde14fedbbcae71d1528c3`.

The authorized confirmation ran from 2026-11-15 00:30:10.1049473 UTC to
00:33:19.5259450 UTC, exit 0, empty stderr: 189.4155584 seconds process,
188407.3688 ms pipeline. All 120 payloads were hashed, covering 118 distinct
embedded images. Preparation returned 15 genuine no-panel fallbacks, 98
single-panel sources and seven multi-panel sources: 112 returned panels plus 15
full-image fallbacks, all 127 attempted. There were zero unattempted,
unsupported, processing-error or incomplete cases.

Results: **50 geometry/OCR panels, 367 numeric rows, zero measured or estimated
complete proposals**. These coverage and reason counts match the recorded
0.4.50 prepared confirmation, without establishing a private case-level cause.
Overlapping reasons: axis ambiguous 73; native unavailable 4; insufficient pairs
43; unpaired label 33; invalid numeric label 17; inconsistent spacing 5; invalid
range 3; duplicate association 2; nonmonotonic 2; ambiguous association 1; zero
not observed 1. All 120 fully processed projects had no complete proposal.

Preparation median/p95 was 27.9623/360.1075 ms. Parent budget verified 12 logical
processors, rate 416, 0.4992 logical cores and silent breakaway; child runtime
responses retained their independent verified CPU budgets. Peak sampled combined
memory was 496,881,664 bytes, not a hard memory ceiling. No deadline was reached.

The unchanged assignment/inventory hashes were
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a` /
`03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`.
Complete source digest:
`bbe77dd52fc4a08dd2e2a039de234677bbf0e9267d6b812864c8e87e16e5931f`.
Aggregate report, 4434 bytes:
`c78f77995c5457be641f3a9f0aa522c84275712c5c8d4524dffc9c1b9caf34cc`.

## Retention and limits

`artifacts/corner-tick-worker-451-check` now holds 64 files / 42,716,164 bytes,
including the completed corner proof and follow-up build/self-test/aggregate
records. The owned probe exited and disposed its worker; this diagnostic's
package consumer is finished. Preserve the sole 451 package, original corpus,
SSH/native QA inputs and other tasks' outputs. Retire this disposable subtree
after both outcome records are pushed and remaining integration consumers finish.

Execution completed; **automatic calibration readiness remains FAIL**. No
independent accuracy, private case cause or production approval is claimed.
