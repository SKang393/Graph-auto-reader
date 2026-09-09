// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Ocr;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class ProductionOcrAdapterTests
{
    private static readonly string[] ExpectedOcrTasks =
        ["ocr_detection", "ocr_recognition"];

    [TestMethod]
    public async Task ApprovedAdapterBindsTwoModelsToCurrentOriginalEvidence()
    {
        Fixture fixture = CreateFixture();
        var detector = new TextDetectorStub();
        var recognizer = new TextRecognizerStub();
        var pipeline = new OcrPipeline(detector, recognizer, new MemoryOcrResultCache());
        var adapter = CreateAdapter(pipeline, isApproved: true);

        ProductionOcrEvidence evidence = await adapter.RecognizeAsync(
            fixture.Request,
            fixture.Raster,
            new OcrRectangle(4, 4, 24, 20),
            DetectorImage(fixture.Raster),
            CancellationToken.None);

        Assert.IsTrue(evidence.Result.Succeeded);
        Assert.HasCount(1, evidence.Result.Regions);
        Assert.AreEqual("10", evidence.Result.Regions[0].Text);
        Assert.HasCount(2, evidence.ModelEvidence);
        CollectionAssert.AreEquivalent(
            ExpectedOcrTasks,
            evidence.ModelEvidence.Select(static item => item.Task).ToArray());
        foreach (ProductionOcrModelEvidence modelEvidence in evidence.ModelEvidence)
        {
            Assert.AreEqual(fixture.Request.RunId, modelEvidence.Envelope.RunId);
            Assert.AreEqual(fixture.Request.ProjectId, modelEvidence.Envelope.ProjectId);
            Assert.AreEqual(fixture.Request.Panel.ImportedPanel.PanelId, modelEvidence.Envelope.PanelId);
            Assert.AreEqual(fixture.Request.Image.Sha256, modelEvidence.Envelope.InputSha256);
            Assert.AreEqual("original_pixels", modelEvidence.Envelope.CoordinateSpace);
            Assert.AreEqual("cpu", modelEvidence.Envelope.Model?.Provider);
            CollectionAssert.Contains(
                modelEvidence.Envelope.Warnings.ToArray(),
                "ocr_pipeline_timing_not_model_isolated");
        }

        Assert.AreEqual(1, detector.CallCount);
        Assert.AreEqual(1, recognizer.CallCount);
    }

    [TestMethod]
    public async Task UnapprovedAdapterRejectsBeforePipelineExecution()
    {
        Fixture fixture = CreateFixture();
        var detector = new TextDetectorStub();
        var recognizer = new TextRecognizerStub();
        var pipeline = new OcrPipeline(detector, recognizer, new MemoryOcrResultCache());
        var adapter = CreateAdapter(pipeline, isApproved: false);

        ProductionWorkflowStageException exception =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => adapter.RecognizeAsync(
                fixture.Request,
                fixture.Raster,
                new OcrRectangle(4, 4, 24, 20),
                DetectorImage(fixture.Raster),
                CancellationToken.None));

        Assert.AreEqual(
            ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
            exception.Failure.Code);
        Assert.AreEqual(0, detector.CallCount);
        Assert.AreEqual(0, recognizer.CallCount);
    }

    [TestMethod]
    public async Task AdapterRejectsMismatchedRasterBeforePipelineExecution()
    {
        Fixture fixture = CreateFixture();
        Fixture other = CreateFixture(Guid.Parse("10000000-0000-0000-0000-000000000022"));
        var detector = new TextDetectorStub();
        var recognizer = new TextRecognizerStub();
        var pipeline = new OcrPipeline(detector, recognizer, new MemoryOcrResultCache());
        var adapter = CreateAdapter(pipeline, isApproved: true);

        ProductionWorkflowStageException exception =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => adapter.RecognizeAsync(
                fixture.Request,
                other.Raster,
                new OcrRectangle(4, 4, 24, 20),
                DetectorImage(other.Raster),
                CancellationToken.None));

        Assert.AreEqual(
            ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
            exception.Failure.Code);
        Assert.AreEqual(0, detector.CallCount);
        Assert.AreEqual(0, recognizer.CallCount);
    }

    [TestMethod]
    public async Task AdapterHonorsPreCanceledRequestBeforePipelineExecution()
    {
        Fixture fixture = CreateFixture();
        var detector = new TextDetectorStub();
        var recognizer = new TextRecognizerStub();
        var pipeline = new OcrPipeline(detector, recognizer, new MemoryOcrResultCache());
        var adapter = CreateAdapter(pipeline, isApproved: true);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => adapter.RecognizeAsync(
            fixture.Request,
            fixture.Raster,
            new OcrRectangle(4, 4, 24, 20),
            DetectorImage(fixture.Raster),
            source.Token));

        Assert.AreEqual(0, detector.CallCount);
        Assert.AreEqual(0, recognizer.CallCount);
    }

    [TestMethod]
    public async Task DetectionFailureDoesNotClaimEitherModelCompleted()
    {
        Fixture fixture = CreateFixture();
        var detector = new TextDetectorStub(throwOnCall: true);
        var recognizer = new TextRecognizerStub();
        var pipeline = new OcrPipeline(detector, recognizer, new MemoryOcrResultCache());
        var adapter = CreateAdapter(pipeline, isApproved: true);

        ProductionWorkflowStageException exception =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => adapter.RecognizeAsync(
                fixture.Request,
                fixture.Raster,
                new OcrRectangle(4, 4, 24, 20),
                DetectorImage(fixture.Raster),
                CancellationToken.None));

        Assert.AreEqual(
            ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
            exception.Failure.Code);
        Assert.IsEmpty(exception.CompletedEvidence);
        Assert.AreEqual(1, detector.CallCount);
        Assert.AreEqual(0, recognizer.CallCount);
    }

    [TestMethod]
    public async Task RecognitionFailurePreservesOnlyCompletedDetectionEvidence()
    {
        Fixture fixture = CreateFixture();
        var detector = new TextDetectorStub();
        var recognizer = new TextRecognizerStub(throwOnCall: true);
        var pipeline = new OcrPipeline(detector, recognizer, new MemoryOcrResultCache());
        var adapter = CreateAdapter(pipeline, isApproved: true);

        ProductionWorkflowStageException exception =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => adapter.RecognizeAsync(
                fixture.Request,
                fixture.Raster,
                new OcrRectangle(4, 4, 24, 20),
                DetectorImage(fixture.Raster),
                CancellationToken.None));

        Assert.AreEqual(
            ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
            exception.Failure.Code);
        Assert.HasCount(1, exception.CompletedEvidence);
        Assert.AreEqual(new string('a', 64), exception.CompletedEvidence[0].Model?.Sha256);
        Assert.AreEqual(1, detector.CallCount);
        Assert.AreEqual(1, recognizer.CallCount);
    }

    private static ProductionOcrAdapter CreateAdapter(OcrPipeline pipeline, bool isApproved)
    {
        var detection = new ModelIdentity(
                "graph-ocr-detector",
                "0.1.0",
                new string('a', 64),
                "detector.onnx");
        var recognition = new ModelIdentity(
                "graph-ocr-recognizer",
                "0.1.0",
                new string('b', 64),
                "recognizer.onnx");
        return isApproved
            ? ProductionOcrAdapter.CreateFromValidatedApprovedPipeline(
                pipeline, detection, InferenceProvider.Cpu,
                recognition, InferenceProvider.Cpu, new string('c', 64))
            : new ProductionOcrAdapter(
                pipeline, detection, InferenceProvider.Cpu,
                recognition, InferenceProvider.Cpu, new string('c', 64), isApproved: false);
    }

    private static OcrDetectorImage DetectorImage(ProductionDecodedRaster raster)
    {
        OcrImage image = raster.CreateOcrImage();
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(image.Pixels.Span));
        Assert.IsNotNull(image.BgrPixels);
        string bgrSha256 = Convert.ToHexStringLower(SHA256.HashData(image.BgrPixels.Pixels.Span));
        return new OcrDetectorImage(image, sha256, bgrSha256);
    }

    private static Fixture CreateFixture(Guid? projectId = null)
    {
        byte[] encoded = CreatePng(projectId.HasValue ? (byte)0x40 : (byte)0x20);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded));
        Guid actualProjectId = projectId ?? Guid.Parse("10000000-0000-0000-0000-000000000021");
        Guid sourceId = Guid.Parse("20000000-0000-0000-0000-000000000021");
        Guid panelId = Guid.Parse("30000000-0000-0000-0000-000000000021");
        Guid runId = Guid.Parse("40000000-0000-0000-0000-000000000021");
        var original = new WorkflowImageEvidence(
            "memory:ocr.png",
            sha256,
            width: 32,
            height: 32,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(panelId, sourceId, "ocr.png", original);
        var request = new ProductionWorkflowDetectionRequest(
            new WorkflowPreparedPanel(imported, original, enhanced: null),
            original,
            WorkflowImageVariant.Original,
            runId,
            actualProjectId,
            encoded);
        ProductionDecodedRaster raster = new ProductionRasterFrameDecoder().Decode(
            request,
            CancellationToken.None);
        return new Fixture(request, raster);
    }

    private static byte[] CreatePng(byte background)
    {
        const int width = 32;
        const int height = 32;
        byte[] pixels = Enumerable.Repeat(background, width * height).ToArray();
        for (int y = 8; y < 14; y++)
        {
            for (int x = 8; x < 16; x++)
            {
                pixels[(y * width) + x] = 0xff;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Gray8,
            palette: null,
            pixels,
            stride: width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed record Fixture(
        ProductionWorkflowDetectionRequest Request,
        ProductionDecodedRaster Raster);

    private sealed class TextDetectorStub(bool throwOnCall = false) : ITextRegionDetector
    {
        public int CallCount { get; private set; }

        public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
            OcrImage image,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (throwOnCall)
            {
                throw new InvalidOperationException("Controlled detector failure.");
            }

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

    private sealed class TextRecognizerStub(bool throwOnCall = false) : ITextRecognizer
    {
        public string ModelId => "graph-ocr-recognizer";

        public string ModelVersion => "0.1.0";

        public string ModelSha256 => new('b', 64);

        public int CallCount { get; private set; }

        public ValueTask<IReadOnlyList<OcrRecognition>> RecognizeBatchAsync(
            IReadOnlyList<OcrCrop> crops,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (throwOnCall)
            {
                throw new InvalidOperationException("Controlled recognizer failure.");
            }

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
