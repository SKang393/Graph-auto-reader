<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Synthetic runtime seed evidence

This local command-line diagnostic sends annotation-free project-owned train/dev
PNGs through the application's normal image import and raster panelization path,
then sends every actual imported panel through the real raster decoder, OpenCV
axis fitter, official DB detector, graph-consensus detector, recognizer, and
seed-mask composer.
Recognition reads the original raster; detection uses the same axis-masked
derivative as application composition. Model and manifest bytes are checked
before executable CPU preflight. All candidate adapters remain unapproved.

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

The v2 report has one case per manifest source. A source is `panels-completed`
only when it imported at least one panel and every panel completed seed
composition. Root `count`, `completed`, and `failed` count source images;
`panel_count`, `completed_panels`, and `failed_panels` count imported panels.
An import failure retains a failed source case with an empty panel list. A
failure in one panel is retained without preventing the other imported panels
from running.

Each panel records the normal workflow panel ID, crop checksum and dimensions,
full-source checksum and dimensions, requested and encoded source crops, both
translation matrices, import warnings, and an exact copy of the imported panel
PNG. Evidence files are placed under `<output>/<source-sha256>/<panel-id>/`.
The tool validates the retained full-source checksum and dimensions against the
input manifest before running inference on a panel. The exact immutable panel
bytes are passed to the detector without resizing or substituting geometry.

Successful panels emit little-endian float32 OCR and **geometry seed** planes,
panel-local Gray8 bytes, exact checksums, and actual axis/OCR evidence in panel
pixels. A separate raster algorithm supplies a candidate residual mask.
The `composed-artifact-candidate.f32` plane contains that residual mask unioned
with the geometry seed. Its report includes every source envelope; the residual
algorithm envelope alone must not be credited for seeded geometry pixels.
The algorithm carries a null model identity and binds its App assembly plus
configuration hash, including the exact OCR assembly dependency.
Failed cases retain their error and produce no substitute accepted mask.

These outputs use schema `graphreader.synthetic-runtime-seed-evidence.v2` and
remain deliberately marked `production_approved: false`,
`training_input_ready: false`, and `complete_artifact_mask: false`. The missing
stage remains `representative-artifact-validation-and-training-input-binding`.
The residual prototype still needs representative accuracy evidence and an
independently controlled training-input binding. A successful exit means every
requested panel diagnostic completed. It does not mean accuracy passed, any
model was approved, or normal Production composition is available. No CSV
acceptance or sealed evaluation is performed by this tool.

When OCR returns no regions, the failure report also records independent raw DB
and connected-component proposals for diagnosis. They are never substituted
into the result or passed to the seed composer as accepted text.

`Test-InputBoundary.ps1` exercises rejection before inference using copied
synthetic inputs. Supply `-ToolPath`, `-InputManifestPath`, `-CandidatePath`, and
`-CandidateSha256`; its nine checks keep reports under ignored test scratch.

The pre-OCR structural provider emits descriptive marker-like and connector
planes before OCR. They are **not applied to OCR input**. Its focused unit
fixtures must not be mistaken for the required representative text-preservation
gate. `score_family_structure.py` checks the saved planes against independently
regenerated dark ink inside glyph boxes. These explicitly labeled proxy metrics
include possible overlapping graph ink and cannot satisfy an acceptance gate.
Preservation recall is conditional on ink left after the existing geometry
exclusion; it does not count text removed by that earlier geometry step.

`score_family_ocr.py` independently regenerates the exact PNG identities and
scores final recognized region boxes using the existing fixed IoU 0.5 matcher.
Tight glyph boxes can penalize legitimate padded detections; the score is not
a raw-detector-only recall metric. Failed cases remain misses. Neither scorer
selects a threshold, reads private or sealed data, or approves a candidate.
Truth remains exclusively in these separate synthetic evaluators.

Evidence files are written in cancellation-aware chunks to a temporary sibling
and moved into place only after completion. Cancellation removes the writer's
own in-progress file. Earlier complete diagnostic files remain as an incomplete
run record without a final report; use a fresh directory when rerunning.
