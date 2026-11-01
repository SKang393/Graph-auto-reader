// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Inference;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionLocalCandidateEvaluationAdapterTests
{
    private static readonly string[] ExpectedOcrTasks =
        ["ocr_detection", "ocr_recognition"];
    private static readonly string[] ExpectedDetectionOnlyOcrTasks =
        ["ocr_detection"];

    [TestMethod]
    public async Task AxisCandidateEvaluationUsesProductionCoreWithoutApprovingAdapter()
    {
        TestInputs inputs = CreateInputs();
        var candidateDetector = new AxisDetectorStub();
        var candidateProvider = new LineCandidateProviderStub();
        var candidate = new ProductionAxisGeometryAdapter(
            new string('a', 64),
            isApproved: false,
            candidateDetector,
            candidateProvider,
            new RasterDecoderStub(inputs.Raster));
        var approvedDetector = new AxisDetectorStub();
        var approved = new ProductionAxisGeometryAdapter(
            new string('a', 64),
            isApproved: true,
            approvedDetector,
            candidateProvider,
            new RasterDecoderStub(inputs.Raster));

        ProductionAxisGeometryEvidence candidateEvidence =
            await candidate.DetectForLocalSyntheticCandidateEvaluationAsync(
                inputs.Request,
                CancellationToken.None);
        ProductionAxisGeometryEvidence approvedEvidence = await approved.DetectAsync(
            inputs.Request,
            CancellationToken.None);
        await Assert.ThrowsAsync<ProductionWorkflowStageException>(() =>
            approved.DetectForLocalSyntheticCandidateEvaluationAsync(inputs.Request, CancellationToken.None));
        ProductionWorkflowStageException exception =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => candidate.DetectAsync(
                inputs.Request,
                CancellationToken.None));

        Assert.IsFalse(candidate.IsApproved);
        Assert.AreEqual(1, candidateDetector.CallCount);
        Assert.AreEqual(1, approvedDetector.CallCount);
        Assert.AreSame(candidateProvider, candidateDetector.ObservedProvider);
        Assert.AreEqual(
            approvedEvidence.Geometry.CoordinateSpace,
            candidateEvidence.Geometry.CoordinateSpace);
        Assert.AreEqual(
            approvedEvidence.Geometry.PlotPolygon,
            candidateEvidence.Geometry.PlotPolygon);
        Assert.AreEqual(approvedEvidence.Geometry.XAxis.Line, candidateEvidence.Geometry.XAxis.Line);
        Assert.AreEqual(approvedEvidence.Geometry.YAxis.Line, candidateEvidence.Geometry.YAxis.Line);
        Assert.AreEqual(approvedEvidence.Geometry.Confidence, candidateEvidence.Geometry.Confidence);
        Assert.AreEqual(inputs.Request.RunId, candidateEvidence.Envelope.RunId);
        Assert.AreEqual(inputs.Request.Image.Sha256, candidateEvidence.Envelope.InputSha256);
        Assert.AreEqual(new string('a', 64), candidateEvidence.Envelope.Model?.Sha256);
        Assert.AreEqual("cpu", candidateEvidence.Envelope.Model?.Provider);
        Assert.AreEqual(
            ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
            exception.Failure.Code);
    }

    [TestMethod]
    public async Task AxisCandidateEvaluationHonorsCancellationBeforeExecution()
    {
        TestInputs inputs = CreateInputs();
        var detector = new AxisDetectorStub();
        var adapter = new ProductionAxisGeometryAdapter(
            new string('a', 64),
            isApproved: false,
            detector,
            new LineCandidateProviderStub(),
            new RasterDecoderStub(inputs.Raster));
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            adapter.DetectForLocalSyntheticCandidateEvaluationAsync(inputs.Request, source.Token));

        Assert.IsFalse(adapter.IsApproved);
        Assert.AreEqual(0, detector.CallCount);
    }

    [TestMethod]
    public async Task OcrCandidateEvaluationUsesProductionCoreWithoutApprovingAdapter()
    {
        TestInputs inputs = CreateInputs();
        var candidateDetector = new TextDetectorStub();
        var candidateRecognizer = new TextRecognizerStub();
        ProductionOcrAdapter candidate = CreateOcrAdapter(
            candidateDetector,
            candidateRecognizer,
            isApproved: false);
        var approvedDetector = new TextDetectorStub();
        var approvedRecognizer = new TextRecognizerStub();
        ProductionOcrAdapter approved = CreateOcrAdapter(
            approvedDetector,
            approvedRecognizer,
            isApproved: true);
        OcrDetectorImage detectorImage = CreateDetectorImage(inputs.Raster);
        var plotBounds = new OcrRectangle(4, 4, 24, 20);

        ProductionOcrEvidence candidateEvidence =
            await candidate.RecognizeForLocalSyntheticCandidateEvaluationAsync(
                inputs.Request,
                inputs.Raster,
                plotBounds,
                detectorImage,
                CancellationToken.None);
        ProductionOcrEvidence approvedEvidence = await approved.RecognizeAsync(
            inputs.Request,
            inputs.Raster,
            plotBounds,
            detectorImage,
            CancellationToken.None);
        await Assert.ThrowsAsync<ProductionWorkflowStageException>(() =>
            approved.RecognizeForLocalSyntheticCandidateEvaluationAsync(
                inputs.Request, inputs.Raster, plotBounds, detectorImage, CancellationToken.None));
        ProductionWorkflowStageException exception =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => candidate.RecognizeAsync(
                inputs.Request,
                inputs.Raster,
                plotBounds,
                detectorImage,
                CancellationToken.None));

        Assert.IsFalse(candidate.IsApproved);
        Assert.AreEqual(1, candidateDetector.CallCount);
        Assert.AreEqual(1, candidateRecognizer.CallCount);
        Assert.AreEqual(1, approvedDetector.CallCount);
        Assert.AreEqual(1, approvedRecognizer.CallCount);
        Assert.AreEqual(approvedEvidence.Result.Regions[0].Text, candidateEvidence.Result.Regions[0].Text);
        CollectionAssert.AreEquivalent(
            ExpectedOcrTasks,
            candidateEvidence.ModelEvidence.Select(static evidence => evidence.Task).ToArray());
        CollectionAssert.AreEquivalent(
            approvedEvidence.ModelEvidence.Select(static evidence => evidence.Envelope.Model?.Sha256).ToArray(),
            candidateEvidence.ModelEvidence.Select(static evidence => evidence.Envelope.Model?.Sha256).ToArray());
        Assert.IsTrue(candidateEvidence.ModelEvidence.All(evidence =>
            evidence.Envelope.RunId == inputs.Request.RunId &&
            evidence.Envelope.ProjectId == inputs.Request.ProjectId &&
            evidence.Envelope.PanelId == inputs.Request.Panel.ImportedPanel.PanelId &&
            evidence.Envelope.InputSha256 == inputs.Request.Image.Sha256));
        Assert.AreEqual(
            ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
            exception.Failure.Code);
    }

    [TestMethod]
    public async Task OcrCandidateEvaluationRetainsInputAndCancellationValidation()
    {
        TestInputs inputs = CreateInputs();
        var detector = new TextDetectorStub();
        var recognizer = new TextRecognizerStub();
        ProductionOcrAdapter adapter = CreateOcrAdapter(detector, recognizer, isApproved: false);
        var mismatchedRaster = new ProductionDecodedRaster(
            inputs.Raster.Width,
            inputs.Raster.Height,
            new string('f', 64),
            inputs.Raster.Variant,
            MarkerAffineTransform.Identity,
            OcrFrameTransform.Identity,
            inputs.Raster.Width,
            inputs.Raster.Height,
            new byte[inputs.Raster.Width * inputs.Raster.Height],
            new float[inputs.Raster.Width * inputs.Raster.Height]);

        ProductionWorkflowStageException mismatch =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(() =>
                adapter.RecognizeForLocalSyntheticCandidateEvaluationAsync(
                    inputs.Request,
                    mismatchedRaster,
                    new OcrRectangle(4, 4, 24, 20),
                    CreateDetectorImage(mismatchedRaster),
                    CancellationToken.None));
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            adapter.RecognizeForLocalSyntheticCandidateEvaluationAsync(
                inputs.Request,
                inputs.Raster,
                new OcrRectangle(4, 4, 24, 20),
                CreateDetectorImage(inputs.Raster),
                source.Token));

        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionEvidenceRejected, mismatch.Failure.Code);
        Assert.IsFalse(adapter.IsApproved);
        Assert.AreEqual(0, detector.CallCount);
        Assert.AreEqual(0, recognizer.CallCount);
    }

    [TestMethod]
    public async Task ApprovedAdapterReportsBothConfiguredModelsButOnlyExecutedDetectionForEmptyResult()
    {
        TestInputs inputs = CreateInputs();
        var detector = new EmptyTextDetectorStub();
        var recognizer = new TextRecognizerStub();
        ProductionOcrAdapter adapter = CreateOcrAdapter(detector, recognizer, isApproved: true);

        ProductionOcrEvidence evidence = await adapter.RecognizeAsync(
            inputs.Request,
            inputs.Raster,
            new OcrRectangle(4, 4, 24, 20),
            CreateDetectorImage(inputs.Raster),
            CancellationToken.None);

        Assert.AreEqual(1, detector.CallCount);
        Assert.AreEqual(0, recognizer.CallCount);
        Assert.IsTrue(evidence.Result.Succeeded);
        Assert.AreEqual(0, evidence.Result.Regions.Count);
        Assert.AreEqual(0, evidence.Result.Masks.Count);
        Assert.AreEqual(0, evidence.Result.Cache.CropCount);
        Assert.AreEqual(0, evidence.Result.Cache.BatchCount);
        Assert.Contains("no_text_regions_detected", evidence.Result.Warnings);
        CollectionAssert.AreEqual(
            ExpectedDetectionOnlyOcrTasks,
            evidence.ModelEvidence.Select(static item => item.Task).ToArray());
        CollectionAssert.AreEqual(
            ExpectedOcrTasks,
            evidence.ConfiguredModels.Models.Select(static item => item.Task).ToArray());
        Assert.AreEqual("approved_production", evidence.ConfiguredModels.Scope);
    }

    [TestMethod]
    public async Task FullResultCacheHitDoesNotClaimDetectorOrRecognizerExecution()
    {
        TestInputs inputs = CreateInputs();
        var detector = new TextDetectorStub();
        var recognizer = new TextRecognizerStub();
        ProductionOcrAdapter adapter = CreateOcrAdapter(
            detector, recognizer, isApproved: true, cache: new MemoryOcrResultCache());
        var bounds = new OcrRectangle(4, 4, 24, 20);
        OcrDetectorImage detectorImage = CreateDetectorImage(inputs.Raster);

        ProductionOcrEvidence first = await adapter.RecognizeAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);
        ProductionOcrEvidence cached = await adapter.RecognizeAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);

        Assert.IsFalse(first.Result.Cache.CacheHit);
        CollectionAssert.AreEquivalent(ExpectedOcrTasks, first.ModelEvidence.Select(static item => item.Task).ToArray());
        Assert.IsTrue(cached.Result.Cache.CacheHit);
        ProductionOcrModelEvidence reuse = cached.ModelEvidence.Single(
            static item => item.Task == ProductionOcrModelEvidence.CacheReuseTask);
        Assert.IsNull(reuse.Envelope.Model);
        Assert.AreEqual("result", reuse.CacheKind);
        Assert.AreEqual(cached.Result.Cache.CacheKey, reuse.CacheKeySha256);
        CollectionAssert.AreEquivalent(ExpectedOcrTasks, cached.ConfiguredModels.Models.Select(static item => item.Task).ToArray());
        Assert.AreEqual(1, detector.CallCount);
        Assert.AreEqual(1, recognizer.CallCount);
    }

    [TestMethod]
    public async Task RecognitionCacheHitDoesNotClaimRecognizerExecution()
    {
        TestInputs inputs = CreateInputs();
        var detector = new TextDetectorStub();
        var recognizer = new TextRecognizerStub();
        ProductionOcrAdapter adapter = CreateOcrAdapter(
            detector, recognizer, isApproved: true, cache: new RecognitionOnlyCache());
        var bounds = new OcrRectangle(4, 4, 24, 20);
        OcrDetectorImage detectorImage = CreateDetectorImage(inputs.Raster);

        _ = await adapter.RecognizeAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);
        ProductionOcrEvidence cachedRecognition = await adapter.RecognizeAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);

        Assert.IsFalse(cachedRecognition.Result.Cache.CacheHit);
        Assert.IsTrue(cachedRecognition.Result.Cache.RecognitionCacheHit);
        CollectionAssert.AreEqual(ExpectedDetectionOnlyOcrTasks,
            cachedRecognition.ModelEvidence.Where(static item =>
                item.Task != ProductionOcrModelEvidence.CacheReuseTask)
                .Select(static item => item.Task).ToArray());
        ProductionOcrModelEvidence reuse = cachedRecognition.ModelEvidence.Single(
            static item => item.Task == ProductionOcrModelEvidence.CacheReuseTask);
        Assert.IsNull(reuse.Envelope.Model);
        Assert.AreEqual("recognition", reuse.CacheKind);
        Assert.AreEqual(cachedRecognition.Result.Cache.RecognitionCacheKey, reuse.CacheKeySha256);
        Assert.AreEqual(2, detector.CallCount);
        Assert.AreEqual(1, recognizer.CallCount);
    }

    [TestMethod]
    public async Task RecognitionOnlyCacheHitFlowsThroughComposerWithReuseReceipt()
    {
        TestInputs inputs = CreateInputs();
        var detector = new TextDetectorStub();
        var recognizer = new TextRecognizerStub();
        ProductionOcrAdapter adapter = CreateOcrAdapter(
            detector, recognizer, isApproved: false, cache: new RecognitionOnlyCache());
        var bounds = new OcrRectangle(4, 4, 24, 20);
        OcrDetectorImage detectorImage = CreateDetectorImage(inputs.Raster);
        _ = await adapter.RecognizeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);
        ProductionOcrEvidence cached = await adapter.RecognizeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);
        Assert.IsFalse(cached.Result.Cache.CacheHit);
        Assert.IsTrue(cached.Result.Cache.RecognitionCacheHit);

        var axisEvidence = new ProductionAxisGeometryEvidence(
            new WorkflowVisionEnvelope(
                1,
                inputs.Request.RunId,
                inputs.Request.ProjectId,
                inputs.Request.Panel.ImportedPanel.PanelId,
                "axis",
                "axis-v1",
                inputs.Request.Image.Sha256,
                new WorkflowVisionModel("axis", "1", new string('a', 64), "cpu"),
                new WorkflowVisionTiming(1, 1, 1, 3),
                0.98),
            CreateGeometry());
        var artifact = new CacheHitArtifactMaskAdapter();
        var composer = new ProductionDetectionMaskComposer(artifact);

        ProductionDetectionMaskEvidence composed = await composer.ComposeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request,
            inputs.Raster,
            axisEvidence,
            cached,
            CancellationToken.None);

        Assert.IsTrue(artifact.WasInvoked);
        CollectionAssert.AreEqual(ExpectedDetectionOnlyOcrTasks, artifact.OcrModelTasks.ToArray());
        WorkflowVisionEnvelope reuse = composed.SourceEnvelopes.Single(envelope =>
            envelope.Warnings.Contains("ocr_cache_reuse_kind:recognition", StringComparer.Ordinal));
        Assert.IsNull(reuse.Model);
        Assert.IsTrue(composed.CopyOcrMask().Values.ToArray().Any(static value => value == 1));
    }

    [TestMethod]
    public async Task FullResultCacheHitFlowsThroughMaskComposerWithReuseReceipt()
    {
        TestInputs inputs = CreateInputs();
        var detector = new TextDetectorStub();
        var recognizer = new TextRecognizerStub();
        ProductionOcrAdapter adapter = CreateOcrAdapter(
            detector, recognizer, isApproved: false, cache: new MemoryOcrResultCache());
        var bounds = new OcrRectangle(4, 4, 24, 20);
        OcrDetectorImage detectorImage = CreateDetectorImage(inputs.Raster);
        _ = await adapter.RecognizeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);
        ProductionOcrEvidence cached = await adapter.RecognizeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);
        Assert.IsTrue(cached.Result.Cache.CacheHit);

        var axisEvidence = new ProductionAxisGeometryEvidence(
            new WorkflowVisionEnvelope(
                1,
                inputs.Request.RunId,
                inputs.Request.ProjectId,
                inputs.Request.Panel.ImportedPanel.PanelId,
                "axis",
                "axis-v1",
                inputs.Request.Image.Sha256,
                new WorkflowVisionModel("axis", "1", new string('a', 64), "cpu"),
                new WorkflowVisionTiming(1, 1, 1, 3),
                0.98),
            CreateGeometry());
        var artifact = new CacheHitArtifactMaskAdapter();
        var composer = new ProductionDetectionMaskComposer(artifact);

        ProductionDetectionMaskEvidence composed = await composer.ComposeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request,
            inputs.Raster,
            axisEvidence,
            cached,
            CancellationToken.None);

        Assert.IsTrue(artifact.WasInvoked);
        Assert.IsTrue(artifact.OcrCacheHit);
        Assert.IsEmpty(artifact.OcrModelTasks);
        WorkflowVisionEnvelope reuse = composed.SourceEnvelopes.Single(envelope =>
            envelope.Warnings.Contains("ocr_cache_reuse_kind:result", StringComparer.Ordinal));
        Assert.IsNull(reuse.Model);
        Assert.AreEqual(inputs.Request.RunId, reuse.RunId);
        Assert.AreEqual(inputs.Request.ProjectId, reuse.ProjectId);
        Assert.AreEqual(inputs.Request.Panel.ImportedPanel.PanelId, reuse.PanelId);
        Assert.AreEqual(inputs.Request.Image.Sha256, reuse.InputSha256);
        Assert.IsTrue(composed.CopyOcrMask().Values.ToArray().Any(static value => value == 1));
    }

    [TestMethod]
    public async Task ZeroCropFullResultCacheHitFlowsThroughMaskComposerWithReuseReceipt()
    {
        TestInputs inputs = CreateInputs();
        var detector = new EmptyTextDetectorStub();
        var recognizer = new TextRecognizerStub();
        ProductionOcrAdapter adapter = CreateOcrAdapter(
            detector, recognizer, isApproved: false, cache: new MemoryOcrResultCache());
        var bounds = new OcrRectangle(4, 4, 24, 20);
        OcrDetectorImage detectorImage = CreateDetectorImage(inputs.Raster);
        _ = await adapter.RecognizeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);
        ProductionOcrEvidence cached = await adapter.RecognizeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);
        Assert.IsTrue(cached.Result.Cache.CacheHit);
        Assert.IsTrue(cached.Result.Cache.RecognitionCacheHit);
        Assert.AreEqual(0, cached.Result.Cache.CropCount);

        var axisEvidence = new ProductionAxisGeometryEvidence(
            new WorkflowVisionEnvelope(
                1,
                inputs.Request.RunId,
                inputs.Request.ProjectId,
                inputs.Request.Panel.ImportedPanel.PanelId,
                "axis",
                "axis-v1",
                inputs.Request.Image.Sha256,
                new WorkflowVisionModel("axis", "1", new string('a', 64), "cpu"),
                new WorkflowVisionTiming(1, 1, 1, 3),
                0.98),
            CreateGeometry());
        var artifact = new CacheHitArtifactMaskAdapter();
        var composer = new ProductionDetectionMaskComposer(artifact);

        ProductionDetectionMaskEvidence composed = await composer.ComposeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request,
            inputs.Raster,
            axisEvidence,
            cached,
            CancellationToken.None);

        Assert.IsTrue(artifact.WasInvoked);
        Assert.IsTrue(artifact.OcrCacheHit);
        Assert.IsEmpty(artifact.OcrModelTasks);
        Assert.AreEqual(0, composed.OcrMaskedPixelCount);
        Assert.IsTrue(composed.SourceEnvelopes.Any(envelope =>
            envelope.Warnings.Contains("ocr_cache_reuse_kind:result", StringComparer.Ordinal) &&
            envelope.Model is null));
    }

    [TestMethod]
    public async Task ApprovedFullResultCacheHitFlowsThroughProductionComposer()
    {
        TestInputs inputs = CreateInputs();
        var detector = new TextDetectorStub();
        var recognizer = new TextRecognizerStub();
        ProductionOcrAdapter adapter = CreateOcrAdapter(
            detector, recognizer, isApproved: true, cache: new MemoryOcrResultCache());
        var bounds = new OcrRectangle(4, 4, 24, 20);
        OcrDetectorImage detectorImage = CreateDetectorImage(inputs.Raster);
        _ = await adapter.RecognizeAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);
        ProductionOcrEvidence cached = await adapter.RecognizeAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);
        Assert.AreEqual("approved_production", cached.ConfiguredModels.Scope);
        Assert.IsTrue(cached.ConfiguredModels.IsApproved);
        Assert.IsTrue(cached.Result.Cache.CacheHit);

        var axisEvidence = new ProductionAxisGeometryEvidence(
            new WorkflowVisionEnvelope(
                1,
                inputs.Request.RunId,
                inputs.Request.ProjectId,
                inputs.Request.Panel.ImportedPanel.PanelId,
                "axis",
                "axis-v1",
                inputs.Request.Image.Sha256,
                new WorkflowVisionModel("axis", "1", new string('a', 64), "cpu"),
                new WorkflowVisionTiming(1, 1, 1, 3),
                0.98),
            CreateGeometry());
        var artifact = new CacheHitArtifactMaskAdapter(isApproved: true);
        var composer = new ProductionDetectionMaskComposer(artifact);

        ProductionDetectionMaskEvidence composed = await composer.ComposeAsync(
            inputs.Request,
            inputs.Raster,
            axisEvidence,
            cached,
            CancellationToken.None);

        Assert.IsTrue(composer.IsApproved);
        Assert.IsTrue(artifact.WasInvoked);
        Assert.IsEmpty(artifact.OcrModelTasks);
        Assert.IsTrue(composed.SourceEnvelopes.Any(envelope =>
            envelope.Warnings.Contains("ocr_cache_reuse_kind:result", StringComparer.Ordinal) &&
            envelope.Model is null));
    }

    [TestMethod]
    [DataRow("inconsistent-cache-flags")]
    [DataRow("mismatched-cache-key")]
    [DataRow("configured-model-identity-changed")]
    public async Task CacheReuseReceiptRejectsInconsistentOrUnboundEvidence(string mode)
    {
        TestInputs inputs = CreateInputs();
        var detector = new TextDetectorStub();
        var recognizer = new TextRecognizerStub();
        ProductionOcrAdapter adapter = CreateOcrAdapter(
            detector, recognizer, isApproved: false, cache: new MemoryOcrResultCache());
        var bounds = new OcrRectangle(4, 4, 24, 20);
        OcrDetectorImage detectorImage = CreateDetectorImage(inputs.Raster);
        _ = await adapter.RecognizeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);
        ProductionOcrEvidence cached = await adapter.RecognizeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request, inputs.Raster, bounds, detectorImage, CancellationToken.None);
        Assert.IsTrue(cached.Result.Cache.CacheHit);

        OcrResult result = cached.Result;
        ProductionOcrConfigurationEvidence configuration = cached.ConfiguredModels;
        switch (mode)
        {
            case "inconsistent-cache-flags":
                result = result with { Cache = result.Cache with { RecognitionCacheHit = false } };
                break;
            case "mismatched-cache-key":
                result = result with { Cache = result.Cache with { CacheKey = new string('f', 64) } };
                break;
            case "configured-model-identity-changed":
                configuration = new ProductionOcrConfigurationEvidence(
                    cached.ConfiguredModels.Models.Select(model => model.Task == "ocr_detection"
                        ? new ProductionOcrConfiguredModel(
                            model.Task,
                            new ModelIdentity(model.ModelId, model.Version, new string('e', 64), "detector.onnx"),
                            InferenceProvider.Cpu)
                        : model),
                    ProductionOcrConfigurationScope.UnapprovedLocalSyntheticCandidate);
                break;
            default:
                Assert.Fail($"Unknown cache mutation '{mode}'.");
                break;
        }

        var artifact = new CacheHitArtifactMaskAdapter();
        var composer = new ProductionDetectionMaskComposer(artifact);
        var axisEvidence = new ProductionAxisGeometryEvidence(
            new WorkflowVisionEnvelope(
                1,
                inputs.Request.RunId,
                inputs.Request.ProjectId,
                inputs.Request.Panel.ImportedPanel.PanelId,
                "axis",
                "axis-v1",
                inputs.Request.Image.Sha256,
                new WorkflowVisionModel("axis", "1", new string('a', 64), "cpu"),
                new WorkflowVisionTiming(1, 1, 1, 3),
                0.98),
            CreateGeometry());

        await Assert.ThrowsAsync<ProductionWorkflowStageException>(() =>
            composer.ComposeForLocalSyntheticCandidateEvaluationAsync(
                inputs.Request,
                inputs.Raster,
                axisEvidence,
                new ProductionOcrEvidence(result, cached.ModelEvidence, configuration),
                CancellationToken.None));
        Assert.IsFalse(artifact.WasInvoked, "Untrusted cache evidence must be rejected before mask composition.");
    }

    [TestMethod]
    public async Task CacheBindingDistinguishesDelimiterContainingModelIdentities()
    {
        TestInputs inputs = CreateInputs();
        ProductionOcrAdapter adapter = CreateOcrAdapter(
            new TextDetectorStub(), new TextRecognizerStub(), isApproved: false);
        ProductionOcrEvidence evidence = await adapter.RecognizeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request,
            inputs.Raster,
            new OcrRectangle(4, 4, 24, 20),
            CreateDetectorImage(inputs.Raster),
            CancellationToken.None);
        ProductionOcrConfiguredModel detector = evidence.ConfiguredModels.Models.Single(
            static model => model.Task == "ocr_detection");

        ProductionOcrConfigurationEvidence first = WithDetectorIdentity("foo|bar", "baz");
        ProductionOcrConfigurationEvidence second = WithDetectorIdentity("foo", "bar|baz");
        Assert.AreEqual(
            string.Join("|", detector.Task, "foo|bar", "baz", detector.Sha256, detector.ExecutionProvider),
            string.Join("|", detector.Task, "foo", "bar|baz", detector.Sha256, detector.ExecutionProvider));

        string firstBinding = ProductionOcrModelEvidence.ComputeCacheBindingSha256(
            "result", new string('f', 64), inputs.Request, evidence.Result, first.Models);
        string secondBinding = ProductionOcrModelEvidence.ComputeCacheBindingSha256(
            "result", new string('f', 64), inputs.Request, evidence.Result, second.Models);

        Assert.AreNotEqual(firstBinding, secondBinding);

        ProductionOcrConfigurationEvidence WithDetectorIdentity(string modelId, string version) =>
            new(
                evidence.ConfiguredModels.Models.Select(model => model.Task == "ocr_detection"
                    ? new ProductionOcrConfiguredModel(
                        model.Task,
                        new ModelIdentity(modelId, version, model.Sha256, "test.onnx"),
                        InferenceProvider.Cpu)
                    : model),
                ProductionOcrConfigurationScope.UnapprovedLocalSyntheticCandidate);
    }

    [TestMethod]
    public void DirectAdapterConstructionCannotClaimProductionApproval()
    {
        var pipeline = new OcrPipeline(
            new EmptyTextDetectorStub(),
            new TextRecognizerStub(),
            new MemoryOcrResultCache());

        Assert.ThrowsExactly<ArgumentException>(() => new ProductionOcrAdapter(
            pipeline,
            new ModelIdentity("graph-ocr-detector", "0.1.0", new string('b', 64), "detector.onnx"),
            InferenceProvider.Cpu,
            new ModelIdentity("graph-ocr-recognizer", "0.1.0", new string('c', 64), "recognizer.onnx"),
            InferenceProvider.Cpu,
            new string('d', 64),
            isApproved: true));
    }

    private static ProductionOcrAdapter CreateOcrAdapter(
        ITextRegionDetector detector,
        ITextRecognizer recognizer,
        bool isApproved,
        IOcrResultCache? cache = null)
    {
        var pipeline = new OcrPipeline(detector, recognizer, cache ?? new MemoryOcrResultCache());
        var detectionModel = new ModelIdentity(
            "graph-ocr-detector",
            "0.1.0",
            new string('b', 64),
            "detector.onnx");
        var recognitionModel = new ModelIdentity(
            "graph-ocr-recognizer",
            "0.1.0",
            new string('c', 64),
            "recognizer.onnx");
        return isApproved
            ? ProductionOcrAdapter.CreateFromValidatedApprovedPipeline(
                pipeline,
                detectionModel,
                InferenceProvider.Cpu,
                recognitionModel,
                InferenceProvider.Cpu,
                new string('d', 64))
            : new ProductionOcrAdapter(
                pipeline,
                detectionModel,
                InferenceProvider.Cpu,
                recognitionModel,
                InferenceProvider.Cpu,
                new string('d', 64),
                isApproved: false);
    }

    private static OcrDetectorImage CreateDetectorImage(ProductionDecodedRaster raster)
    {
        OcrImage image = raster.CreateOcrImage();
        Assert.IsNotNull(image.BgrPixels);
        return new OcrDetectorImage(
            image,
            Convert.ToHexStringLower(SHA256.HashData(image.Pixels.Span)),
            Convert.ToHexStringLower(SHA256.HashData(image.BgrPixels.Pixels.Span)));
    }

    private static TestInputs CreateInputs()
    {
        const int width = 32;
        const int height = 32;
        byte[] sourceBytes = [1, 2, 3, 4];
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        var image = new WorkflowImageEvidence(
            "memory:synthetic-candidate.png",
            sha256,
            width,
            height,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(
            Guid.Parse("10000000-0000-0000-0000-000000000020"),
            Guid.Parse("20000000-0000-0000-0000-000000000020"),
            "synthetic-candidate.png",
            image);
        var request = new ProductionWorkflowDetectionRequest(
            new WorkflowPreparedPanel(imported, image, enhanced: null),
            image,
            WorkflowImageVariant.Original,
            Guid.Parse("30000000-0000-0000-0000-000000000020"),
            Guid.Parse("40000000-0000-0000-0000-000000000020"),
            sourceBytes);
        var raster = new ProductionDecodedRaster(
            width,
            height,
            sha256,
            WorkflowImageVariant.Original,
            MarkerAffineTransform.Identity,
            OcrFrameTransform.Identity,
            width,
            height,
            new byte[width * height],
            new float[width * height]);
        return new TestInputs(request, raster);
    }

    private static AxisGeometryResult CreateGeometry() =>
        new(
            AxisGeometryCoordinateSpaces.OriginalPixels,
            new PlotPolygon(
                new PixelPoint(4, 27),
                new PixelPoint(27, 27),
                new PixelPoint(27, 4),
                new PixelPoint(4, 4)),
            new AxisLineFit(
                new GeometryLineSegment(new PixelPoint(4, 27), new PixelPoint(27, 27)),
                0.98,
                0,
                1,
                ["x"]),
            new AxisLineFit(
                new GeometryLineSegment(new PixelPoint(4, 27), new PixelPoint(4, 4)),
                0.98,
                0,
                1,
                ["y"]),
            [],
            [],
            [],
            0.98,
            new AxisGeometryUncertainty(0, 0, 1, false, []),
            new AxisGeometryDiagnostics(2, 2, 0, 1, 1, 0, 0, 0, TimeSpan.Zero, []));

    private sealed record TestInputs(
        ProductionWorkflowDetectionRequest Request,
        ProductionDecodedRaster Raster);

    private sealed class RasterDecoderStub(ProductionDecodedRaster raster) : IProductionRasterFrameDecoder
    {
        public ProductionDecodedRaster Decode(
            ProductionWorkflowDetectionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return raster;
        }
    }

    private sealed class LineCandidateProviderStub : ILineCandidateProvider
    {
        public ValueTask<IReadOnlyList<GeometryLineCandidate>> DetectLinesAsync(
            GrayscaleLineCandidateFrame frame,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<GeometryLineCandidate>>([]);
    }

    private sealed class AxisDetectorStub : IAxisGeometryDetector
    {
        public int CallCount { get; private set; }

        public ILineCandidateProvider? ObservedProvider { get; private set; }

        public ValueTask<AxisGeometryResult> DetectAsync(
            AxisGeometryRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<AxisGeometryResult> DetectAsync(
            GrayscaleLineCandidateFrame frame,
            ILineCandidateProvider candidateProvider,
            AxisGeometryOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            ObservedProvider = candidateProvider;
            return ValueTask.FromResult(CreateGeometry());
        }
    }

    private sealed class TextDetectorStub : ITextRegionDetector
    {
        public int CallCount { get; private set; }

        public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
            OcrImage image,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            IReadOnlyList<OcrDetectedRegion> regions =
            [
                new(
                    "region-1",
                    OcrPolygon.FromRectangle(new OcrRectangle(8, 8, 8, 6)),
                    OrientationDegrees: 0,
                    DetectionConfidence: 0.98,
                    new OcrRegionContext(NumericExpected: true)),
            ];
            return ValueTask.FromResult(regions);
        }
    }

    private sealed class EmptyTextDetectorStub : ITextRegionDetector
    {
        public int CallCount { get; private set; }

        public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
            OcrImage image,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult<IReadOnlyList<OcrDetectedRegion>>([]);
        }
    }

    private sealed class TextRecognizerStub : ITextRecognizer
    {
        public string ModelId => "graph-ocr-recognizer";

        public string ModelVersion => "0.1.0";

        public string ModelSha256 => new('c', 64);

        public int CallCount { get; private set; }

        public ValueTask<IReadOnlyList<OcrRecognition>> RecognizeBatchAsync(
            IReadOnlyList<OcrCrop> crops,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            IReadOnlyList<OcrRecognition> recognitions = crops
                .Select(crop => new OcrRecognition(
                    crop.RegionId,
                    crop.SourceImage,
                    [new OcrRecognitionAlternative("10", 0.99, crop.SourceImage)],
                    InferenceMilliseconds: 0.1))
                .ToArray();
            return ValueTask.FromResult(recognitions);
        }
    }

    private sealed class RecognitionOnlyCache : IOcrResultCache
    {
        private OcrRecognitionCachePayload? recognition;

        public ValueTask<OcrCachedPayload?> TryGetAsync(string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult<OcrCachedPayload?>(null);

        public ValueTask PutAsync(string key, OcrCachedPayload payload, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<OcrRecognitionCachePayload?> TryGetRecognitionAsync(
            string key,
            CancellationToken cancellationToken) => ValueTask.FromResult(recognition);

        public ValueTask PutRecognitionAsync(
            string key,
            OcrRecognitionCachePayload payload,
            CancellationToken cancellationToken)
        {
            recognition = payload;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CacheHitArtifactMaskAdapter : IProductionArtifactMaskAdapter
    {
        private readonly bool isApproved;

        public CacheHitArtifactMaskAdapter(bool isApproved = false) => this.isApproved = isApproved;

        public string AdapterId => "test-cached-ocr-artifacts";
        public bool IsApproved => isApproved;
        public bool WasInvoked { get; private set; }
        public bool OcrCacheHit { get; private set; }
        public IReadOnlyList<string> OcrModelTasks { get; private set; } = [];

        public Task<ProductionArtifactMaskEvidence> DetectAsync(
            ProductionWorkflowDetectionRequest request,
            ProductionDecodedRaster raster,
            ProductionDetectionMaskSeed seed,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Expected axis and OCR provenance overload.");

        public Task<ProductionArtifactMaskEvidence> DetectAsync(
            ProductionWorkflowDetectionRequest request,
            ProductionDecodedRaster raster,
            ProductionDetectionMaskSeed seed,
            ProductionAxisGeometryEvidence axisEvidence,
            IReadOnlyList<ProductionOcrModelEvidence> ocrModelEvidence,
            OcrResult ocrResult,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasInvoked = true;
            OcrCacheHit = ocrResult.Cache.CacheHit;
            OcrModelTasks = Array.AsReadOnly(ocrModelEvidence.Select(static item => item.Task).ToArray());
            var algorithm = new ProductionArtifactAlgorithmEvidence(
                "test-cached-ocr-mask", "1", new string('d', 64), new string('e', 64));
            var envelope = new WorkflowVisionEnvelope(
                1,
                request.RunId,
                request.ProjectId,
                request.Panel.ImportedPanel.PanelId,
                "markers",
                algorithm.StageVersion,
                raster.InputSha256,
                model: null,
                new WorkflowVisionTiming(0, null, 0, 0),
                0,
                [algorithm.AssemblyWarning, algorithm.ConfigurationWarning],
                request.Transforms);
            int pixelCount = checked(raster.Width * raster.Height);
            return Task.FromResult(new ProductionArtifactMaskEvidence(
                raster.Width,
                raster.Height,
                raster.InputSha256,
                raster.Variant,
                envelope,
                new float[pixelCount],
                algorithm: algorithm,
                residualCategoryBits: new byte[pixelCount]));
        }
    }
}
