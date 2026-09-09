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

    private static ProductionOcrAdapter CreateOcrAdapter(
        TextDetectorStub detector,
        TextRecognizerStub recognizer,
        bool isApproved) =>
        new(
            new OcrPipeline(detector, recognizer, new MemoryOcrResultCache()),
            new ModelIdentity("graph-ocr-detector", "0.1.0", new string('b', 64), "detector.onnx"),
            InferenceProvider.Cpu,
            new ModelIdentity("graph-ocr-recognizer", "0.1.0", new string('c', 64), "recognizer.onnx"),
            InferenceProvider.Cpu,
            new string('d', 64),
            isApproved);

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
}
