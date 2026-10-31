# Marker context crop parity diagnostic

This diagnostic captures `MarkerPatchExtractor` output from deterministic synthetic frames and compares it to the pure-Python reference in `python/marker_context_reference.py`.

The serialized `OcrMask` and `ArtifactMask` values are provenance-only snapshots. Neither the production classifier patch sampler nor the Python reference reads them, so this diagnostic does not establish OCR-mask or artifact-mask preprocessing parity. The patch tensor is derived from the channel-first image planes and, when supplied, measured original-pixel content bounds.

The default comparison uses 32x32 patches with radius scale 2.25. The expanded-context comparison keeps the 32x32 tensor shape and changes radius scale to 4.5, isolating crop context from model input dimensions. A separate 65x65 case exercises an expanded spatial tensor for future architecture exploration. No model inference, scoring, optimizer, annotations, private data, or sealed data are used.

The production manifest factory currently requires a 32x32 classifier input and leaves radius scale at 2.25. The lower-level production extractor accepts explicit larger context options, which this tool tests. This diagnostic does not imply production support, approval, or rollout of a different model contract.

Tests invoke the bare `python` executable from `PATH`. The interpreter and version are not pinned. If `python` cannot be started, cross-language parity tests are inconclusive; C# sampler checks and source checks still need to be run separately.
