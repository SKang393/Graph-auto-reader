# Marker candidate label curation v1

This isolated package derives review-only candidate labels from the fixed
synthetic train artifacts. It does not read development, private, sealed, or
test inputs, and it never constructs an optimizer.

## Label rules

- A `plot_marker` candidate must mutually match exactly one annotated marker
  within 2 pixels. Its marker ID and connected/isolated subtype must agree.
- A `visible_legend_symbol` candidate must mutually match exactly one
  annotated glyph within 2 pixels. The separate artifact audit must bind the
  glyph ID, changed-pixel evidence, distinct rendered/ablated hashes, and
  visibility proof hash.
- A positive candidate within 2 pixels of any annotated glyph is ambiguous.
  Marker/glyph collisions, multiple candidates for one truth center, an
  overlay-center match, or a wide crop intersecting the generic overlay/cue
  leave the candidate unlabeled.
- All producer unknown/excluded statuses remain unlabeled; none can be
  overridden by a geometric match. Unknowns and generic overlays never become
  negative examples. Duplicated complete local+wide pairs remove involved
  labeled rows, while shared single views with different context remain
  distinct. Visible cues require a boolean visibility flag and a bounded box;
  invisible cues must have null box and text. Crop checks include one source
  pixel for bilinear neighbor support.

## Split policy

The split unit is scene ID grouped by its declared family signature. Recipe
cells and seeds do not count as independent families. Both proposed classes
must appear in both assigned partitions. A successful assignment is named
`assigned_signature_partition_review_only`; it is bookkeeping only. Distinct
declared keys do not prove independent rendering code, bytes, assets, or
historical visual independence. Therefore every candidate remains
`training_eligible=false` and `training_eligible_rows=0`, even when a partition
can be assigned. The current 64-source evidence has one shared signature.

The next technical step is to create additional genuinely different renderer
families, bind the actual implementation and assets that produce each family,
then regenerate capture and visibility evidence. One possible measurable
experiment threshold is ten distinct families, both labels in each, and an
8/2 whole-family assignment. This is only a planning target, not a product
gate or proof that the families are independent.

## Inputs and run

The caller prepares an immutable `graphreader.marker-label-curation-input-binding.v1`
JSON with the exact paths in `curator.INPUT_PATHS`, SHA-256 for every input,
and the exact source closure in `curator.SOURCE_PATHS`. The prepared descriptor
is `artifacts/goal22-runs/marker-label-curation-binding-prep-20261031-v2/input-binding.json`;
its independently recorded SHA is in the adjacent preparation receipt. The tool
checks the full corpus completion and raster manifest, capture and artifact
receipts, all 64 source joins, source code hashes, all candidate tensor files,
and source/candidate denominators before it writes a fresh report.

Run from `E:\Graph-auto-reader` after the binding has been reviewed:

```powershell
python -m unittest ml.markers.candidate_label_curation_v1.test_curator -v
python ml/markers/candidate_label_curation_v1/curator.py `
  --binding E:\Graph-auto-reader\artifacts\goal22-runs\marker-label-curation-binding-prep-20261031-v2\input-binding.json `
  --binding-sha256 $PreparedBindingSha256 `
  --output E:\Graph-auto-reader\artifacts\goal22-runs\marker-label-curation-train-v1
```

Set `$PreparedBindingSha256` to the binding digest recorded by the preparation
receipt. The bound validation and curation run completed against the pinned
64-source synthetic train inputs. Its
aggregate was 1,214 `plot_marker` and 30 `visible_legend_symbol` proposals among
2,022 candidates; 547 remained unknown/excluded and 231 were collision or
mismatch. It found one declared family signature, so the partition is blocked;
all rows remain ineligible and optimizer steps remain zero. The original v1
preparation binding is preserved as historical failed plumbing evidence.

The persisted report is `artifacts/goal22-runs/marker-label-curation-train-v1/report.json`
(SHA-256 `9fb89258659ec838a69c2c6834318f4120c45f0a2a9212273e8843340d71fd22`).
Its independently checked receipt has SHA-256
`3677822a8fdf50bce07c0fd49e25791605a09de0d4f67f0cab09a7808130689c`.

The output remains a curation proposal. It sets `training_approved=false` and
`optimizer_steps=0`; it does not change model manifests or training admission.
The proposed labels are for an exploratory two-view context experiment and do
not map to existing production classifier class indices or assign class 13.

## Evidence limits

The stored tensors are raw sampler outputs with local `[1,32,32]` view at
radius scale 2.25/minimum half extent 4 and wide `[1,32,32]` view at scale
6.75/minimum half extent 12. Production factory and normalization parity are
not established. Visibility evidence is cross-checked by hash and metadata but
not rerendered by this curation tool. Scene grouping is not participant or
study grouping. No family-independent validation is presently available.
