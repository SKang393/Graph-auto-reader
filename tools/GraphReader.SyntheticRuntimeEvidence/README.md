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
executed application, axis, OCR, inference, PDF/panelization, and tool assembly checksums. The
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

Every panel records independent raw DB and connected-component proposals for
diagnosis, bound to the exact detector input. A separate DB pass on original
panel pixels measures the effect of axis masking. These additional diagnostic passes
do not substitute proposals into the result or pass them to the seed composer
as accepted text. Their cost is included in diagnostic timing. Zero-region cases
retain the historical empty-OCR diagnostic field as well.

Configured OCR identities are recorded separately from actual execution
envelopes. A successful zero-crop result requires both configured models, but
records detector execution only because the recognizer did not run. Its OCR
mask is all zero; this records the detector result without asserting that the
image contains no text. The OCR pipeline has its own subrun UUID; adapter
envelopes share the parent workflow run, project, panel, and input identity.

The separate `ml/markers/center/mask_preserving_v24/runtime_binding.py` loader
validates explicitly frozen train/dev report, source, binary, model, crop, and
plane identities. It regenerates project-owned sources before joining marker
labels, rejects lost or multiply mapped markers, and preserves a non-marker
omission audit. Only a separately validated binding can enable experimental
training input; diagnostic completion and model approval remain distinct.

`Test-InputBoundary.ps1` exercises rejection before inference using copied
synthetic inputs. Supply `-ToolPath`, `-InputManifestPath`, `-CandidatePath`, and
`-CandidateSha256`; its fourteen checks keep reports under ignored test scratch.

The optional checksum-bound candidate field `ocr_output_geometry` accepts
`model_polygon` (the default) or `matched_component`. The latter is the
preregistered unapproved component-geometry experiment; the report records its
distinct OCR adapter identity. It cannot enter the production factory. Unknown
geometry values are rejected before inference or output creation.
The experimental mode also requires `geometry_protocol` with the exact protocol
path and SHA-256. That declaration must bind the input manifest to train or dev,
reference the shared evidence policy, and permit zero sealed reads on this route.
The tool pins the reviewed declaration's SHA-256 independently of the candidate
and parses the same bytes it hashes, preventing a caller-created replacement
declaration from authorizing this experiment.

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


## In-memory full OCR metrics

`--self-test-original-db-ocr-aggregate` runs model-free metric checks. Windows CI
builds this tool and runs these checks. It does not generate a packaged build.

`check_original_db_ocr_aggregate_parity.py --executable <tool.dll> --output
<new-report.json>` compares 197 deterministic fixtures against the preserved V2
Python scorer. It checks raw detection and recognized geometry separately,
full-denominator exact text and roles, Unicode-scalar edit distance, and all
unmatched text insertions/deletions. It requires the existing Python evaluation
environment; it does not install dependencies or load model weights.

The bounded stdin command `--score-synthetic-ocr-metric-fixtures` supports this
comparison using model-free fixture JSON and emits aggregate metrics only.
It is not a sealed archive reader or a Production approval entry point.
The aggregate scorer reduces each source immediately and retains no source
identities, text, boxes, or predictions. A future sealed worker must still
perform canonical first-read accounting and use actual application-derived
plot geometry before these metrics can support model approval.
