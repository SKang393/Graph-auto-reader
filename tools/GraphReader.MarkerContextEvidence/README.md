# Marker context crop parity diagnostic

This diagnostic captures `MarkerPatchExtractor` output from deterministic synthetic frames and compares it to the pure-Python reference in `python/marker_context_reference.py`.

The serialized `OcrMask` and `ArtifactMask` values are provenance-only snapshots. Neither the production classifier patch sampler nor the Python reference reads them, so this diagnostic does not establish OCR-mask or artifact-mask preprocessing parity. The patch tensor is derived from the channel-first image planes and, when supplied, measured original-pixel content bounds.

The default comparison uses 32x32 patches with radius scale 2.25. The expanded-context comparison keeps the 32x32 tensor shape and changes radius scale to 4.5, isolating crop context from model input dimensions. A separate 65x65 case exercises an expanded spatial tensor for future architecture exploration. No model inference, scoring, optimizer, annotations, private data, or sealed data are used.

The companion production-request diagnostic parses the pinned classifier manifest and asserts its declared 32x32 input, tensor names, and identity normalization. It manually supplies the radius scale and other sampling values drawn from the current source defaults when constructing the adapter options. It does not invoke or verify the normal model-store factory: that path requires a resolved model payload, which is absent in this checkout. The lower-level extractor accepts explicit larger-context options, which this tool tests; those cases do not establish factory support, production approval, or rollout of a different model contract.

Tests invoke the bare `python` executable from `PATH`. The interpreter and version are not pinned. If `python` cannot be started, cross-language parity tests are inconclusive; C# sampler checks and source checks still need to be run separately.
