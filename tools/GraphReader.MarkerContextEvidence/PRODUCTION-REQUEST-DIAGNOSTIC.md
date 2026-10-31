# Production classifier request capture

The companion test project exercises the real `ProductionMarkerClassificationAdapter` and `MarkerClassificationService` with a synthetic frame and a capture-only inference runner. The runner records `InferenceRequest` and returns a failure before model/provider execution. The adapter test intentionally observes the resulting failure; it does not mark the synthetic adapter as a real approved model or produce classification evidence. The adapter constructor receives options explicitly assembled by the test from the pinned classifier manifest, rather than calling the model-store factory.

The manifest test parses and pins `models/manifest/markers/graph-marker-classifier-0.1.0.json` at SHA-256 `FB0AEBDE5785E760C8C10BCF7FDB2C04DBA7E63315D16DDCB6DBC6B25D4AF312`; it asserts the NCHW `[N,1,32,32]` input, names, and identity normalization. The adapter test manually supplies options assembled from those manifest fields and explicit source-derived sampling values: batch size 64, radius scale 2.25, minimum half-extent 4, and zero ink padding. These assertions cover manifest parsing and the supplied-options mapping, not execution of the normal factory. The test does not invoke the private `ReadOptions` parser or `Create` model-store factory: `Create` requires a `ResolvedProductionModel` whose constructor is internal, and the referenced classifier ONNX payload is absent from this checkout. The source-level basis for the factory settings is `ProductionMarkerClassificationAdapter.ReadOptions` and `MarkerClassificationOptions` defaults. No claim is made that the production factory was verified.

The diagnostic also compares two experimental lower-level views: a 32×32 context view at radius scale 6.75 and minimum half-extent 12, without symbol-bound whitening, and a 65×65 view at radius scale 4.5. Neither experimental view uses `ProductionMarkerClassificationAdapter`, which currently requires 32×32 and configures radius scale 2.25.

The captured classifier input is compared to the independent Python sampler using the synthetic grayscale frame and measured original-pixel content bounds. OCR and artifact mask planes are retained only as provenance snapshots and are deliberately excluded from the classifier sampler comparison. This diagnostic does not test the production raster decoder, automatic-detection adapter wiring, mask composition, marker-center tensor, ONNX execution, model accuracy, or production approval.

Run on Windows from the repository root:

```powershell
dotnet test tools/GraphReader.MarkerContextEvidence/tests/production-request-tests/GraphReader.MarkerContextProductionRequest.Tests.csproj --configuration Release --verbosity minimal -m:1 -p:NuGetAudit=false -p:UseSharedCompilation=false
```

The test invokes bare `python` from `PATH`; its interpreter and version are not pinned. If unavailable, cross-language parity is inconclusive while the C# request capture assertions remain active.
