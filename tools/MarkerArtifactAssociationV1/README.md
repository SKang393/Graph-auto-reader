# Marker artifact association v1

This isolated Python utility is a train-split, review-only diagnostic. It accepts already authenticated source-pixel candidate and truth records, and returns aggregate status counts without case-level output. It never reads files or data itself.

Only typed `legend_symbol` artifact truths with per-glyph single-draw ablation evidence can be matched as `matched_artifact_review_only`. `produce_legend_symbol_visibility` generates a fixed train scene in memory, verifies its pinned renderer/font source bundle, renders the scene twice, and suppresses exactly one unique legend marker draw on the second pass. It derives the expected binding from that renderer trace, source scene, annotation, and raster output. The association entrypoint requires a capture key from the separately stored `capture_manifest.json`, whose exact file SHA is pinned in `association.py`; it rechecks the source ID, dimensions, original PNG SHA, current producer source, current renderer bundle, font identity, and each artifact binding against that manifest before matching. It then recomputes rendered-plane SHA, scene/annotation/glyph identity, draw ID, ablated-plane bytes, and actual changed pixels. A caller that forges its raster, proof, and binding consistently still fails the fixed capture manifest check. The producer/manifest are integrity-pinned within this new package, not independently signed or attested; the package itself must be reviewed and externally pinned before use.

Matching uses the existing inclusive Euclidean 2-pixel rule jointly across positive marker truths and artifact truths. A proposal that collides with either side, has duplicate/ambiguous links, overlaps a generic overlay, is a symbol input, is not a detector candidate, or comes from incomplete capture cannot become an artifact match. Generic overlays retain suppression precedence. Connected and isolated legitimate markers remain positive review outcomes. Unmatched proposals remain unknown. Other semantic negatives, including arrows, ticks, endpoints, dividers, and absent annotations, are not admitted.

Every result reports zero negative training rows, zero trainable rows, `training_approved=false`, and zero optimizer steps. It does not adapt or register the existing corpus schema and has not been run against real truth. Synthetic-only tests:

The pinned capture manifest currently admits exactly one fixed synthetic source and one legend glyph. The `associate_full` 64-source aggregate shape checks source/truth identities and emits review counts, but it does not authenticate or provide 64 artifact-capture manifest entries; it must not be read as 64-case artifact evidence.

```powershell
python -m pytest tools/MarkerArtifactAssociationV1/test_association.py tools/MarkerArtifactAssociationV1/test_producer.py -q --basetemp artifacts/goal22-runs/marker-artifact-association-tests-v1
```
