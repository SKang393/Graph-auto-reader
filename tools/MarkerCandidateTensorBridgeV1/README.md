# Marker candidate tensor review bridge v2

This isolated tool joins the saved full-capture panel's inline local and
wider-context sampler tensors to train-only positive marker geometry and
visible legend glyph evidence. It is a review materializer, not a trainer or a
model admission path. Every output row has `training_admitted=false`; the
summary fixes trainable rows, negative rows, optimizer steps, and training
approval to zero/false.

## Intended input contract

The public caller supplies the canonical E project root and either one bound
train source or the fixed 64-source batch descriptor. Before importing the
label classifier, the entrypoint checks the pinned source closure and rejects
reparse points. It verifies complete receipt, identity metadata, capture
report, truth sidecar, artifact report, panel JSON, and image bytes. It derives
labels from the complete sidecars; it does not accept caller-supplied positive,
glyph, or overlay inventories.

The full 64-source metadata/truth/artifact inventories must bijectively join.
Every source annotation is classified and its legend glyph inventory, visibility
records, and review-only overlay are checked before materializing the selected
panel. The output carries a digest of the full derived inventory and of the
verified source closure. Group identity is derived as `source_id`; participant
or study-level independence is not established.

Matching is the existing inclusive 2 px Euclidean source-pixel join. Marker
truths and all valid visible glyphs share one joint one-to-one match graph.
Only artifact glyphs with `matched_artifact_review_only` status can receive an
artifact review label; any nearby valid glyph, even unmatched, makes a positive
collision ambiguous. Duplicate centers, input symbols, generic overlays,
collisions, capture failures, and unmatched candidates never receive labels.
Isolated legitimate markers remain positive markers. Unmatched proposals are
unknown, not negatives.

## Sampler boundary

The local tensor is the raw 32x32, one-channel `MarkerPatchExtractor` output at
radius scale 2.25/minimum half extent 4. The wide tensor is a separate raw
32x32 output at radius scale 6.75/minimum half extent 12. Both use padding 0.
The bridge verifies the source-plane and mask snapshot hashes, candidate order,
and each inline tensor hash against float32 little-endian bytes. It validates
the saved local content bounds and the fixed sampler options/source. The masks
are provenance only; they are not model inputs. The separate synthetic
`MarkerContextEvidenceCapture` adapter remains supported by the lower-level
API, but the full-capture path does not require one evidence JSON per view.
Classifier mean/scale normalization is not applied. The ordinary model store
factory, production raster decoder, ONNX inference, and normalization path are
not claimed as verified by these captures.

## Execution boundary

A reviewed single-source run for seed 20820000 completed earlier and remains
separate evidence. The new batch path below has not been run against the real
64-source truth/capture set. Its tests use a two-source synthetic fixture with
the inventory derivation mocked, and assert the derivation is called exactly
once. No dev, private, sealed, or test data is accepted, and no
training/admission is implemented. The 30 review-only glyph matches are not
claimed as a sufficient training set.

Run only the focused synthetic tests with:

```powershell
python -m unittest tools.MarkerCandidateTensorBridgeV1.test_bridge
```

## One-source runner

`runner.py` accepts a descriptor with this exact shape:

```json
{
  "schema": "graphreader.marker-candidate-tensor-bridge-run.v1",
  "source": { "...": "the complete source binding object" },
  "inputs": {
    "panel_capture": "project-relative/panel.json",
    "source_raster": "project-relative/source.png",
    "panel_raster": "project-relative/panel.png",
    "complete_receipt": "project-relative/COMPLETE.json",
    "identity_metadata": "project-relative/identity-metadata.json",
    "capture_report": "project-relative/capture-report.json",
    "truth_sidecar": "project-relative/truth.json",
    "artifact_report": "project-relative/artifact-report.json"
  }
}
```

All paths must be project-relative regular files without reparse ancestors.
The runner accepts only the canonical E project root and a fresh direct child
of `artifacts/goal22-runs`. It writes `review-rows.json`, separate hashed raw
local/wide tensor files, and `receipt.json` using exclusive creation. The
receipt records the descriptor, every input, source closure, tensor manifest,
and output hashes. The descriptor's `source_group_id` must equal its bound
`source_id`; this is source-level grouping only.

The current pinned synthetic inputs are located at:

- `artifacts/goal22-runs/marker-context-bound-input-full-capture-v4/report.json`
- `artifacts/goal22-runs/marker-context-train-corpus-v5-full/identity-metadata.json`
- `artifacts/goal22-runs/marker-context-train-corpus-v5-full/COMPLETE.json`
- `artifacts/goal22-runs/marker-context-train-corpus-v5-full/post-capture-truth/truth.json`
- `artifacts/goal22-runs/marker-artifact-v5-train-full-analysis-v2/report.json`
- source PNGs under `artifacts/goal22-runs/marker-context-train-corpus-v5-full/raster-only/`
- inline panel JSON files under `artifacts/goal22-runs/marker-context-bound-input-full-capture-v4/panels/`

One descriptor for synthetic train seed 20820000 was prepared and used in a
completed review-only run. Its historical runner protocol SHA-256 was
`65991c1338110f03a47dd5ee0cd9f5c8a27020b8d7a885442aa44867f348688e`.
The historical invocation was:

```powershell
python -m tools.MarkerCandidateTensorBridgeV1.runner `
  --descriptor artifacts/goal22-runs/marker-candidate-tensor-bridge-one-source-prep-v1/source-descriptor.json `
  --output artifacts/goal22-runs/marker-candidate-tensor-bridge-seed-20820000-v1 `
  --expected-runner-protocol-sha256 65991c1338110f03a47dd5ee0cd9f5c8a27020b8d7a885442aa44867f348688e
```

## Bounded 64-source batch runner

`--mode train-batch` uses fixed project-relative corpus, identity, capture,
truth, and artifact paths from `runner.py`. The caller cannot substitute those
inputs. Its descriptor has only the exact sorted source IDs:

```json
{
  "schema": "graphreader.marker-candidate-tensor-bridge-batch.v1",
  "source_ids": ["the 64 exact sorted source IDs"]
}
```

The runner verifies the complete inventories once, checks the descriptor IDs
against that derivation, then handles one panel and its candidate tensors at a
time. Each source receives a separate output folder or a failure record;
captured candidate totals remain distinct from successfully materialized
candidate totals. A failed source does not remove its denominator. Per-source
tensors are written to a temporary subfolder before that folder is renamed into
place. Review rows are not training data, and the receipt fixes training and
negative-row counts to zero.

The original batch was stopped after a separately verified fast batch completed;
its partial output is retained. The original batch command shape, using a fresh
output directory and a separately reviewed descriptor, is:

```powershell
python -m tools.MarkerCandidateTensorBridgeV1.runner `
  --mode train-batch `
  --descriptor artifacts/goal22-runs/marker-candidate-tensor-bridge-batch-descriptor-v1.json `
  --output artifacts/goal22-runs/<new-output-directory> `
  --expected-runner-protocol-sha256 b511b49b37490f5c21c7929d2da52818cd8c1e459baabaf0dbdd6d02c0ff7119
```
