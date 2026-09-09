<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Synthetic runtime seed evidence

This local command-line diagnostic sends annotation-free project-owned train/dev
PNGs through the application's real raster decoder, OpenCV axis fitter, official
DB detector, graph-consensus detector, recognizer, and seed-mask composer.
Recognition reads the original raster; detection uses the same axis-masked
derivative as application composition. Model and manifest bytes are checked
before executable CPU preflight. Both candidate adapters remain unapproved.

The input exchange is created by
`python -m ml.markers.center.mask_preserving_v24.export_family_rasters`.
It carries PNG bytes and identities only. The diagnostic rejects sealed split
names, annotations, extra manifest fields, precomputed masks, duplicate image
names, changed bytes, and nonlocal image paths. It never reads the renderer's
annotations or the private acceptance corpus.
The exporter serializes the policy's `dev` split as the existing generator's
`validation` name; this is the same split, not a fourth evidence split.

## Local execution

1. Export a synthetic `train` or `dev` exchange to an ignored directory using
   the module above with `--output-root`, `--split`, and `--seed` (default 393).
2. Run `prepare_official_metadata.py --root <primary-repository> --output
   <new-ignored-metadata-directory> --native <existing-OpenCvSharpExtern.dll>`.
   It verifies the existing official model bytes, alphabet, and Apache notices.
   The generated manifests satisfy the frozen model-manifest schema but remain
   local diagnostic metadata. Nothing is added to the production model store.
3. Compile this project with `dotnet build -c Release`. This is a diagnostic
   compiler output, not a packaged application build.
4. Invoke the resulting DLL with four positional arguments:
   `<input-manifest.json> <candidate.json> <candidate-sha256> <new-output-directory>`.
   The metadata command prints the candidate SHA-256. Existing output folders
   are refused, so a new run cannot overwrite earlier evidence.
   Outputs must remain under this checkout's ignored `artifacts/` directory.

The native DLL is loaded from its exact checked path and kept read-locked for
the process lifetime. Reports record its checksum and approval scope, plus the
executed application, axis, OCR, inference, and tool assembly checksums. The
existing development OpenCV binary can be used for diagnosis, but its result is
not evidence for the separate reviewed source-runtime binary.

## Limits

Successful cases emit little-endian float32 OCR and **geometry seed** planes,
original Gray8 bytes, exact checksums, and actual axis/OCR evidence in original
pixels. Failed cases retain their error and produce no substitute mask.

These outputs are deliberately marked `training_input_ready: false` and
`complete_artifact_mask: false`. A residual arrow/bracket/legend/intersection
artifact provider is still required before the planes can represent the full
marker runtime input. A successful exit means every requested seed computation
completed. It does not mean accuracy passed, any model was approved, or normal
Production composition is available. No CSV acceptance or sealed evaluation is
performed by this tool.

When OCR returns no regions, the failure report also records independent raw DB
and connected-component proposals for diagnosis. They are never substituted
into the result or passed to the seed composer as accepted text.

`Test-InputBoundary.ps1` exercises rejection before inference using copied
synthetic inputs. Supply `-ToolPath`, `-InputManifestPath`, `-CandidatePath`, and
`-CandidateSha256`; its nine checks keep reports under ignored test scratch.

The proposed pre-OCR structural provider is not enabled here. Its focused unit
fixtures must not be mistaken for the required representative text-preservation
gate. Truth remains exclusively in a separate synthetic evaluator.
