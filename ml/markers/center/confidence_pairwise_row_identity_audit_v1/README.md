# V37 train-row identity audit

This metadata-only audit checks whether the 276,618 original identity rows used
by V37 can be traced to their train-only synthetic source scenes. V37 feature
arrays do not store source scene IDs. The tool reconstructs contiguous row
ranges using the exact V27 scene/proposal ordering and the per-scene source
inventories for the coverage, crowded, and mined-negative supplements.

It binds the V35/V37 reports by SHA without parsing their JSON, then reads only
the train proposal metadata and sidecars. It does not open tensor or feature
payloads, dev/private/sealed data, create a model, or run an optimizer. Thus this
is a row-order and metadata identity audit, not a pixel-to-feature
recomputation or a proof that every saved tensor slice matches its sidecar.

For component scenes, IDs include the generator's stable train seed. Family
rows use the frozen V25 train-panel ordinal and the V27 mined scene-tensor hash;
the ordinal is only meaningful under the pinned family binding. Coverage and
crowded source cases use their deterministic sample IDs and per-case raster,
tensor, patch, and label hashes. Mined negatives use each train scene ordinal,
source tensor hash, and selected proposal indices.

Run from the project root:

```powershell
tools\gpu-training\venv\Scripts\python.exe -B -m ml.markers.center.confidence_pairwise_row_identity_audit_v1.audit --output artifacts/goal22-runs/marker-center-v37-row-identity-audit-v2/report.json
```

The output path must be fresh and under `artifacts/goal22-runs`. Tests include
synthetic fixtures and an optional integration check against the pinned
train-only metadata when those local artifacts are present.
