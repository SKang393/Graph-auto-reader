// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using Axis = GraphReader.Axis;
using LegendReasoning = GraphReader.Legends;
using MarkerClassification = GraphReader.Markers.Classification;
using MarkerDetection = GraphReader.Markers.Detection;
using PhaseReasoning = GraphReader.Phases;
using GraphReader.Domain;
using GraphReader.Export;
using GraphReader.Imaging;
using GraphReader.Inference;
using GraphReader.Pdf;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class ProductionWorkflowStagesTests
{
    private static readonly byte[] ExpectedDecodedBgr = [10, 20, 30];

    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [TestMethod]
    public async Task ImageImportRetainsImmutableBytesAndStablePanelMapping()
    {
        string evidenceDirectory = GetEvidenceDirectory("import");
        string imagePath = Path.Combine(evidenceDirectory, "real-source.png");
        byte[] sourceBytes = Convert.FromBase64String(OnePixelPng);
        await File.WriteAllBytesAsync(imagePath, sourceBytes);
        Guid projectId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid sourceId = Guid.Parse("20000000-0000-0000-0000-000000000001");

        ProductionPanelEvidence first = await ImportOneAsync(projectId, sourceId, imagePath);
        ProductionPanelEvidence second = await ImportOneAsync(projectId, sourceId, imagePath);

        Assert.AreEqual(first.Panel.PanelId, second.Panel.PanelId);
        Assert.AreEqual(1, first.Panel.Original.Width);
        Assert.AreEqual(1, first.Panel.Original.Height);
        CollectionAssert.AreEqual(sourceBytes, first.CopyOriginalBytes());
        byte[] callerCopy = first.CopyOriginalBytes();
        callerCopy[0] ^= 0xff;
        CollectionAssert.AreEqual(sourceBytes, first.CopyOriginalBytes());
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(),
            first.Panel.Original.Sha256);
    }

    [TestMethod]
    public async Task IdenticalImportRerunIsIdempotentAndPreservesPanelIdentity()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = await WriteSourceAsync(directory.Path);
        var store = new ProductionWorkflowPanelStore();
        Guid projectId = Guid.Parse("10000000-0000-0000-0000-000000000005");
        Guid sourceId = Guid.Parse("20000000-0000-0000-0000-000000000005");

        WorkflowImportedPanel first = await ImportOneAsync(store, projectId, sourceId, imagePath);
        WorkflowImportedPanel second = await ImportOneAsync(store, projectId, sourceId, imagePath);

        Assert.AreEqual(first.PanelId, second.PanelId);
        Assert.HasCount(1, store.PanelIds);
        CollectionAssert.AreEqual(
            Convert.FromBase64String(OnePixelPng),
            store.Get(first.PanelId).CopyOriginalBytes());
    }

    [TestMethod]
    public async Task ProductionBatchCancellationRetainsCompletedImmutablePanelEvidence()
    {
        using var directory = new TemporaryDirectory();
        byte[] sourceBytes = Convert.FromBase64String(OnePixelPng);
        string firstPath = Path.Combine(directory.Path, "batch-first.png");
        string secondPath = Path.Combine(directory.Path, "batch-second.png");
        await File.WriteAllBytesAsync(firstPath, sourceBytes);
        await File.WriteAllBytesAsync(secondPath, sourceBytes);
        string firstHashBefore = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(firstPath)))
            .ToLowerInvariant();
        string secondHashBefore = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(secondPath)))
            .ToLowerInvariant();
        using var cancellation = new CancellationTokenSource();
        var observer = new CancelSecondSourceObserver(secondPath, cancellation);
        var store = new ProductionWorkflowPanelStore();
        var stage = new ProductionWorkflowImportStage(store, new ImageImportService(observer));
        Guid firstSourceId = Guid.Parse("20000000-0000-0000-0000-000000000006");

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => stage.ImportAsync(
            new WorkflowImportRequest(
                Guid.Parse("10000000-0000-0000-0000-000000000006"),
                [
                    new WorkflowSourceRequest(firstSourceId, WorkflowSourceKind.Image, firstPath),
                    new WorkflowSourceRequest(
                        Guid.Parse("20000000-0000-0000-0000-000000000007"),
                        WorkflowSourceKind.Image,
                        secondPath),
                ]),
            cancellation.Token));

        Assert.HasCount(1, store.PanelIds);
        ProductionPanelEvidence completed = store.Get(store.PanelIds.Single());
        Assert.AreEqual(firstSourceId, completed.Panel.SourceId);
        Assert.AreEqual(firstPath, completed.Panel.Original.Reference);
        Assert.AreEqual(firstHashBefore, completed.Panel.Original.Sha256);
        CollectionAssert.AreEqual(sourceBytes, completed.CopyOriginalBytes());
        Assert.AreEqual(
            firstHashBefore,
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(firstPath))).ToLowerInvariant());
        Assert.AreEqual(
            secondHashBefore,
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(secondPath))).ToLowerInvariant());
    }

    [TestMethod]
    public async Task PdfPanelWithoutEncodedDetectorBytesFailsWithStructuredCode()
    {
        using var directory = new TemporaryDirectory();
        string pdfPath = Path.Combine(directory.Path, "article.pdf");
        await File.WriteAllBytesAsync(pdfPath, "%PDF-1.7\n%%EOF"u8.ToArray());
        var stage = new ProductionWorkflowImportStage(
            new ProductionWorkflowPanelStore(),
            new ImageImportService(),
            new PdfWithoutDetectorBytes());
        var request = new WorkflowImportRequest(
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            [new WorkflowSourceRequest(
                Guid.Parse("20000000-0000-0000-0000-000000000002"),
                WorkflowSourceKind.Pdf,
                pdfPath)]);

        ProductionWorkflowStageException exception = await Assert.ThrowsAsync<ProductionWorkflowStageException>(
            () => stage.ImportAsync(request, CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.PdfPanelBytesUnavailable, exception.Failure.Code);
        Assert.IsTrue(exception.Failure.Recoverable);
        StringAssert.Contains(exception.Failure.TechnicalMessage, "detector-ready");
    }

    [TestMethod]
    public async Task PdfPanelCropProducesImmutableDetectorBytesAndPageProvenance()
    {
        using var directory = new TemporaryDirectory();
        string pdfPath = Path.Combine(directory.Path, "cropped-panel.pdf");
        byte[] pdfBytes = "%PDF-1.7\n% cropped panel import\n%%EOF"u8.ToArray();
        await File.WriteAllBytesAsync(pdfPath, pdfBytes);
        var store = new ProductionWorkflowPanelStore();
        var stage = new ProductionWorkflowImportStage(
            store,
            new ImageImportService(),
            new PdfWithCroppedDetectorPanel());
        var request = new WorkflowImportRequest(
            Guid.Parse("10000000-0000-0000-0000-000000000009"),
            [new WorkflowSourceRequest(
                Guid.Parse("20000000-0000-0000-0000-000000000009"),
                WorkflowSourceKind.Pdf,
                pdfPath)]);
        WorkflowImportSnapshot snapshot = await stage.ImportAsync(request, CancellationToken.None);
        WorkflowImportSnapshot repeated = await stage.ImportAsync(request, CancellationToken.None);

        WorkflowImportedPanel imported = snapshot.Panels.Single();
        Assert.AreEqual(imported.PanelId, repeated.Panels.Single().PanelId);
        Assert.HasCount(1, store.PanelIds);
        ProductionPanelEvidence evidence = store.Get(imported.PanelId);
        Assert.AreEqual(21, imported.Original.Width);
        Assert.AreEqual(11, imported.Original.Height);
        byte[] detectorBytes = evidence.CopyOriginalBytes();
        Assert.AreEqual(
            imported.Original.Sha256,
            Convert.ToHexStringLower(SHA256.HashData(detectorBytes)));
        using (var stream = new MemoryStream(detectorBytes, writable: false))
        {
            BitmapFrame frame = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            Assert.AreEqual(21, frame.PixelWidth);
            Assert.AreEqual(11, frame.PixelHeight);
        }

        Assert.IsNotNull(evidence.PdfPanelSource);
        PdfPanelSourceProvenance provenance = evidence.PdfPanelSource;
        Assert.AreEqual(new PdfRectD(5.2, 4.1, 20.3, 10.2), provenance.RequestedCropInSourcePixels);
        Assert.AreEqual(new PdfRectD(5, 4, 21, 11), provenance.EncodedCropInSourcePixels);
        Assert.AreEqual(PdfFigureSourceKind.RenderedPage, provenance.FigureSourceKind);
        Assert.AreEqual(
            Convert.ToHexStringLower(SHA256.HashData(pdfBytes)),
            provenance.DocumentSha256);
        PdfPointD pagePoint = provenance.MapPanelPixelToPagePoint(new PdfPointD(0, 0));
        Assert.AreEqual(150d, pagePoint.X, 1e-9);
        Assert.AreEqual(460d, pagePoint.Y, 1e-9);
        StringAssert.Contains(imported.Original.Reference, "crop=5,4,21,11");
    }

    [TestMethod]
    public async Task PrepareUsesOriginalWhenEnhancementAdapterIsNotApproved()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = await WriteSourceAsync(directory.Path);
        var store = new ProductionWorkflowPanelStore();
        WorkflowImportedPanel imported = await ImportOneAsync(store, imagePath);
        var adapter = new EnhancementAdapter(isApproved: false);
        var stage = new ProductionWorkflowPrepareStage(store, adapter);

        WorkflowPreparedPanel prepared = await stage.PrepareAsync(
            imported,
            enhancementEnabled: true,
            CancellationToken.None);

        Assert.IsNull(prepared.Enhanced);
        Assert.AreEqual(0, adapter.CallCount);
        Assert.AreEqual(imported.Original.Sha256, prepared.Original.Sha256);
        StringAssert.Contains(prepared.Warnings.Single(), "not approved");
    }

    [TestMethod]
    public async Task PrepareRetainsApprovedDerivativeAndReversibleTransform()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = await WriteSourceAsync(directory.Path);
        var store = new ProductionWorkflowPanelStore();
        WorkflowImportedPanel imported = await ImportOneAsync(store, imagePath);
        var adapter = new EnhancementAdapter(isApproved: true);
        var stage = new ProductionWorkflowPrepareStage(store, adapter);

        WorkflowPreparedPanel prepared = await stage.PrepareAsync(
            imported,
            enhancementEnabled: true,
            CancellationToken.None);
        ProductionPanelEvidence stored = store.Get(imported.PanelId);

        Assert.IsNotNull(prepared.Enhanced);
        Assert.AreEqual(1, adapter.CallCount);
        Assert.AreEqual(2, prepared.Enhanced.Width);
        Assert.AreEqual(2, prepared.Enhanced.Height);
        Assert.HasCount(1, stored.EnhancementTransforms);
        Assert.IsNotNull(stored.EnhancementTransforms[0].OutputToInputMatrix);
        byte[] firstCopy = stored.CopyEnhancedBytes()!;
        firstCopy[0] ^= 0xff;
        Assert.AreNotEqual(firstCopy[0], stored.CopyEnhancedBytes()![0]);
    }

    [TestMethod]
    public async Task DetectionWithoutApprovedAdapterFailsClosedWithoutCandidates()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = await WriteSourceAsync(directory.Path);
        var store = new ProductionWorkflowPanelStore();
        WorkflowImportedPanel imported = await ImportOneAsync(store, imagePath);
        var prepared = new WorkflowPreparedPanel(imported, imported.Original, enhanced: null);
        var stage = new ProductionWorkflowDetectionStage(store);

        ProductionWorkflowStageException exception = await Assert.ThrowsAsync<ProductionWorkflowStageException>(
            () => stage.DetectAsync(
                prepared,
                WorkflowImageVariant.Original,
                Guid.NewGuid(),
                Guid.Parse("10000000-0000-0000-0000-000000000003"),
                CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionModelsUnavailable, exception.Failure.Code);
        StringAssert.Contains(exception.Failure.SuggestedAction, "manual mode");
    }

    [TestMethod]
    public async Task ApprovedAxisAdapterDecodesImmutableOriginalAndReturnsRuntimeProvenance()
    {
        byte[] encoded = CreateAxisPng();
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded));
        Guid projectId = Guid.Parse("10000000-0000-0000-0000-000000000006");
        Guid sourceId = Guid.Parse("20000000-0000-0000-0000-000000000006");
        Guid panelId = Guid.Parse("30000000-0000-0000-0000-000000000006");
        Guid runId = Guid.Parse("40000000-0000-0000-0000-000000000006");
        var original = new WorkflowImageEvidence(
            "memory:axis.png",
            sha256,
            width: 96,
            height: 72,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(
            panelId,
            sourceId,
            "axis.png",
            original);
        var prepared = new WorkflowPreparedPanel(imported, original, enhanced: null);
        var request = new ProductionWorkflowDetectionRequest(
            prepared,
            original,
            WorkflowImageVariant.Original,
            runId,
            projectId,
            encoded);
        encoded[0] ^= 0xff;

        string runtimeSha256 = new('a', 64);
        var adapter = new ProductionAxisGeometryAdapter(
            runtimeSha256,
            isApproved: true);

        ProductionAxisGeometryEvidence evidence = await adapter.DetectAsync(
            request,
            CancellationToken.None);

        Assert.AreEqual("axis", evidence.Envelope.Stage);
        Assert.AreEqual(ProductionAxisGeometryAdapter.StageVersion, evidence.Envelope.StageVersion);
        Assert.AreEqual(sha256, evidence.Envelope.InputSha256);
        Assert.AreEqual("original_pixels", evidence.Envelope.CoordinateSpace);
        Assert.AreEqual(runtimeSha256, evidence.Envelope.Model?.Sha256);
        Assert.AreEqual("cpu", evidence.Envelope.Model?.Provider);
        Assert.AreEqual("original_pixels", evidence.Geometry.CoordinateSpace);
        Assert.IsTrue(evidence.Geometry.Confidence > 0);
        Assert.IsTrue(evidence.Envelope.Timing.TotalMilliseconds >= 0);

        var provider = new AxisCandidateProviderStub();
        var unavailable = new ProductionAxisGeometryAdapter(
            runtimeSha256,
            isApproved: false,
            new Axis.AxisGeometryDetector(),
            provider);
        ProductionWorkflowStageException exception =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(
                () => unavailable.DetectAsync(request, CancellationToken.None));
        Assert.AreEqual(
            ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
            exception.Failure.Code);
        Assert.AreEqual(0, provider.CallCount);
    }

    [TestMethod]
    public void RasterDecoderCreatesIsolatedOriginalStageFramesAndRequiresAlignedMasks()
    {
        byte[] encoded = CreateAxisPng();
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded));
        Guid projectId = Guid.Parse("10000000-0000-0000-0000-000000000011");
        Guid sourceId = Guid.Parse("20000000-0000-0000-0000-000000000011");
        Guid panelId = Guid.Parse("30000000-0000-0000-0000-000000000011");
        var original = new WorkflowImageEvidence(
            "memory:raster-original.png",
            sha256,
            width: 96,
            height: 72,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(panelId, sourceId, "raster-original.png", original);
        var request = new ProductionWorkflowDetectionRequest(
            new WorkflowPreparedPanel(imported, original, enhanced: null),
            original,
            WorkflowImageVariant.Original,
            Guid.Parse("40000000-0000-0000-0000-000000000011"),
            projectId,
            encoded);
        encoded[0] ^= 0xff;

        var decoder = new ProductionRasterFrameDecoder();
        ProductionDecodedRaster raster = decoder.Decode(request, CancellationToken.None);
        Axis.GrayscaleLineCandidateFrame axis = raster.CreateAxisFrame();
        GraphReader.Ocr.OcrImage ocr = raster.CreateOcrImage();

        Assert.AreEqual(sha256, raster.InputSha256);
        Assert.AreEqual(96 * 72, axis.Pixels.Length);
        Assert.AreEqual(GraphReader.Ocr.OcrSourceImage.Original, ocr.SourceImage);
        Assert.AreEqual(96, ocr.CanonicalOriginalWidth);
        Assert.AreEqual(72, ocr.CanonicalOriginalHeight);
        Assert.AreEqual(0, axis.Pixels.Span[(10 * 96) + 10]);

        byte[] ocrCopy = ocr.Pixels.ToArray();
        ocrCopy[0] = 0;
        Assert.AreEqual(255, raster.CreateOcrImage().Pixels.Span[0]);

        var ocrMaskValues = new float[96 * 72];
        var artifactMaskValues = new float[96 * 72];
        ocrMaskValues[0] = 0.25f;
        artifactMaskValues[0] = 0.75f;
        MarkerDetection.MarkerImageFrame marker = raster.CreateMarkerFrame(
            new MarkerDetection.MarkerMask(96, 72, ocrMaskValues),
            new MarkerDetection.MarkerMask(96, 72, artifactMaskValues));
        ocrMaskValues[0] = 1;
        artifactMaskValues[0] = 1;

        Assert.AreEqual(MarkerDetection.MarkerSourceImage.Original, marker.SourceImage);
        Assert.AreEqual(MarkerDetection.MarkerAffineTransform.Identity, marker.OriginalToFrame);
        Assert.AreEqual(1f, marker.ChannelsFirstPixels.Span[0]);
        Assert.AreEqual(0.25f, marker.OcrMask.Values.Span[0]);
        Assert.AreEqual(0.75f, marker.ArtifactMask.Values.Span[0]);
        Assert.Throws<ProductionWorkflowStageException>(() => raster.CreateMarkerFrame(
            new MarkerDetection.MarkerMask(1, 1, new float[1]),
            MarkerDetection.MarkerMask.Empty(96, 72)));
    }

    [TestMethod]
    public void RasterDecoderPreservesExactBgr24OrderFromColoredSource()
    {
        byte[] encoded = CreateSolidBgrPng(10, 20, 30);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded));
        Guid sourceId = Guid.Parse("21000000-0000-0000-0000-000000000011");
        Guid panelId = Guid.Parse("31000000-0000-0000-0000-000000000011");
        var original = new WorkflowImageEvidence(
            "memory:colored-bgr.png",
            sha256,
            width: 1,
            height: 1,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(panelId, sourceId, "colored-bgr.png", original);
        var request = new ProductionWorkflowDetectionRequest(
            new WorkflowPreparedPanel(imported, original, enhanced: null),
            original,
            WorkflowImageVariant.Original,
            Guid.Parse("41000000-0000-0000-0000-000000000011"),
            Guid.Parse("11000000-0000-0000-0000-000000000011"),
            encoded);

        GraphReader.Ocr.OcrImage ocr = new ProductionRasterFrameDecoder()
            .Decode(request, CancellationToken.None)
            .CreateOcrImage();

        Assert.IsNotNull(ocr.BgrPixels);
        Assert.AreEqual(3, ocr.BgrPixels.Stride);
        CollectionAssert.AreEqual(
            ExpectedDecodedBgr,
            ocr.BgrPixels.Pixels.ToArray());
    }

    [TestMethod]
    public void RasterDecoderMapsExactReversibleEnhancedScaleToOriginalPixels()
    {
        byte[] originalBytes = CreateSolidGrayPng(96, 72, 255);
        byte[] enhancedBytes = CreateSolidGrayPng(192, 144, 128);
        string originalSha256 = Convert.ToHexStringLower(SHA256.HashData(originalBytes));
        string enhancedSha256 = Convert.ToHexStringLower(SHA256.HashData(enhancedBytes));
        Guid projectId = Guid.Parse("10000000-0000-0000-0000-000000000012");
        Guid sourceId = Guid.Parse("20000000-0000-0000-0000-000000000012");
        Guid panelId = Guid.Parse("30000000-0000-0000-0000-000000000012");
        var original = new WorkflowImageEvidence(
            "memory:raster-original.png",
            originalSha256,
            96,
            72,
            WorkflowImageVariant.Original);
        var enhanced = new WorkflowImageEvidence(
            "memory:raster-enhanced.png",
            enhancedSha256,
            192,
            144,
            WorkflowImageVariant.Enhanced);
        var imported = new WorkflowImportedPanel(panelId, sourceId, "raster-original.png", original);
        var prepared = new WorkflowPreparedPanel(imported, original, enhanced);
        var transform = new WorkflowTransformProvenance(
            "scale-2x",
            "original_pixels",
            "enhanced_pixels",
            [2, 0, 0, 0, 2, 0, 0, 0, 1],
            [0.5, 0, 0, 0, 0.5, 0, 0, 0, 1],
            lossy: false);
        var request = new ProductionWorkflowDetectionRequest(
            prepared,
            enhanced,
            WorkflowImageVariant.Enhanced,
            Guid.Parse("40000000-0000-0000-0000-000000000012"),
            projectId,
            enhancedBytes,
            [transform]);

        ProductionDecodedRaster raster = new ProductionRasterFrameDecoder().Decode(
            request,
            CancellationToken.None);
        MarkerDetection.MarkerPoint originalPoint = raster.OriginalToFrame.MapToOriginal(
            new MarkerDetection.MarkerPoint(40, 60));
        GraphReader.Ocr.OcrPoint ocrPoint = raster.OriginalToImage.MapToOriginal(
            new GraphReader.Ocr.OcrPoint(40, 60));

        Assert.AreEqual(20, originalPoint.X);
        Assert.AreEqual(30, originalPoint.Y);
        Assert.AreEqual(20, ocrPoint.X);
        Assert.AreEqual(30, ocrPoint.Y);
        Assert.AreEqual(MarkerDetection.MarkerSourceImage.Enhanced, raster.CreateMarkerFrame(
            MarkerDetection.MarkerMask.Empty(192, 144),
            MarkerDetection.MarkerMask.Empty(192, 144)).SourceImage);
        Assert.Throws<ProductionWorkflowStageException>(() => raster.CreateAxisFrame());

        var invalidTransform = new WorkflowTransformProvenance(
            "invalid-inverse",
            "original_pixels",
            "enhanced_pixels",
            [2, 0, 0, 0, 2, 0, 0, 0, 1],
            [1, 0, 0, 0, 1, 0, 0, 0, 1],
            lossy: false);
        var invalidRequest = new ProductionWorkflowDetectionRequest(
            prepared,
            enhanced,
            WorkflowImageVariant.Enhanced,
            request.RunId,
            projectId,
            enhancedBytes,
            [invalidTransform]);
        Assert.Throws<ProductionWorkflowStageException>(() =>
            new ProductionRasterFrameDecoder().Decode(invalidRequest, CancellationToken.None));
    }

    [TestMethod]
    public async Task DetectionMaskComposerRequiresTwoModelOcrAndRasterizesOnlyBoundEvidence()
    {
        byte[] encoded = CreateSolidGrayPng(96, 72, 255);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded));
        Guid projectId = Guid.Parse("10000000-0000-0000-0000-000000000013");
        Guid sourceId = Guid.Parse("20000000-0000-0000-0000-000000000013");
        Guid panelId = Guid.Parse("30000000-0000-0000-0000-000000000013");
        Guid runId = Guid.Parse("40000000-0000-0000-0000-000000000013");
        var original = new WorkflowImageEvidence(
            "memory:mask-source.png",
            sha256,
            96,
            72,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(panelId, sourceId, "mask-source.png", original);
        var request = new ProductionWorkflowDetectionRequest(
            new WorkflowPreparedPanel(imported, original, enhanced: null),
            original,
            WorkflowImageVariant.Original,
            runId,
            projectId,
            encoded);
        ProductionDecodedRaster raster = new ProductionRasterFrameDecoder().Decode(
            request,
            CancellationToken.None);

        var xAxis = new Axis.AxisLineFit(
            new Axis.GeometryLineSegment(new Axis.PixelPoint(10, 60), new Axis.PixelPoint(86, 60)),
            0.98,
            0,
            1,
            ["x-axis"]);
        var yAxis = new Axis.AxisLineFit(
            new Axis.GeometryLineSegment(new Axis.PixelPoint(10, 60), new Axis.PixelPoint(10, 10)),
            0.98,
            0,
            1,
            ["y-axis"]);
        var geometry = new Axis.AxisGeometryResult(
            "original_pixels",
            new Axis.PlotPolygon(
                new Axis.PixelPoint(10, 60),
                new Axis.PixelPoint(86, 60),
                new Axis.PixelPoint(86, 10),
                new Axis.PixelPoint(10, 10)),
            xAxis,
            yAxis,
            [
                new Axis.AxisTickGeometry(
                    "tick-1",
                    Axis.TickAxis.XAxis,
                    new Axis.PixelPoint(30, 60),
                    new Axis.GeometryLineSegment(new Axis.PixelPoint(30, 58), new Axis.PixelPoint(30, 62)),
                    0.9,
                    ["tick"]),
            ],
            [
                new Axis.PhaseDividerGeometry(
                    "divider-1",
                    new Axis.GeometryLineSegment(new Axis.PixelPoint(50, 10), new Axis.PixelPoint(50, 60)),
                    Axis.DividerStyle.Solid,
                    0.9,
                    1,
                    1,
                    ["divider"]),
            ],
            [
                new Axis.AmbiguousGridOrDividerGeometry(
                    "ambiguous-1",
                    new Axis.GeometryLineSegment(new Axis.PixelPoint(70, 10), new Axis.PixelPoint(70, 60)),
                    0.8,
                    1,
                    1,
                    ["ambiguous"]),
            ],
            0.95,
            new Axis.AxisGeometryUncertainty(0, 0, 1, false, []),
            new Axis.AxisGeometryDiagnostics(5, 5, 0, 1, 4, 1, 1, 0, TimeSpan.Zero, []));
        var axisEnvelope = new WorkflowVisionEnvelope(
            1,
            runId,
            projectId,
            panelId,
            "axis",
            ProductionAxisGeometryAdapter.StageVersion,
            sha256,
            new WorkflowVisionModel("axis", "1", new string('c', 64), "cpu"),
            new WorkflowVisionTiming(1, 0, 1, 2),
            0.95);
        var axisEvidence = new ProductionAxisGeometryEvidence(axisEnvelope, geometry);

        GraphReader.Ocr.OcrPolygon textPolygon = GraphReader.Ocr.OcrPolygon.FromRectangle(
            new GraphReader.Ocr.OcrRectangle(30, 20, 10, 5));
        var ocrResult = new GraphReader.Ocr.OcrResult(
            GraphReader.Ocr.OcrContract.Version,
            Guid.Parse("50000000-0000-0000-0000-000000000013").ToString("D"),
            projectId.ToString("D"),
            panelId.ToString("D"),
            GraphReader.Ocr.OcrContract.Stage,
            "0.3.0",
            sha256,
            GraphReader.Ocr.OcrContract.CoordinateSpace,
            [
                new GraphReader.Ocr.OcrRegion(
                    "text-1",
                    textPolygon,
                    "100",
                    [new GraphReader.Ocr.OcrRecognitionAlternative("100", 0.95, GraphReader.Ocr.OcrSourceImage.Original)],
                    GraphReader.Ocr.OcrTextRole.YTick,
                    0.95,
                    GraphReader.Ocr.OcrSourceImage.Original,
                    GraphReader.Ocr.OcrReviewStatus.Unreviewed),
            ],
            [new GraphReader.Ocr.OcrMask("text-1", textPolygon, 0.95)],
            new GraphReader.Ocr.OcrTiming(1, 2, 1, 4),
            0.95,
            [],
            new GraphReader.Ocr.OcrCacheDiagnostics(false, "cache", 1, 1),
            null,
            []);
        var detectionEnvelope = new WorkflowVisionEnvelope(
            1,
            runId,
            projectId,
            panelId,
            "ocr",
            "0.3.0",
            sha256,
            new WorkflowVisionModel("ocr-detector", "1", new string('a', 64), "cpu"),
            new WorkflowVisionTiming(1, 1, 1, 3),
            0.9);
        var recognitionEnvelope = new WorkflowVisionEnvelope(
            1,
            runId,
            projectId,
            panelId,
            "ocr",
            "0.3.0",
            sha256,
            new WorkflowVisionModel("ocr-recognizer", "1", new string('b', 64), "cpu"),
            new WorkflowVisionTiming(1, 1, 1, 3),
            0.9);
        ProductionOcrModelEvidence[] ocrEvidence =
        [
            new("ocr_detection", detectionEnvelope),
            new("ocr_recognition", recognitionEnvelope),
        ];
        var configuredModels = new ProductionOcrConfigurationEvidence(
        [
            new("ocr_detection", new ModelIdentity("ocr-detector", "1", new string('a', 64), "fixture-detector.onnx"), InferenceProvider.Cpu),
            new("ocr_recognition", new ModelIdentity("ocr-recognizer", "1", new string('b', 64), "fixture-recognizer.onnx"), InferenceProvider.Cpu),
        ],
        ProductionOcrConfigurationScope.ApprovedProduction);
        var completeOcrEvidence = new ProductionOcrEvidence(ocrResult, ocrEvidence, configuredModels);

        var composer = new ProductionDetectionMaskComposer(new ArtifactMaskAdapter());
        ProductionDetectionMaskEvidence masks = await composer.ComposeAsync(
            request,
            raster,
            axisEvidence,
            completeOcrEvidence,
            CancellationToken.None);
        MarkerDetection.MarkerImageFrame markerFrame = masks.CreateMarkerFrame(raster);

        Assert.HasCount(4, masks.SourceEnvelopes);
        Assert.IsTrue(masks.OcrMaskedPixelCount > 0);
        Assert.IsTrue(masks.ArtifactMaskedPixelCount > 0);
        Assert.AreEqual(1f, markerFrame.OcrMask.Values.Span[(22 * 96) + 35]);
        Assert.AreEqual(1f, markerFrame.ArtifactMask.Values.Span[(60 * 96) + 30]);
        Assert.AreEqual(1f, markerFrame.ArtifactMask.Values.Span[(40 * 96) + 80]);
        Assert.AreEqual(0f, markerFrame.OcrMask.Values.Span[(40 * 96) + 80]);
        await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => composer.ComposeAsync(
            request,
            raster,
            axisEvidence,
            new ProductionOcrEvidence(ocrResult, [ocrEvidence[0]], configuredModels),
            CancellationToken.None));

        var unavailable = new ProductionDetectionMaskComposer();
        Assert.IsFalse(unavailable.IsApproved);
        ProductionWorkflowStageException unavailableException =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => unavailable.ComposeAsync(
                request,
                raster,
                axisEvidence,
                completeOcrEvidence,
                CancellationToken.None));
        Assert.AreEqual(
            ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
            unavailableException.Failure.Code);
    }

    [TestMethod]
    public async Task ApprovedMarkerClassifierAdapterRetainsExactModelProviderAndCoordinates()
    {
        byte[] encoded = CreateAxisPng();
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded));
        Guid projectId = Guid.Parse("10000000-0000-0000-0000-000000000007");
        Guid sourceId = Guid.Parse("20000000-0000-0000-0000-000000000007");
        Guid panelId = Guid.Parse("30000000-0000-0000-0000-000000000007");
        Guid runId = Guid.Parse("40000000-0000-0000-0000-000000000007");
        var original = new WorkflowImageEvidence(
            "memory:markers.png",
            sha256,
            width: 96,
            height: 72,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(panelId, sourceId, "markers.png", original);
        var request = new ProductionWorkflowDetectionRequest(
            new WorkflowPreparedPanel(imported, original, enhanced: null),
            original,
            WorkflowImageVariant.Original,
            runId,
            projectId,
            encoded);
        var frame = new MarkerDetection.MarkerImageFrame(
            96,
            72,
            1,
            Enumerable.Repeat(0.25f, 96 * 72).ToArray(),
            MarkerDetection.MarkerSourceImage.Original,
            MarkerDetection.MarkerAffineTransform.Identity,
            MarkerDetection.MarkerMask.Empty(96, 72),
            MarkerDetection.MarkerMask.Empty(96, 72));
        MarkerDetection.MarkerCenter[] markers =
        [
            new(
                "marker-1",
                new MarkerDetection.MarkerPoint(32, 28),
                4,
                0.01,
                0.96,
                MarkerDetection.MarkerSourceImage.Original),
        ];
        var model = new ModelIdentity(
            "graph-marker-classifier",
            "0.1.0",
            new string('b', 64),
            "model.onnx");
        var options = new MarkerClassification.MarkerClassificationOptions(
            new MarkerClassification.MarkerClassifierTensorContract(
                "marker_patch",
                "classification_probabilities",
                32,
                32,
                1,
                12)
            {
                OutputEncoding = MarkerClassification.MarkerClassifierOutputEncoding.Probabilities,
            })
        {
            StageVersion = "0.1.0",
        };
        var service = new MarkerClassificationServiceStub();
        var adapter = new ProductionMarkerClassificationAdapter(
            model,
            options,
            isApproved: true,
            service);

        ProductionMarkerClassificationEvidence evidence = await adapter.ClassifyAsync(
            request,
            frame,
            markers,
            CancellationToken.None);

        Assert.AreEqual(1, service.CallCount);
        Assert.AreEqual("markers", evidence.Envelope.Stage);
        Assert.AreEqual(model.Sha256.ToLowerInvariant(), evidence.Envelope.Model?.Sha256);
        Assert.AreEqual("cpu", evidence.Envelope.Model?.Provider);
        Assert.AreEqual(sha256, evidence.Envelope.InputSha256);
        Assert.AreEqual("original_pixels", evidence.Envelope.CoordinateSpace);
        Assert.HasCount(1, evidence.Markers);
        Assert.AreEqual("marker-1", evidence.Markers[0].Marker.MarkerId);
        Assert.AreEqual(MarkerClassification.MarkerShape.Circle, evidence.Markers[0].Shape);
        Assert.AreEqual(MarkerClassification.MarkerFill.Open, evidence.Markers[0].Fill);

        MarkerDetection.MarkerCenter[] consensusMarkers =
            [markers[0] with { SourceImage = MarkerDetection.MarkerSourceImage.Consensus }];
        ProductionMarkerClassificationEvidence consensusEvidence = await adapter.ClassifyAsync(
            request,
            frame,
            consensusMarkers,
            CancellationToken.None);
        Assert.AreEqual(2, service.CallCount);
        Assert.AreEqual(
            MarkerDetection.MarkerSourceImage.Consensus,
            consensusEvidence.Markers[0].Marker.SourceImage);

        var unavailableService = new MarkerClassificationServiceStub();
        var unavailable = new ProductionMarkerClassificationAdapter(
            model,
            options,
            isApproved: false,
            unavailableService);
        ProductionWorkflowStageException exception =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(
                () => unavailable.ClassifyAsync(request, frame, markers, CancellationToken.None));
        Assert.AreEqual(
            ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
            exception.Failure.Code);
        Assert.AreEqual(0, unavailableService.CallCount);

        var invalidMaskService = new MarkerClassificationServiceStub();
        var invalidMaskAdapter = new ProductionMarkerClassificationAdapter(
            model,
            options,
            isApproved: true,
            invalidMaskService);
        var invalidMaskFrame = frame with
        {
            OcrMask = new MarkerDetection.MarkerMask(96, 72, new float[1]),
        };
        ProductionWorkflowStageException invalidMaskException =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(
                () => invalidMaskAdapter.ClassifyAsync(
                    request,
                    invalidMaskFrame,
                    markers,
                    CancellationToken.None));
        Assert.AreEqual(
            ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
            invalidMaskException.Failure.Code);
        Assert.AreEqual(0, invalidMaskService.CallCount);
    }

    [TestMethod]
    public async Task ApprovedMarkerCenterAdapterRetainsExactModelProviderAndOriginalCoordinates()
    {
        byte[] encoded = CreateAxisPng();
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded));
        Guid projectId = Guid.Parse("10000000-0000-0000-0000-000000000008");
        Guid sourceId = Guid.Parse("20000000-0000-0000-0000-000000000008");
        Guid panelId = Guid.Parse("30000000-0000-0000-0000-000000000008");
        Guid runId = Guid.Parse("40000000-0000-0000-0000-000000000008");
        var original = new WorkflowImageEvidence(
            "memory:marker-center.png",
            sha256,
            width: 96,
            height: 72,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(panelId, sourceId, "marker-center.png", original);
        var request = new ProductionWorkflowDetectionRequest(
            new WorkflowPreparedPanel(imported, original, enhanced: null),
            original,
            WorkflowImageVariant.Original,
            runId,
            projectId,
            encoded);
        var frame = new MarkerDetection.MarkerImageFrame(
            96,
            72,
            1,
            Enumerable.Repeat(0.75f, 96 * 72).ToArray(),
            MarkerDetection.MarkerSourceImage.Original,
            MarkerDetection.MarkerAffineTransform.Identity,
            MarkerDetection.MarkerMask.Empty(96, 72),
            MarkerDetection.MarkerMask.Empty(96, 72));
        var plot = MarkerDetection.MarkerPolygon.FromRectangle(
            new MarkerDetection.MarkerRectangle(8, 8, 80, 56));
        var model = new ModelIdentity(
            "graph-marker-center",
            "0.1.0",
            new string('c', 64),
            "marker-center.onnx");
        var tensor = new MarkerDetection.MarkerModelTensorContract(
            "image_and_masks",
            "marker_heads",
            64,
            64,
            3,
            MarkerDetection.MarkerTensorLayout.ChannelsFirst,
            64,
            64,
            3,
            MarkerDetection.MarkerTensorLayout.ChannelsFirst,
            0,
            1,
            2,
            MarkerDetection.MarkerHeadActivation.Identity,
            MarkerDetection.MarkerHeadActivation.Identity,
            1,
            0,
            1);
        var options = new MarkerDetection.MarkerDetectionOptions(tensor)
        {
            StageVersion = "0.1.0",
        };
        var service = new MarkerDetectionServiceStub();
        var adapter = new ProductionMarkerCenterAdapter(
            model,
            options,
            isApproved: true,
            service);

        ProductionMarkerCenterEvidence evidence = await adapter.DetectAsync(
            request,
            frame,
            plot,
            enhancedImage: null,
            enhancedTransforms: null,
            CancellationToken.None);

        Assert.AreEqual(1, service.CallCount);
        Assert.AreEqual("markers", evidence.Envelope.Stage);
        Assert.AreEqual(model.Sha256.ToLowerInvariant(), evidence.Envelope.Model?.Sha256);
        Assert.AreEqual("cpu", evidence.Envelope.Model?.Provider);
        Assert.AreEqual(sha256, evidence.Envelope.InputSha256);
        Assert.AreEqual("original_pixels", evidence.Envelope.CoordinateSpace);
        Assert.HasCount(1, evidence.Markers);
        Assert.AreEqual("center-1", evidence.Markers[0].MarkerId);
        Assert.AreEqual(MarkerDetection.MarkerSourceImage.Original, evidence.Markers[0].SourceImage);

        var unavailableService = new MarkerDetectionServiceStub();
        var unavailable = new ProductionMarkerCenterAdapter(
            model,
            options,
            isApproved: false,
            unavailableService);
        ProductionWorkflowStageException exception =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(
                () => unavailable.DetectAsync(
                    request,
                    frame,
                    plot,
                    enhancedImage: null,
                    enhancedTransforms: null,
                    CancellationToken.None));
        Assert.AreEqual(
            ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
            exception.Failure.Code);
        Assert.AreEqual(0, unavailableService.CallCount);

        var invalidFrameService = new MarkerDetectionServiceStub();
        var invalidFrameAdapter = new ProductionMarkerCenterAdapter(
            model,
            options,
            isApproved: true,
            invalidFrameService);
        var invalidFrame = frame with
        {
            ArtifactMask = new MarkerDetection.MarkerMask(96, 72, new float[1]),
        };
        ProductionWorkflowStageException invalidFrameException =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(
                () => invalidFrameAdapter.DetectAsync(
                    request,
                    invalidFrame,
                    plot,
                    enhancedImage: null,
                    enhancedTransforms: null,
                    CancellationToken.None));
        Assert.AreEqual(
            ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
            invalidFrameException.Failure.Code);
        Assert.AreEqual(0, invalidFrameService.CallCount);
    }

    [TestMethod]
    public async Task DeterministicSemanticAdaptersRetainIdentityAndRemainDependencyGated()
    {
        byte[] encoded = CreateAxisPng();
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded));
        Guid projectId = Guid.Parse("10000000-0000-0000-0000-000000000009");
        Guid sourceId = Guid.Parse("20000000-0000-0000-0000-000000000009");
        Guid panelId = Guid.Parse("30000000-0000-0000-0000-000000000009");
        Guid runId = Guid.Parse("40000000-0000-0000-0000-000000000009");
        var original = new WorkflowImageEvidence(
            "memory:semantic.png",
            sha256,
            width: 96,
            height: 72,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(panelId, sourceId, "semantic.png", original);
        var request = new ProductionWorkflowDetectionRequest(
            new WorkflowPreparedPanel(imported, original, enhanced: null),
            original,
            WorkflowImageVariant.Original,
            runId,
            projectId,
            encoded);

        var legendRequest = new LegendReasoning.LegendReasoningRequest(
            projectId.ToString("D"),
            panelId.ToString("D"),
            sha256,
            new LegendReasoning.LegendRectangle(0, 0, 96, 72),
            new LegendReasoning.LegendRectangle(8, 8, 80, 56),
            textRegions: [],
            glyphs: [],
            series: [],
            plotMarkers: []);
        var legendAdapter = new ProductionLegendReasoningAdapter();
        ProductionLegendReasoningEvidence legendEvidence = await legendAdapter.ResolveAsync(
            request,
            legendRequest,
            CancellationToken.None);

        Assert.AreEqual("legends", legendEvidence.Envelope.Stage);
        Assert.AreEqual(runId, legendEvidence.Envelope.RunId);
        Assert.AreEqual(sha256, legendEvidence.Envelope.InputSha256);
        Assert.AreEqual("original_pixels", legendEvidence.Envelope.CoordinateSpace);
        Assert.IsNull(legendEvidence.Envelope.Model);
        Assert.HasCount(0, legendEvidence.Payload.Participants);

        var phaseRequest = new PhaseReasoning.PhaseReasoningRequest(
            projectId.ToString("D"),
            panelId.ToString("D"),
            sha256,
            new PhaseReasoning.PhaseRectangle(8, 8, 80, 56),
            segments: [],
            headings: [],
            points: [],
            series: []);
        var phaseAdapter = new ProductionPhaseReasoningAdapter();
        ProductionPhaseReasoningEvidence phaseEvidence = await phaseAdapter.ResolveAsync(
            request,
            phaseRequest,
            CancellationToken.None);

        Assert.AreEqual("phases", phaseEvidence.Envelope.Stage);
        Assert.AreEqual(runId, phaseEvidence.Envelope.RunId);
        Assert.AreEqual(sha256, phaseEvidence.Envelope.InputSha256);
        Assert.AreEqual("original_pixels", phaseEvidence.Envelope.CoordinateSpace);
        Assert.IsNull(phaseEvidence.Envelope.Model);
        Assert.HasCount(1, phaseEvidence.Payload.Phases);
        Assert.AreEqual("a", phaseEvidence.Payload.Phases[0].Code);

        var legendStub = new LegendReasoningServiceStub();
        var unavailableLegend = new ProductionLegendReasoningAdapter(
            isApproved: false,
            legendStub);
        await Assert.ThrowsAsync<ProductionWorkflowStageException>(
            () => unavailableLegend.ResolveAsync(request, legendRequest, CancellationToken.None));
        Assert.AreEqual(0, legendStub.CallCount);

        var phaseStub = new PhaseReasoningServiceStub();
        var unavailablePhase = new ProductionPhaseReasoningAdapter(
            isApproved: false,
            phaseStub);
        await Assert.ThrowsAsync<ProductionWorkflowStageException>(
            () => unavailablePhase.ResolveAsync(request, phaseRequest, CancellationToken.None));
        Assert.AreEqual(0, phaseStub.CallCount);

        var unpinnedLegendRequest = new LegendReasoning.LegendReasoningRequest(
            projectId.ToString("D"),
            panelId.ToString("D"),
            sha256,
            new LegendReasoning.LegendRectangle(0, 0, 96, 72),
            new LegendReasoning.LegendRectangle(8, 8, 80, 56),
            textRegions: [],
            glyphs: [],
            series: [],
            plotMarkers: [],
            options: new LegendReasoning.LegendReasoningOptions { StageVersion = "unreviewed" });
        await Assert.ThrowsAsync<ProductionWorkflowStageException>(
            () => legendAdapter.ResolveAsync(request, unpinnedLegendRequest, CancellationToken.None));
    }

    [TestMethod]
    public void DetectionEvidenceChainPreservesEarlierStagesWhenLaterStageFails()
    {
        byte[] encoded = CreateAxisPng();
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(encoded));
        Guid projectId = Guid.Parse("10000000-0000-0000-0000-000000000010");
        Guid sourceId = Guid.Parse("20000000-0000-0000-0000-000000000010");
        Guid panelId = Guid.Parse("30000000-0000-0000-0000-000000000010");
        Guid runId = Guid.Parse("40000000-0000-0000-0000-000000000010");
        var original = new WorkflowImageEvidence(
            "memory:evidence-chain.png",
            sha256,
            width: 96,
            height: 72,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(panelId, sourceId, "evidence-chain.png", original);
        var request = new ProductionWorkflowDetectionRequest(
            new WorkflowPreparedPanel(imported, original, enhanced: null),
            original,
            WorkflowImageVariant.Original,
            runId,
            projectId,
            encoded);
        var axis = new WorkflowVisionEnvelope(
            1,
            runId,
            projectId,
            panelId,
            "axis",
            "1.0.0",
            sha256,
            new WorkflowVisionModel("axis-runtime", "1.0.0", new string('d', 64), "cpu"),
            new WorkflowVisionTiming(1, 0, 1, 2),
            0.9);
        var ocr = new WorkflowVisionEnvelope(
            1,
            runId,
            projectId,
            panelId,
            "ocr",
            "1.0.0",
            sha256,
            new WorkflowVisionModel("ocr", "1.0.0", new string('e', 64), "cpu"),
            new WorkflowVisionTiming(1, 2, 1, 4),
            0.8);
        var chain = new ProductionDetectionEvidenceChain(request);

        chain.Append(axis);
        chain.Append(ocr);
        ProductionWorkflowStageException exception = chain.Reject(new ProductionWorkflowFailure(
            "MARKERS_FAILED",
            "Errors.DetectionEvidenceRejected",
            "Marker stage failed after OCR.",
            Recoverable: true,
            "Continue manual review with retained axis and OCR evidence."));

        Assert.HasCount(2, exception.CompletedEvidence);
        Assert.AreSame(axis, exception.CompletedEvidence[0]);
        Assert.AreSame(ocr, exception.CompletedEvidence[1]);
        Assert.AreEqual("MARKERS_FAILED", exception.Failure.Code);
        Assert.Throws<ArgumentException>(() => chain.Append(axis));
        Assert.HasCount(2, chain.Snapshot);

        Assert.Throws<ArgumentException>(() => new WorkflowVisionEnvelope(
            1,
            runId,
            projectId,
            panelId,
            "markers",
            "1.0.0",
            sha256,
            new WorkflowVisionModel("markers", "1.0.0", new string('f', 64), "fake"),
            new WorkflowVisionTiming(1, 2, 1, 4),
            0.8));
        Assert.HasCount(2, chain.Snapshot);
    }

    [TestMethod]
    public async Task BlankTabProjectsOnlyExactEvidenceAndMismatchIsAtomic()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = await WriteSourceAsync(directory.Path);
        var workspace = new ProjectionWorkspace();
        GraphReader.App.ViewModels.WorkspaceTabViewModel tab =
            (await workspace.ImportImagesAsync([imagePath], CancellationToken.None)).Single();
        ImageSource? immutableImage = tab.ImageSource;
        var store = new ProductionWorkflowPanelStore();
        WorkflowImportedPanel imported = await ImportOneAsync(
            store,
            workspace.CurrentProject.ProjectId.Value,
            Guid.Parse(tab.SourceId!),
            imagePath);
        var prepared = new WorkflowPreparedPanel(imported, imported.Original, null);
        Guid pointId = Guid.NewGuid();
        SeriesId seriesId = SeriesId.New();
        PhaseId phaseId = PhaseId.New();
        var series = new SeriesRecord(
            seriesId, "●", MarkerShape.Circle, MarkerFill.Filled, "Detected", SemanticRole.Intervention,
            null, [PointId.FromGuid(pointId)], 0.95, null, [], false);
        var phase = new PhaseRecord(
            phaseId, 1, "a", PhaseNormalizedType.Baseline, "Baseline", 0, 1,
            null, null, 0.95, PhaseSource.ProfilePrior, false);
        var calibration = new CalibrationRecord(
            CalibrationId.New(), CalibrationStatus.Valid,
            [
                new CalibrationAnchor(CalibrationAnchorKind.Session1Y0, new GraphReader.Domain.PixelPoint(0, 1), new GraphPoint(1, 0), 1, null),
                new CalibrationAnchor(CalibrationAnchorKind.Session1Ymax, new GraphReader.Domain.PixelPoint(0, 0), new GraphPoint(1, 100), 1, null),
                new CalibrationAnchor(CalibrationAnchorKind.SessionmaxY0, new GraphReader.Domain.PixelPoint(1, 1), new GraphPoint(2, 0), 1, null),
            ],
            new SessionLatticeRecord(0, 1, 1, 2, 1, "approved"), false, 1, []);
        var point = new WorkflowPoint(
            pointId.ToString("D"), "key", 0.5, 0.5, 0.95, WorkflowImageVariant.Original,
            WorkflowReviewStatus.Accepted, "●", "circle", "filled", seriesId.Value.ToString("D"),
            phaseId.Value.ToString("D"), 1, 50, "markers", "1", false);
        MarkerId markerId = MarkerId.New();
        OcrRegionId ocrRegionId = OcrRegionId.New();
        var transform = new TransformRecord(
            TransformId.New(),
            TransformKind.Scale,
            CoordinateSpace.OriginalPixels,
            CoordinateSpace.EnhancedPixels,
            [2, 0, 0, 0, 2, 0, 0, 0, 1],
            [0.5, 0, 0, 0, 0.5, 0, 0, 0, 1],
            JsonSerializer.SerializeToElement(new { source = "approved-production-fixture" }),
            Lossy: false);
        var ocrEvidence = new OcrEvidence(
            ocrRegionId,
            [
                new GraphReader.Domain.PixelPoint(0.1, 0.1),
                new GraphReader.Domain.PixelPoint(0.2, 0.1),
                new GraphReader.Domain.PixelPoint(0.2, 0.2),
                new GraphReader.Domain.PixelPoint(0.1, 0.2),
            ],
            "50",
            [new OcrAlternative("50", 0.99)],
            OcrRole.YTick,
            0.99,
            SourceImageKind.Original,
            ReviewStatus.Accepted);
        var marker = new MarkerRecord(
            markerId,
            new GraphReader.Domain.PixelPoint(0.5, 0.5),
            0.05,
            MarkerShape.Circle,
            MarkerFill.Filled,
            "●",
            0,
            0.95,
            0.96,
            0.97,
            Embedding: null,
            CandidateSeriesId: seriesId,
            SourceImageKind.Original,
            ReviewStatus.Accepted);
        var domainPoint = new PointRecord(
            PointId.FromGuid(pointId), markerId, seriesId, phaseId,
            new GraphReader.Domain.PixelPoint(0.5, 0.5), 1, 50, 1, 1, null,
            PointXSource.Printed, 1, 1, 0.95, "markers", "1", ReviewStatus.Accepted, []);
        Guid productionRunId = Guid.NewGuid();
        var identityTransform = new WorkflowTransformProvenance(
            "marker-original-identity",
            "original_pixels",
            "original_pixels",
            [1, 0, 0, 0, 1, 0, 0, 0, 1],
            [1, 0, 0, 0, 1, 0, 0, 0, 1],
            lossy: false);
        var envelope = new WorkflowVisionEnvelope(
            1,
            productionRunId,
            workspace.CurrentProject.ProjectId.Value,
            imported.PanelId,
            "markers",
            "1",
            imported.Original.Sha256,
            new WorkflowVisionModel(
                "project-marker-center",
                "1.0.0",
                new string('b', 64),
                "cpu"),
            new WorkflowVisionTiming(1, 2, 3, 6),
            0.95,
            ["Text and axis masks were applied."],
            [identityTransform]);
        var exportEvidence = new ProductionPanelExportEvidence(
            new ExportCalibration(ExportCalibrationStatus.Valid, true, true, true, 1, 1),
            [new ExportPhase(phaseId.Value, 1, "a", ExportPhaseType.Baseline, "Baseline", 0, 1, 0.95)],
            [new ExportSeries(seriesId.Value, "●", "Detected", ExportSeriesRole.Intervention, [pointId], 0.95)],
            [new ExportSeriesRelation(seriesId.Value, null)],
            [new ProductionPointExportEvidence(pointId, markerId.Value, 1, 1, null, ExportXValueSource.Printed, 1, 1)],
            [envelope],
            projectionEvidence: new ProductionPanelProjectionEvidence(
                calibration,
                [phase],
                [series],
                [domainPoint],
                [transform],
                [ocrEvidence],
                [marker],
                "Participant fixture"));
        store.SetExportEvidence(imported.PanelId, exportEvidence);
        var reviewPanel = new WorkflowReviewPanel(prepared, [point], [envelope]);
        var result = new WorkflowRunResult(productionRunId, new WorkflowReviewState(
            workspace.CurrentProject.ProjectId.Value,
            [reviewPanel],
            warnings: ["Production review warning retained."]),
            [new WorkflowStepRecord(WorkflowStep.Detect, TimeSpan.FromMilliseconds(7), 1)]);

        ProductionReviewProjectionResult firstProjection = workspace.ProjectWithEvidence(result, store);
        Assert.IsTrue(firstProjection.Succeeded);
        Assert.AreEqual(1, firstProjection.ProjectedPointCount);
        Assert.AreEqual(
            WorkflowReviewStatus.Accepted,
            firstProjection.ProjectedRun!.Review.Panels.Single().Points.Single().ReviewStatus,
            "An untouched first-run detection must remain Accepted after project alignment.");
        Assert.HasCount(1, tab.SeriesCards);
        Assert.HasCount(1, tab.Points);
        Assert.IsNotNull(tab.Calibration);
        Assert.AreSame(immutableImage, tab.ImageSource);
        PanelRecord projectedPanel = workspace.CurrentProject.Panels.Single();
        Assert.IsTrue(
            JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(calibration),
                JsonSerializer.SerializeToElement(projectedPanel.Calibration)),
            "Projection must preserve every calibration value even when mapping creates new nested collections.");
        Assert.AreEqual(phase.Code, projectedPanel.Phases.Single().Code);
        Assert.AreEqual(series.SeriesId, projectedPanel.Series.Single().SeriesId);
        Assert.AreEqual(series.Shape, projectedPanel.Series.Single().Shape);
        Assert.AreEqual(series.Fill, projectedPanel.Series.Single().Fill);
        Assert.IsTrue(
            JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(domainPoint),
                JsonSerializer.SerializeToElement(projectedPanel.Points.Single())),
            "Projection must preserve the exact point and modification evidence values.");
        Assert.AreEqual("Participant fixture", projectedPanel.Participant);
        AssertSourceBoundTransformChain(projectedPanel, transform);
        Assert.AreEqual(ocrRegionId, projectedPanel.OcrRegions.Single().RegionId);
        Assert.AreEqual(markerId, projectedPanel.Markers.Single().MarkerId);
        Assert.AreEqual(imported.Original.Sha256, tab.SourceSha256);
        AuditEvent projectionAudit = workspace.CurrentProject.Audit.Events.Single(auditEvent =>
            auditEvent.Details is JsonElement details &&
            details.TryGetProperty("kind", out JsonElement kind) &&
            kind.GetString() == "production_review_projection");
        JsonElement projectionDetails = projectionAudit.Details!.Value;
        Assert.AreEqual(productionRunId.ToString("D"), projectionDetails.GetProperty("run_id").GetString());
        JsonElement persistedEnvelope = projectionDetails.GetProperty("vision_provenance")
            .EnumerateArray()
            .Single();
        Assert.AreEqual("markers", persistedEnvelope.GetProperty("stage").GetString());
        Assert.AreEqual(imported.Original.Sha256, persistedEnvelope.GetProperty("input_sha256").GetString());
        Assert.AreEqual("original_pixels", persistedEnvelope.GetProperty("coordinate_space").GetString());
        Assert.AreEqual(0.95, persistedEnvelope.GetProperty("confidence").GetDouble(), 0);
        JsonElement persistedModel = persistedEnvelope.GetProperty("model");
        Assert.AreEqual("project-marker-center", persistedModel.GetProperty("model_id").GetString());
        Assert.AreEqual(new string('b', 64), persistedModel.GetProperty("sha256").GetString());
        Assert.AreEqual("cpu", persistedModel.GetProperty("provider").GetString());
        Assert.AreEqual(2, persistedEnvelope.GetProperty("timing")
            .GetProperty("inference_milliseconds").GetDouble(), 0);
        Assert.AreEqual(
            "Text and axis masks were applied.",
            persistedEnvelope.GetProperty("warnings").EnumerateArray().Single().GetString());
        JsonElement persistedTransform = persistedEnvelope.GetProperty("transforms")
            .EnumerateArray()
            .Single();
        Assert.AreEqual("marker-original-identity", persistedTransform.GetProperty("transform_id").GetString());
        Assert.IsFalse(persistedTransform.GetProperty("lossy").GetBoolean());
        Assert.HasCount(9, persistedTransform.GetProperty("output_to_input_matrix").EnumerateArray());
        JsonElement persistedStep = projectionDetails.GetProperty("workflow_steps").EnumerateArray().Single();
        Assert.AreEqual(nameof(WorkflowStep.Detect), persistedStep.GetProperty("step").GetString());
        Assert.AreEqual(7, persistedStep.GetProperty("elapsed_milliseconds").GetDouble(), 0);
        Assert.AreEqual(
            "Production review warning retained.",
            projectionDetails.GetProperty("workflow_warnings").EnumerateArray().Single().GetString());
        string provenanceProjectPath = Path.Combine(directory.Path, "production-provenance.garproj");
        DomainResult<ProjectSaveReceipt> provenanceSave = await workspace.SaveProjectAsync(
            provenanceProjectPath,
            CancellationToken.None);
        Assert.IsTrue(
            provenanceSave.IsSuccess,
            string.Join(" | ", provenanceSave.Errors.Select(static error => error.TechnicalMessage)));
        var reopenedProvenanceWorkspace = new ProjectionWorkspace();
        await reopenedProvenanceWorkspace.OpenProjectAsync(
            provenanceProjectPath,
            CancellationToken.None);
        JsonElement reopenedProjectionDetails = reopenedProvenanceWorkspace.CurrentProject.Audit.Events
            .Single(auditEvent => auditEvent.Details is JsonElement details &&
                details.TryGetProperty("kind", out JsonElement kind) &&
                kind.GetString() == "production_review_projection")
            .Details!.Value;
        Assert.IsTrue(JsonElement.DeepEquals(projectionDetails, reopenedProjectionDetails));
        PanelRecord persistedProjectedPanel = workspace.CurrentProject.Panels.Single();
        Assert.AreEqual("Participant fixture", persistedProjectedPanel.Participant);
        AssertSourceBoundTransformChain(persistedProjectedPanel, transform);
        Assert.AreEqual(ocrRegionId, persistedProjectedPanel.OcrRegions.Single().RegionId);
        Assert.AreEqual(markerId, persistedProjectedPanel.Markers.Single().MarkerId);
        Assert.AreEqual(calibration.CalibrationId, persistedProjectedPanel.Calibration!.CalibrationId);
        Assert.AreEqual("markers", persistedProjectedPanel.Points.Single().SourceStage);
        Assert.AreEqual("1", persistedProjectedPanel.Points.Single().ModelVersion);
        Assert.AreEqual(ReviewStatus.Accepted, persistedProjectedPanel.Points.Single().ReviewStatus);
        PanelRecord reopenedPersistedPanel = reopenedProvenanceWorkspace.CurrentProject.Panels.Single();
        AssertSourceBoundTransformChain(reopenedPersistedPanel, transform);
        Assert.AreEqual(ocrRegionId, reopenedPersistedPanel.OcrRegions.Single().RegionId);
        Assert.AreEqual(markerId, reopenedPersistedPanel.Markers.Single().MarkerId);
        Assert.AreEqual("markers", reopenedPersistedPanel.Points.Single().SourceStage);
        Assert.AreEqual("1", reopenedPersistedPanel.Points.Single().ModelVersion);
        string productionExportRoot = Path.Combine(directory.Path, "production-export");
        ExportResult productionExport = await workspace.ExportAsync(
            tab.TabId,
            productionExportRoot,
            CancellationToken.None);
        Assert.IsTrue(
            productionExport.Succeeded,
            string.Join(" | ", productionExport.Failures.Select(static failure => failure.TechnicalMessage)));
        ExtendedAuditRow exportedAuditRow = productionExport.AuditArtifacts
            .SelectMany(static artifact => artifact.Rows)
            .First();
        Assert.AreEqual("markers", exportedAuditRow.SourceStage);
        Assert.AreEqual("1", exportedAuditRow.ModelVersion);
        Assert.AreEqual(ExportReviewStatus.Accepted, exportedAuditRow.ReviewStatus);
        persistedProjectedPanel = workspace.CurrentProject.Panels.Single();
        AssertSourceBoundTransformChain(persistedProjectedPanel, transform);
        Assert.AreEqual(ocrRegionId, persistedProjectedPanel.OcrRegions.Single().RegionId);
        Assert.AreEqual(markerId, persistedProjectedPanel.Markers.Single().MarkerId);
        Assert.AreEqual("markers", persistedProjectedPanel.Points.Single().SourceStage);
        Assert.AreEqual("1", persistedProjectedPanel.Points.Single().ModelVersion);

        var mismatchedPoint = point with { Shape = "square" };
        var mismatch = new WorkflowRunResult(Guid.NewGuid(), new WorkflowReviewState(
            workspace.CurrentProject.ProjectId.Value,
            [new WorkflowReviewPanel(prepared, [mismatchedPoint], [envelope])]), []);
        double beforeX = tab.Points[0].PixelX;
        Assert.AreEqual(0, workspace.Project(mismatch, store));
        Assert.AreEqual(beforeX, tab.Points[0].PixelX);
        Assert.HasCount(1, tab.SeriesCards);
        Assert.HasCount(1, tab.Points);
        Assert.AreSame(immutableImage, tab.ImageSource);
        Assert.AreSame(persistedProjectedPanel, workspace.CurrentProject.Panels.Single());

        Guid secondPointId = Guid.NewGuid();
        SeriesId secondSeriesId = SeriesId.New();
        var secondSeries = new SeriesRecord(
            secondSeriesId, "□", MarkerShape.Square, MarkerFill.Open, "Second", SemanticRole.Intervention,
            null, [PointId.FromGuid(secondPointId)], 0.94, null, [], false);
        var secondPoint = new WorkflowPoint(
            secondPointId.ToString("D"), "key-2", 0.75, 0.25, 0.94, WorkflowImageVariant.Original,
            WorkflowReviewStatus.Accepted, "□", "square", "open", secondSeriesId.Value.ToString("D"),
            phaseId.Value.ToString("D"), 2, 75, "markers", "1", false);
        var secondDomainPoint = new PointRecord(
            PointId.FromGuid(secondPointId), null, secondSeriesId, phaseId,
            new GraphReader.Domain.PixelPoint(0.75, 0.25), 2, 75, 2, 2, null,
            PointXSource.Printed, 1, 1, 0.94, "markers", "1", ReviewStatus.Accepted, []);
        var swappedMembershipEvidence = new ProductionPanelExportEvidence(
            exportEvidence.Calibration,
            exportEvidence.Phases,
            [
                exportEvidence.Series.Single(),
                new ExportSeries(secondSeriesId.Value, "□", "Second", ExportSeriesRole.Intervention,
                    [secondPointId], 0.94),
            ],
            [
                exportEvidence.Relations.Single(),
                new ExportSeriesRelation(secondSeriesId.Value, null),
            ],
            [
                exportEvidence.Points.Single(),
                new ProductionPointExportEvidence(
                    secondPointId, null, 2, 2, null, ExportXValueSource.Printed, 1, 1),
            ],
            exportEvidence.Provenance,
            projectionEvidence: new ProductionPanelProjectionEvidence(
                calibration,
                [phase],
                [
                    series with { PointIds = [PointId.FromGuid(secondPointId)] },
                    secondSeries with { PointIds = [PointId.FromGuid(pointId)] },
                ],
                [domainPoint, secondDomainPoint]));
        store.SetExportEvidence(imported.PanelId, swappedMembershipEvidence);
        var swappedMembershipResult = new WorkflowRunResult(Guid.NewGuid(), new WorkflowReviewState(
            workspace.CurrentProject.ProjectId.Value,
            [new WorkflowReviewPanel(prepared, [point, secondPoint], [envelope])]), []);
        Assert.AreEqual(0, workspace.Project(swappedMembershipResult, store));
        Assert.AreSame(persistedProjectedPanel, workspace.CurrentProject.Panels.Single(),
            "Swapped per-series membership must fail before any panel mutation.");
        Assert.HasCount(1, tab.SeriesCards);
        Assert.HasCount(1, tab.Points);
        Assert.AreSame(immutableImage, tab.ImageSource);

        store.SetExportEvidence(imported.PanelId, exportEvidence);
        store.SetExportEvidence(
            imported.PanelId,
            new ProductionPanelExportEvidence(
                exportEvidence.Calibration,
                exportEvidence.Phases,
                exportEvidence.Series,
                exportEvidence.Relations,
                exportEvidence.Points.Append(new ProductionPointExportEvidence(
                    Guid.NewGuid(), null, 2, 2, null, ExportXValueSource.Printed, 1, 1)),
                exportEvidence.Provenance,
                projectionEvidence: exportEvidence.ProjectionEvidence));
        Assert.AreEqual(0, workspace.Project(result, store));
        Assert.AreSame(persistedProjectedPanel, workspace.CurrentProject.Panels.Single());
        store.SetExportEvidence(imported.PanelId, exportEvidence);

        workspace.MovePoint(tab.TabId, pointId.ToString("D"), 0.75, 0.75);
        Assert.AreEqual(0, workspace.Project(result, store), "A corrected point must not be replaced on rerun.");
        Assert.AreEqual(0.75, tab.Points.Single().PixelX, 0);
        Assert.HasCount(1, workspace.CurrentProject.Panels.Single().Points.Single().ModificationHistory);
        Assert.AreSame(immutableImage, tab.ImageSource);
    }

    [TestMethod]
    public async Task ApprovedAutomaticSinglePanelMismatchPreservesPriorRunAndWorkspaceState()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = await WriteSourceAsync(directory.Path);
        var store = new ProductionWorkflowPanelStore();
        WorkflowRunResult? nextResult = null;
        var workspace = new ProductionWorkspaceService(
            automaticStages: ApprovedAutomaticStages(),
            automaticWorkflow: (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(nextResult ?? throw new AssertFailedException("The test workflow result was not assigned."));
            },
            panelStore: store);
        GraphReader.App.ViewModels.WorkspaceTabViewModel tab =
            (await workspace.ImportImagesAsync([imagePath], CancellationToken.None)).Single();
        SeedManualReviewState(workspace, tab);
        WorkflowReviewPanel exactPanel = await CreateExactReviewPanelAsync(workspace, tab, imagePath, store, true);
        nextResult = new WorkflowRunResult(Guid.NewGuid(), new WorkflowReviewState(
            workspace.CurrentProject.ProjectId.Value, [exactPanel]), []);

        WorkflowRunResult accepted = await workspace.RunAutomaticDetectionAsync(CancellationToken.None);
        Assert.AreSame(accepted, workspace.LastAutomaticRun);
        WorkspaceStateSnapshot before = CaptureWorkspace(workspace);

        WorkflowPoint exactPoint = exactPanel.Points.Single();
        WorkflowPoint mismatchedPoint = exactPoint with
        {
            Shape = string.Equals(exactPoint.Shape, "circle", StringComparison.OrdinalIgnoreCase)
                ? "square"
                : "circle",
        };
        nextResult = new WorkflowRunResult(Guid.NewGuid(), new WorkflowReviewState(
            workspace.CurrentProject.ProjectId.Value,
            [new WorkflowReviewPanel(exactPanel.PreparedPanel, [mismatchedPoint], exactPanel.DetectionProvenance)]), []);

        ProductionWorkflowStageException exception = await Assert.ThrowsAsync<ProductionWorkflowStageException>(
            () => workspace.RunAutomaticDetectionAsync(CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.ReviewProjectionRejected, exception.Failure.Code);
        Assert.AreSame(accepted, workspace.LastAutomaticRun, "A rejected rerun must preserve the prior accepted run.");
        AssertWorkspaceUnchanged(workspace, before);
    }

    [TestMethod]
    public async Task ApprovedAutomaticMultiPanelPartialMatchRejectsBeforeAnyPanelMutation()
    {
        using var directory = new TemporaryDirectory();
        string firstDirectory = Path.Combine(directory.Path, "first");
        string secondDirectory = Path.Combine(directory.Path, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        string firstPath = await WriteSourceAsync(firstDirectory);
        string secondPath = await WriteSourceAsync(secondDirectory);
        var store = new ProductionWorkflowPanelStore();
        WorkflowRunResult? nextResult = null;
        var workspace = new ProductionWorkspaceService(
            automaticStages: ApprovedAutomaticStages(),
            automaticWorkflow: (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(nextResult ?? throw new AssertFailedException("The test workflow result was not assigned."));
            },
            panelStore: store);
        GraphReader.App.ViewModels.WorkspaceTabViewModel[] tabs =
            (await workspace.ImportImagesAsync([firstPath, secondPath], CancellationToken.None)).ToArray();
        Assert.HasCount(2, tabs);
        SeedManualReviewState(workspace, tabs[0]);
        SeedManualReviewState(workspace, tabs[1]);
        WorkflowReviewPanel exactFirst = await CreateExactReviewPanelAsync(
            workspace, tabs[0], firstPath, store, retainProjection: true);
        WorkflowReviewPanel missingSecond = await CreateExactReviewPanelAsync(
            workspace, tabs[1], secondPath, store, retainProjection: false);
        nextResult = new WorkflowRunResult(Guid.NewGuid(), new WorkflowReviewState(
            workspace.CurrentProject.ProjectId.Value, [exactFirst, missingSecond]), []);
        WorkspaceStateSnapshot before = CaptureWorkspace(workspace);

        ProductionWorkflowStageException exception = await Assert.ThrowsAsync<ProductionWorkflowStageException>(
            () => workspace.RunAutomaticDetectionAsync(CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.ReviewProjectionRejected, exception.Failure.Code);
        StringAssert.Contains(exception.Failure.TechnicalMessage, missingSecond.PanelId.ToString("D"));
        Assert.IsNull(workspace.LastAutomaticRun);
        AssertWorkspaceUnchanged(workspace, before);
    }

    [TestMethod]
    public async Task ApprovedProductionBatchCancellationKeepsCompletedPanelCheckpoint()
    {
        using var directory = new TemporaryDirectory();
        string firstDirectory = Path.Combine(directory.Path, "checkpoint-first");
        string secondDirectory = Path.Combine(directory.Path, "checkpoint-second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        string firstPath = await WriteSourceAsync(firstDirectory);
        string secondPath = await WriteSourceAsync(secondDirectory);
        string firstHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(firstPath)));
        string secondHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(secondPath)));
        var store = new ProductionWorkflowPanelStore();
        var expectedPanels = new Dictionary<Guid, WorkflowReviewPanel>();
        using var cancellation = new CancellationTokenSource();
        var detector = new CheckpointDetectionStage(expectedPanels, cancellation);
        var orchestrator = new WorkflowOrchestrator(new WorkflowServiceSet(
            new ProductionWorkflowImportStage(store, new ImageImportService()),
            new ProductionWorkflowPrepareStage(store),
            detector,
            new UnusedWorkflowExportStage()));
        var workspace = new ProductionWorkspaceService(
            automaticStages: ApprovedAutomaticStages(),
            workflowOrchestrator: orchestrator,
            panelStore: store);
        WorkspaceTabViewModel[] tabs = (await workspace.ImportImagesAsync(
            [firstPath, secondPath],
            CancellationToken.None)).ToArray();
        Assert.HasCount(2, tabs);
        SeedManualReviewState(workspace, tabs[0]);
        SeedManualReviewState(workspace, tabs[1]);
        WorkflowReviewPanel firstPanel = await CreateExactReviewPanelAsync(
            workspace, tabs[0], firstPath, store, retainProjection: true);
        WorkflowReviewPanel secondPanel = await CreateExactReviewPanelAsync(
            workspace, tabs[1], secondPath, store, retainProjection: true);
        expectedPanels.Add(firstPanel.PanelId, firstPanel);
        expectedPanels.Add(secondPanel.PanelId, secondPanel);
        detector.CancelBeforePanelId = secondPanel.PanelId;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            workspace.RunAutomaticDetectionAsync(cancellation.Token));

        Assert.IsNotNull(workspace.LastAutomaticRun);
        Assert.HasCount(1, workspace.LastAutomaticRun.Review.Panels);
        Assert.AreEqual(firstPanel.PanelId, workspace.LastAutomaticRun.Review.Panels.Single().PanelId);
        Assert.AreEqual(2, detector.CallCount);
        Assert.AreEqual(firstHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(firstPath))));
        Assert.AreEqual(secondHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(secondPath))));
        Assert.IsTrue(workspace.CurrentProject.Audit.Events.Any(auditEvent =>
            string.Equals(
                auditEvent.Note,
                "Approved production workflow results projected by source and checksum identity",
                StringComparison.Ordinal)));

        detector.CancelBeforePanelId = Guid.Empty;
        WorkflowRunResult completed = await workspace.RunAutomaticDetectionAsync(CancellationToken.None);
        CollectionAssert.AreEquivalent(
            new[] { firstPanel.PanelId, secondPanel.PanelId },
            completed.Review.Panels.Select(static panel => panel.PanelId).ToArray());
        CollectionAssert.AreEqual(
            new[] { WorkflowStep.Import, WorkflowStep.Prepare, WorkflowStep.Detect, WorkflowStep.Review },
            completed.Steps.Select(static step => step.Step).ToArray());
        Assert.AreSame(completed, workspace.LastAutomaticRun);
    }

    [TestMethod]
    public async Task ApprovedAutomaticRerunPreservesDeleteMoveAddAndReassignAfterReopen()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = await WriteSourceAsync(directory.Path);
        string projectPath = Path.Combine(directory.Path, "correction-replay.garproj");
        var firstStore = new ProductionWorkflowPanelStore();
        WorkflowRunResult? firstNextResult = null;
        var firstWorkspace = new ProductionWorkspaceService(
            automaticStages: ApprovedAutomaticStages(),
            automaticWorkflow: (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(firstNextResult ?? throw new AssertFailedException("The first test workflow result was not assigned."));
            },
            panelStore: firstStore);
        WorkspaceTabViewModel firstTab =
            (await firstWorkspace.ImportImagesAsync([imagePath], CancellationToken.None)).Single();
        SeedManualReviewState(firstWorkspace, firstTab);
        SeriesCardViewModel detectedSeries = firstTab.SeriesCards.Single();
        GraphReader.App.Models.GraphPoint deletedDetection = firstWorkspace.AddPoint(
            firstTab.TabId,
            detectedSeries.SeriesId,
            0.2,
            0.25);
        WorkflowReviewPanel exactPanel = await CreateExactReviewPanelAsync(
            firstWorkspace, firstTab, imagePath, firstStore, retainProjection: true);
        ProductionPanelExportEvidence fullEvidence = firstStore.Get(exactPanel.PanelId).ExportEvidence!;
        firstNextResult = new WorkflowRunResult(Guid.NewGuid(), new WorkflowReviewState(
            firstWorkspace.CurrentProject.ProjectId.Value, [exactPanel]), []);
        await firstWorkspace.RunAutomaticDetectionAsync(CancellationToken.None);

        string movedPointId = firstTab.Points.Single(point => !string.Equals(
            point.PointId,
            deletedDetection.PointId,
            StringComparison.OrdinalIgnoreCase)).PointId;
        firstWorkspace.MovePoint(firstTab.TabId, movedPointId, 0.52, 0.48);
        SeriesCardViewModel manualSeries = firstWorkspace.AddSeries(
            firstTab.TabId,
            new ManualSeriesDefinition(
                "Manual retained series",
                "□",
                MarkerShape.Square,
                MarkerFill.Open,
                SemanticRole.Intervention));
        GraphReader.App.Models.GraphPoint manualPoint = firstWorkspace.AddPoint(
            firstTab.TabId,
            detectedSeries.SeriesId,
            0.72,
            0.68);
        firstWorkspace.ReassignPoint(firstTab.TabId, manualPoint.PointId, manualSeries.SeriesId);
        firstWorkspace.DeletePoint(firstTab.TabId, deletedDetection.PointId);
        GraphReader.App.Models.EditablePhaseDivider manualDivider = firstTab.PhaseDividers.Single();
        firstWorkspace.MovePhaseDivider(firstTab.TabId, manualDivider.DividerId, 0.5);
        firstWorkspace.LabelPhaseDivider(firstTab.TabId, manualDivider.DividerId, "c", "Manual phase");
        Assert.IsTrue(firstWorkspace.CurrentProject.Audit.Events.Any(auditEvent =>
            auditEvent.Details is JsonElement details &&
            details.TryGetProperty("kind", out JsonElement kind) &&
            kind.GetString() == "production_point_deleted"));
        DomainResult<ProjectSaveReceipt> saved = await firstWorkspace.SaveProjectAsync(
            projectPath,
            CancellationToken.None);
        Assert.IsTrue(saved.IsSuccess, string.Join(" | ", saved.Errors.Select(static error => error.TechnicalMessage)));

        var reopenedStore = new ProductionWorkflowPanelStore();
        WorkflowRunResult? reopenedNextResult = null;
        WorkflowReviewState? observedPreviousReview = null;
        ProductionPanelExportEvidence? automaticEvidence = null;
        Guid automaticPanelId = Guid.Empty;
        bool reapplyPreviousCorrections = false;
        var reopenedWorkspace = new ProductionWorkspaceService(
            automaticStages: ApprovedAutomaticStages(),
            automaticWorkflow: (previousReview, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                observedPreviousReview = previousReview;
                if (automaticEvidence is not null && automaticPanelId != Guid.Empty)
                {
                    reopenedStore.SetExportEvidence(automaticPanelId, automaticEvidence);
                }
                WorkflowRunResult automatic = reopenedNextResult ??
                    throw new AssertFailedException("The reopened test workflow result was not assigned.");
                if (!reapplyPreviousCorrections || previousReview is null)
                {
                    return Task.FromResult(automatic);
                }

                WorkflowReviewPanel automationPanel = automatic.Review.Panels.Single();
                WorkflowReviewPanel replayed = ManualCorrectionOverlay.Reapply(
                    automationPanel,
                    previousReview.Panels.Single(),
                    previousReview.CorrectionJournal);
                return Task.FromResult(new WorkflowRunResult(
                    automatic.RunId,
                    new WorkflowReviewState(
                        automatic.Review.ProjectId,
                        [replayed],
                        previousReview.CorrectionJournal,
                        automatic.Review.Warnings),
                    automatic.Steps));
            },
            panelStore: reopenedStore);
        WorkspaceTabViewModel reopenedTab =
            (await reopenedWorkspace.OpenProjectAsync(projectPath, CancellationToken.None)).Single();
        GraphReader.App.Models.GraphPoint movedBeforeRerun = reopenedTab.Points.Single(point =>
            string.Equals(point.PointId, movedPointId, StringComparison.OrdinalIgnoreCase));
        GraphReader.App.Models.GraphPoint manualBeforeRerun = reopenedTab.Points.Single(point =>
            string.Equals(point.PointId, manualPoint.PointId, StringComparison.OrdinalIgnoreCase));
        int movedHistoryCount = reopenedWorkspace.CurrentProject.Panels.Single().Points.Single(point =>
            point.PointId.Value.ToString("D") == movedPointId).ModificationHistory.Count;
        int manualHistoryCount = reopenedWorkspace.CurrentProject.Panels.Single().Points.Single(point =>
            point.PointId.Value.ToString("D") == manualPoint.PointId).ModificationHistory.Count;

        WorkflowImportedPanel reopenedImported = await ImportOneAsync(
            reopenedStore,
            reopenedWorkspace.CurrentProject.ProjectId.Value,
            reopenedWorkspace.CurrentProject.Sources.Single().SourceId.Value,
            imagePath);
        reopenedStore.SetExportEvidence(reopenedImported.PanelId, fullEvidence);
        automaticEvidence = fullEvidence;
        automaticPanelId = reopenedImported.PanelId;
        var reopenedReviewPanel = new WorkflowReviewPanel(
            new WorkflowPreparedPanel(reopenedImported, reopenedImported.Original, enhanced: null),
            exactPanel.Points);
        reopenedNextResult = new WorkflowRunResult(Guid.NewGuid(), new WorkflowReviewState(
            reopenedWorkspace.CurrentProject.ProjectId.Value, [reopenedReviewPanel]), []);

        WorkflowRunResult correctedRun = await reopenedWorkspace.RunAutomaticDetectionAsync(CancellationToken.None);

        Assert.IsFalse(reopenedTab.Points.Any(point => string.Equals(
            point.PointId,
            deletedDetection.PointId,
            StringComparison.OrdinalIgnoreCase)), "A deleted detected point must not be resurrected.");
        GraphReader.App.Models.GraphPoint movedAfterRerun = reopenedTab.Points.Single(point =>
            string.Equals(point.PointId, movedPointId, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(movedBeforeRerun.PixelX, movedAfterRerun.PixelX, 0);
        Assert.AreEqual(movedBeforeRerun.PixelY, movedAfterRerun.PixelY, 0);
        GraphReader.App.Models.GraphPoint manualAfterRerun = reopenedTab.Points.Single(point =>
            string.Equals(point.PointId, manualPoint.PointId, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(manualBeforeRerun.SeriesId, manualAfterRerun.SeriesId);
        Assert.AreEqual(manualSeries.SeriesId, manualAfterRerun.SeriesId);
        Assert.IsTrue(reopenedTab.SeriesCards.Any(series => string.Equals(
            series.SeriesId,
            manualSeries.SeriesId,
            StringComparison.OrdinalIgnoreCase)));
        PanelRecord correctedPanel = reopenedWorkspace.CurrentProject.Panels.Single();
        Assert.AreEqual(movedHistoryCount, correctedPanel.Points.Single(point =>
            point.PointId.Value.ToString("D") == movedPointId).ModificationHistory.Count);
        Assert.AreEqual(manualHistoryCount, correctedPanel.Points.Single(point =>
            point.PointId.Value.ToString("D") == manualPoint.PointId).ModificationHistory.Count);
        WorkflowCorrection[] journal = correctedRun.Review.CorrectionJournal.ToArray();
        DeleteWorkflowPointCorrection deletion = journal.OfType<DeleteWorkflowPointCorrection>().Single(correction =>
            correction.TargetPointId == deletedDetection.PointId);
        Assert.AreEqual(deletedDetection.PointId, deletion.TargetPointId);
        WorkflowReviewPanel correctedReviewPanel = correctedRun.Review.Panels.Single();
        Assert.IsFalse(correctedReviewPanel.Points.Any(point => string.Equals(
            point.PointId,
            deletedDetection.PointId,
            StringComparison.OrdinalIgnoreCase)));
        WorkflowPoint correctedMoved = correctedReviewPanel.Points.Single(point => point.PointId == movedPointId);
        Assert.AreEqual(0.52, correctedMoved.OriginalPixelX, 0);
        Assert.AreEqual(0.48, correctedMoved.OriginalPixelY, 0);
        Assert.AreEqual(manualDivider.DividerId, correctedMoved.PhaseId);
        WorkflowPoint correctedManual = correctedReviewPanel.Points.Single(point => point.PointId == manualPoint.PointId);
        Assert.IsTrue(correctedManual.IsManual);
        Assert.AreEqual(0.72, correctedManual.OriginalPixelX, 0);
        Assert.AreEqual(0.68, correctedManual.OriginalPixelY, 0);
        Assert.AreEqual(manualSeries.SeriesId, correctedManual.SeriesId);
        Assert.AreEqual(manualDivider.DividerId, correctedManual.PhaseId);
        Assert.IsTrue(journal.OfType<MoveWorkflowPointCorrection>().Any(correction =>
            correction.TargetPointId == movedPointId &&
            correction.OriginalPixelX == 0.52 &&
            correction.OriginalPixelY == 0.48));
        Assert.IsTrue(journal.OfType<AddWorkflowPointCorrection>().Any(correction =>
            correction.Point.PointId == manualPoint.PointId));
        Assert.IsTrue(journal.OfType<ReassignWorkflowPointCorrection>().Any(correction =>
            correction.TargetPointId == manualPoint.PointId && correction.SeriesId == manualSeries.SeriesId));
        Assert.IsTrue(journal.OfType<AssignWorkflowPointPhaseCorrection>().Any(correction =>
            correction.TargetPointId == movedPointId && correction.PhaseId == manualDivider.DividerId));
        Assert.AreEqual(0.5, reopenedTab.PhaseDividers.Single().OriginalX, 0);
        Assert.AreEqual("c", reopenedTab.PhaseDividers.Single().Code);
        Assert.AreEqual("Manual phase", reopenedTab.PhaseDividers.Single().Label);
        Assert.AreEqual("c", correctedPanel.Phases.Single(phase => phase.Order == 2).Code);
        ProductionPanelExportEvidence correctedExport = reopenedStore.Get(reopenedImported.PanelId).ExportEvidence!;
        Assert.IsFalse(correctedExport.Points.Any(point => point.PointId == Guid.Parse(deletedDetection.PointId)));
        Assert.IsTrue(correctedExport.Points.Any(point => point.PointId == Guid.Parse(movedPointId)));
        Assert.IsTrue(correctedExport.Points.Any(point => point.PointId == Guid.Parse(manualPoint.PointId)));
        Assert.AreEqual("c", correctedExport.Phases.Single(phase => phase.Order == 2).Code);

        reopenedNextResult = new WorkflowRunResult(Guid.NewGuid(), new WorkflowReviewState(
            reopenedWorkspace.CurrentProject.ProjectId.Value, [reopenedReviewPanel]), []);
        reapplyPreviousCorrections = true;
        WorkflowRunResult secondCorrectedRun = await reopenedWorkspace.RunAutomaticDetectionAsync(CancellationToken.None);

        Assert.AreSame(correctedRun.Review, observedPreviousReview,
            "The next approved rerun must receive the fully corrected prior review.");
        WorkflowReviewPanel secondPanel = secondCorrectedRun.Review.Panels.Single();
        WorkflowPoint secondMoved = secondPanel.Points.Single(point => point.PointId == movedPointId);
        Assert.AreEqual(correctedMoved.OriginalPixelX, secondMoved.OriginalPixelX, 0);
        Assert.AreEqual(correctedMoved.OriginalPixelY, secondMoved.OriginalPixelY, 0);
        Assert.AreEqual(correctedMoved.GraphX, secondMoved.GraphX);
        Assert.AreEqual(correctedMoved.GraphY, secondMoved.GraphY);
        Assert.AreEqual(correctedMoved.SeriesId, secondMoved.SeriesId);
        Assert.AreEqual(correctedMoved.PhaseId, secondMoved.PhaseId);
        CollectionAssert.AreEqual(correctedMoved.CorrectionIds.ToArray(), secondMoved.CorrectionIds.ToArray());
        WorkflowPoint secondManual = secondPanel.Points.Single(point => point.PointId == manualPoint.PointId);
        Assert.AreEqual(correctedManual.OriginalPixelX, secondManual.OriginalPixelX, 0);
        Assert.AreEqual(correctedManual.OriginalPixelY, secondManual.OriginalPixelY, 0);
        Assert.AreEqual(correctedManual.SeriesId, secondManual.SeriesId);
        Assert.AreEqual(correctedManual.PhaseId, secondManual.PhaseId);
        Assert.AreEqual(correctedManual.IsManual, secondManual.IsManual);
        CollectionAssert.AreEqual(correctedManual.CorrectionIds.ToArray(), secondManual.CorrectionIds.ToArray());
        Assert.IsFalse(secondPanel.Points.Any(point => point.PointId == deletedDetection.PointId));
        CollectionAssert.AreEqual(
            journal.Select(static correction => correction.CorrectionId).ToArray(),
            secondCorrectedRun.Review.CorrectionJournal.Select(static correction => correction.CorrectionId).ToArray());
        Assert.AreEqual(0.5, reopenedTab.PhaseDividers.Single().OriginalX, 0);
        Assert.AreEqual("c", reopenedTab.PhaseDividers.Single().Code);
    }

    [TestMethod]
    public async Task ExportFailsClosedBeforeCallingServiceWhenScientificEvidenceIsMissing()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = await WriteSourceAsync(directory.Path);
        var store = new ProductionWorkflowPanelStore();
        WorkflowImportedPanel imported = await ImportOneAsync(store, imagePath);
        var prepared = new WorkflowPreparedPanel(imported, imported.Original, enhanced: null);
        var review = new WorkflowReviewState(
            Guid.Parse("10000000-0000-0000-0000-000000000003"),
            [new WorkflowReviewPanel(prepared, [])]);
        var export = new CountingExportService();
        var stage = new ProductionWorkflowExportStage(store, export);

        WorkflowExportResult result = await stage.ExportAsync(
            review,
            new WorkflowExportRequest(Guid.NewGuid(), directory.Path),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ProductionWorkflowFailureCodes.RecalibrationRequired, result.FailureCode);
        Assert.AreEqual(0, export.CallCount);
    }

    [TestMethod]
    public async Task ExportOverlaysReviewedPointAndWritesRealCsv()
    {
        string evidenceDirectory = GetEvidenceDirectory(
            Path.Combine("export", Guid.NewGuid().ToString("N")));
        string imagePath = await WriteSourceAsync(evidenceDirectory);
        Guid projectId = Guid.Parse("10000000-0000-0000-0000-000000000004");
        Guid sourceId = Guid.Parse("20000000-0000-0000-0000-000000000004");
        var store = new ProductionWorkflowPanelStore();
        WorkflowImportedPanel imported = await ImportOneAsync(store, projectId, sourceId, imagePath);
        Assert.AreEqual(1, imported.Original.Width);
        Assert.AreEqual(1, imported.Original.Height);
        var prepared = new WorkflowPreparedPanel(imported, imported.Original, enhanced: null);
        Guid pointId = Guid.Parse("30000000-0000-0000-0000-000000000004");
        Guid phaseId = Guid.Parse("40000000-0000-0000-0000-000000000004");
        Guid seriesId = Guid.Parse("50000000-0000-0000-0000-000000000004");
        Guid provenanceRunId = Guid.Parse("60000000-0000-0000-0000-000000000004");
        var point = new WorkflowPoint(
            pointId.ToString("D"),
            "detected-point-1",
            originalPixelX: 0.5,
            originalPixelY: 0.5,
            confidence: 0.98,
            WorkflowImageVariant.Original,
            WorkflowReviewStatus.Corrected,
            "●",
            "circle",
            "filled",
            seriesId.ToString("D"),
            phaseId.ToString("D"),
            graphX: 1,
            graphY: 42,
            sourceStage: "markers",
            modelVersion: "1.0",
            isManual: false);
        var provenance = new WorkflowVisionEnvelope(
            1,
            provenanceRunId,
            projectId,
            imported.PanelId,
            "markers",
            "1.0",
            imported.Original.Sha256,
            new WorkflowVisionModel("marker-model", "1.0", imported.Original.Sha256, "cpu"),
            new WorkflowVisionTiming(0, 0, 0, 0),
            0.98);
        store.SetExportEvidence(
            imported.PanelId,
            new ProductionPanelExportEvidence(
                new ExportCalibration(ExportCalibrationStatus.Valid, true, true, true, 1, 1),
                [new ExportPhase(phaseId, 1, "b", ExportPhaseType.Intervention, "Intervention", 0, 1, 1)],
                [new ExportSeries(seriesId, "●", "Intervention", ExportSeriesRole.Intervention, [pointId], 1)],
                [new ExportSeriesRelation(seriesId, null)],
                [new ProductionPointExportEvidence(
                    pointId,
                    MarkerId: null,
                    ObservationIndex: 1,
                    PrintedXValue: 1,
                    EstimatedXValue: null,
                    ExportXValueSource.Printed,
                    XConfidence: 1,
                    YConfidence: 1)],
                [provenance],
                participant: "Local Test"));
        var review = new WorkflowReviewState(
            projectId,
            [new WorkflowReviewPanel(prepared, [point], [provenance])]);
        var stage = new ProductionWorkflowExportStage(store, new ExportService());
        Guid exportRunId = Guid.Parse("70000000-0000-0000-0000-000000000004");

        WorkflowExportResult result = await stage.ExportAsync(
            review,
            new WorkflowExportRequest(exportRunId, evidenceDirectory),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, string.Join(" | ", result.Warnings));
        WorkflowExportArtifact minimal = result.Artifacts.Single(static artifact =>
            artifact.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
            !artifact.FileName.Contains("audit", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(minimal.WrittenPath);
        Assert.IsTrue(File.Exists(minimal.WrittenPath));
        Assert.AreEqual("x_value,y_value,phase\n1,42,b\n", await File.ReadAllTextAsync(minimal.WrittenPath));
        Assert.AreEqual(
            minimal.Sha256,
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(minimal.WrittenPath))).ToLowerInvariant());

        ProductionPanelExportEvidence retained = store.Get(imported.PanelId).ExportEvidence!;
        store.SetExportEvidence(
            imported.PanelId,
            new ProductionPanelExportEvidence(
                retained.Calibration,
                retained.Phases,
                retained.Series,
                relations: [],
                points: retained.Points,
                provenance: retained.Provenance,
                participant: retained.Participant,
                mode: retained.Mode,
                auditMode: retained.AuditMode,
                sessionOriginPolicy: retained.SessionOriginPolicy));
        WorkflowExportResult emptyRelationResult = await stage.ExportAsync(
            review,
            new WorkflowExportRequest(
                Guid.NewGuid(),
                GetEvidenceDirectory(Path.Combine("export-empty-relations", Guid.NewGuid().ToString("N")))),
            CancellationToken.None);
        Assert.AreNotEqual(
            ProductionWorkflowFailureCodes.RecalibrationRequired,
            emptyRelationResult.FailureCode,
            "An explicitly retained empty relation set must be delegated to the real export service.");
    }

    private static AutomaticStageStatus[] ApprovedAutomaticStages() =>
    [
        new("axis", AutomaticStageState.Approved, "Injected approved test adapter."),
        new("ocr", AutomaticStageState.Approved, "Injected approved test adapter."),
        new("markers", AutomaticStageState.Approved, "Injected approved test adapter."),
        new("legends", AutomaticStageState.Approved, "Injected approved test adapter."),
        new("phases", AutomaticStageState.Approved, "Injected approved test adapter."),
    ];

    private static void SeedManualReviewState(
        ProductionWorkspaceService workspace,
        WorkspaceTabViewModel tab)
    {
        workspace.Calibrate(
            tab.TabId,
            new ManualCalibrationRequest(
                new GraphReader.Axis.PixelPoint(0, 1),
                new GraphReader.Axis.PixelPoint(0, 0),
                new GraphReader.Axis.PixelPoint(1, 1),
                YMaximum: 100,
                XMaximum: 2));
        SeriesCardViewModel series = workspace.AddSeries(
            tab.TabId,
            new ManualSeriesDefinition(
                "Manual intervention",
                "●",
                MarkerShape.Circle,
                MarkerFill.Filled,
                SemanticRole.Intervention));
        workspace.AddPhaseDivider(tab.TabId, 0.6, "b", "Intervention");
        GraphReader.App.Models.GraphPoint point = workspace.AddPoint(tab.TabId, series.SeriesId, 0.4, 0.5);
        workspace.MovePoint(tab.TabId, point.PointId, 0.45, 0.5);
    }

    private static async Task<WorkflowReviewPanel> CreateExactReviewPanelAsync(
        ProductionWorkspaceService workspace,
        WorkspaceTabViewModel tab,
        string imagePath,
        ProductionWorkflowPanelStore store,
        bool retainProjection)
    {
        PanelRecord panel = workspace.CurrentProject.Panels.Single(candidate =>
            string.Equals(candidate.PanelId.Value.ToString("D"), tab.PanelId, StringComparison.OrdinalIgnoreCase));
        WorkflowImportedPanel imported = await ImportOneAsync(
            store,
            workspace.CurrentProject.ProjectId.Value,
            panel.SourceId.Value,
            imagePath);
        var prepared = new WorkflowPreparedPanel(imported, imported.Original, enhanced: null);
        Dictionary<SeriesId, SeriesRecord> seriesById = panel.Series.ToDictionary(static series => series.SeriesId);
        WorkflowPoint[] points = panel.Points.Select(point =>
        {
            SeriesRecord series = seriesById[point.SeriesId!.Value];
            return new WorkflowPoint(
                point.PointId.Value.ToString("D"),
                $"approved:{point.PointId.Value:D}",
                point.OriginalPixel.X,
                point.OriginalPixel.Y,
                point.PointConfidence,
                WorkflowImageVariant.Original,
                WorkflowReviewStatus.Accepted,
                series.Symbol,
                series.Shape.ToString(),
                series.Fill.ToString(),
                series.SeriesId.Value.ToString("D"),
                point.PhaseId!.Value.Value.ToString("D"),
                point.GraphX,
                point.GraphY,
                point.SourceStage,
                point.ModelVersion ?? "project-owned-test",
                isManual: false);
        }).ToArray();

        if (retainProjection)
        {
            store.SetExportEvidence(
                imported.PanelId,
                new ProductionPanelExportEvidence(
                    new ExportCalibration(ExportCalibrationStatus.Valid, true, true, true, 1, 1),
                    panel.Phases.Select(phase => new ExportPhase(
                        phase.PhaseId.Value,
                        phase.Order,
                        phase.Code,
                        ToExportPhaseType(phase.NormalizedType),
                        phase.LabelText,
                        phase.ScreenXMin,
                        phase.ScreenXMax,
                        phase.Confidence)),
                    panel.Series.Select(series => new ExportSeries(
                        series.SeriesId.Value,
                        series.Symbol,
                        series.DisplayName,
                        ToExportSeriesRole(series.SemanticRole),
                        series.PointIds.Select(static pointId => pointId.Value),
                        series.Confidence,
                        series.LegendText)),
                    panel.Series.Select(series => new ExportSeriesRelation(
                        series.SeriesId.Value,
                        series.SharedBaselineSeriesId?.Value,
                        series.ApplicableProbeSeriesIds.Select(static id => id.Value))),
                    panel.Points.Select(point => new ProductionPointExportEvidence(
                        point.PointId.Value,
                        point.MarkerId?.Value,
                        point.ObservationIndex,
                        point.PrintedXValue,
                        point.EstimatedXValue,
                        ToExportXSource(point.XSource),
                        point.XConfidence,
                        point.YConfidence)),
                    provenance: [],
                    participant: panel.Participant,
                    projectionEvidence: new ProductionPanelProjectionEvidence(
                        panel.Calibration!,
                        panel.Phases,
                        panel.Series,
                        panel.Points,
                        panel.Transforms,
                        panel.OcrRegions,
                        panel.Markers,
                        panel.Participant)));
        }

        return new WorkflowReviewPanel(prepared, points);
    }

    private static ExportPhaseType ToExportPhaseType(PhaseNormalizedType value) => value switch
    {
        PhaseNormalizedType.Baseline => ExportPhaseType.Baseline,
        PhaseNormalizedType.Intervention => ExportPhaseType.Intervention,
        PhaseNormalizedType.Maintenance => ExportPhaseType.Maintenance,
        PhaseNormalizedType.Generalization => ExportPhaseType.Generalization,
        _ => ExportPhaseType.Unknown,
    };

    private static ExportSeriesRole ToExportSeriesRole(SemanticRole value) => value switch
    {
        SemanticRole.Baseline => ExportSeriesRole.Baseline,
        SemanticRole.Intervention => ExportSeriesRole.Intervention,
        SemanticRole.Maintenance => ExportSeriesRole.Maintenance,
        SemanticRole.Generalization => ExportSeriesRole.Generalization,
        _ => ExportSeriesRole.Unknown,
    };

    private static ExportXValueSource ToExportXSource(PointXSource value) => value switch
    {
        PointXSource.Printed => ExportXValueSource.Printed,
        PointXSource.Estimated => ExportXValueSource.Estimated,
        PointXSource.ObservationOrder => ExportXValueSource.ObservationOrder,
        _ => ExportXValueSource.Unknown,
    };

    private static WorkspaceStateSnapshot CaptureWorkspace(ProductionWorkspaceService workspace) => new(
        workspace.CurrentProject,
        workspace.CreateWorkspace().Select(tab => new WorkspaceTabSnapshot(
            tab.TabId,
            tab.ImageSource,
            tab.SourceSha256,
            tab.Calibration,
            tab.Points.Select(static point =>
                $"{point.PointId}|{point.SeriesId}|{point.PixelX:R}|{point.PixelY:R}|{point.GraphX:R}|{point.GraphY:R}|{point.PhaseId}|{point.PhaseCode}|{point.ObservationIndex}").ToArray(),
            tab.SeriesCards.Select(static series =>
                $"{series.SeriesId}|{series.Symbol}|{series.AccessibleName}|{series.Label}|{series.Shape}|{series.Fill}|{series.SemanticRole}|{series.Count}").ToArray(),
            tab.PhaseDividers.Select(static divider =>
                $"{divider.DividerId}|{divider.OriginalX:R}|{divider.Code}|{divider.Label}").ToArray())).ToArray(),
        workspace.CurrentProject.Panels.SelectMany(static panel => panel.Points).SelectMany(static point =>
            point.ModificationHistory.Select(modification =>
                $"{point.PointId.Value:D}|{modification.EventId.Value:D}|{modification.OccurredUtc:O}|{modification.PreviousPixel}|{modification.PreviousGraph}|{modification.Reason}"))
            .ToArray());

    private static void AssertWorkspaceUnchanged(
        ProductionWorkspaceService workspace,
        WorkspaceStateSnapshot before)
    {
        Assert.AreSame(before.Project, workspace.CurrentProject, "The project document must not be replaced on rejection.");
        IReadOnlyList<WorkspaceTabViewModel> currentTabs = workspace.CreateWorkspace();
        Assert.AreEqual(before.Tabs.Length, currentTabs.Count);
        for (int index = 0; index < before.Tabs.Length; index++)
        {
            WorkspaceTabSnapshot expected = before.Tabs[index];
            WorkspaceTabViewModel actual = currentTabs[index];
            Assert.AreEqual(expected.TabId, actual.TabId);
            Assert.AreSame(expected.ImageSource, actual.ImageSource);
            Assert.AreEqual(expected.SourceSha256, actual.SourceSha256);
            Assert.AreEqual(expected.Calibration, actual.Calibration);
            CollectionAssert.AreEqual(expected.Points, actual.Points.Select(static point =>
                $"{point.PointId}|{point.SeriesId}|{point.PixelX:R}|{point.PixelY:R}|{point.GraphX:R}|{point.GraphY:R}|{point.PhaseId}|{point.PhaseCode}|{point.ObservationIndex}").ToArray());
            CollectionAssert.AreEqual(expected.Series, actual.SeriesCards.Select(static series =>
                $"{series.SeriesId}|{series.Symbol}|{series.AccessibleName}|{series.Label}|{series.Shape}|{series.Fill}|{series.SemanticRole}|{series.Count}").ToArray());
            CollectionAssert.AreEqual(expected.Dividers, actual.PhaseDividers.Select(static divider =>
                $"{divider.DividerId}|{divider.OriginalX:R}|{divider.Code}|{divider.Label}").ToArray());
        }

        CollectionAssert.AreEqual(
            before.Histories,
            workspace.CurrentProject.Panels.SelectMany(static panel => panel.Points).SelectMany(static point =>
                point.ModificationHistory.Select(modification =>
                    $"{point.PointId.Value:D}|{modification.EventId.Value:D}|{modification.OccurredUtc:O}|{modification.PreviousPixel}|{modification.PreviousGraph}|{modification.Reason}"))
                .ToArray());
    }

    private sealed record WorkspaceStateSnapshot(
        ProjectDocument Project,
        WorkspaceTabSnapshot[] Tabs,
        string[] Histories);

    private sealed record WorkspaceTabSnapshot(
        string TabId,
        ImageSource? ImageSource,
        string? SourceSha256,
        GraphReader.App.Models.ManualCalibrationState? Calibration,
        string[] Points,
        string[] Series,
        string[] Dividers);

    private static void AssertSourceBoundTransformChain(
        PanelRecord panel,
        TransformRecord expectedPreparation)
    {
        Assert.HasCount(2, panel.Transforms);
        TransformRecord crop = panel.Transforms.Single(transform =>
            transform.Parameters.ValueKind == JsonValueKind.Object &&
            transform.Parameters.TryGetProperty("schema", out JsonElement schema) &&
            schema.GetString() == "graphreader.raster_source_crop.v1");
        Assert.AreEqual(TransformKind.Crop, crop.Kind);
        Assert.AreEqual(CoordinateSpace.OriginalPixels, crop.SourceSpace);
        Assert.AreEqual(CoordinateSpace.PanelPixels, crop.TargetSpace);
        Assert.IsFalse(crop.Lossy);
        Assert.IsNotNull(crop.InverseMatrix3x3);

        TransformRecord preparation = panel.Transforms.Single(transform =>
            transform.TransformId == expectedPreparation.TransformId);
        Assert.AreEqual(expectedPreparation.Kind, preparation.Kind);
        Assert.AreEqual(crop.TargetSpace, preparation.SourceSpace);
        Assert.AreEqual(expectedPreparation.TargetSpace, preparation.TargetSpace);
        Assert.AreEqual(expectedPreparation.Lossy, preparation.Lossy);
        CollectionAssert.AreEqual(expectedPreparation.Matrix3x3.ToArray(), preparation.Matrix3x3.ToArray());
        CollectionAssert.AreEqual(
            expectedPreparation.InverseMatrix3x3!.ToArray(),
            preparation.InverseMatrix3x3!.ToArray());
        Assert.IsTrue(JsonElement.DeepEquals(expectedPreparation.Parameters, preparation.Parameters));

        double[] composed = MultiplyMatrix3x3(preparation.Matrix3x3, crop.Matrix3x3);
        CollectionAssert.AreEqual(expectedPreparation.Matrix3x3.ToArray(), composed,
            "The retained full-source crop followed by panel preparation must reproduce the original preparation mapping.");
    }

    private static double[] MultiplyMatrix3x3(
        IReadOnlyList<double> left,
        IReadOnlyList<double> right)
    {
        var result = new double[9];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                result[(row * 3) + column] = Enumerable.Range(0, 3)
                    .Sum(index => left[(row * 3) + index] * right[(index * 3) + column]);
            }
        }
        return result;
    }

    private static async Task<ProductionPanelEvidence> ImportOneAsync(
        Guid projectId,
        Guid sourceId,
        string imagePath)
    {
        var store = new ProductionWorkflowPanelStore();
        await ImportOneAsync(store, projectId, sourceId, imagePath);
        return store.Get(store.PanelIds.Single());
    }

    private static Task<WorkflowImportedPanel> ImportOneAsync(
        ProductionWorkflowPanelStore store,
        string imagePath) =>
        ImportOneAsync(
            store,
            Guid.Parse("10000000-0000-0000-0000-000000000003"),
            Guid.Parse("20000000-0000-0000-0000-000000000003"),
            imagePath);

    private static async Task<WorkflowImportedPanel> ImportOneAsync(
        ProductionWorkflowPanelStore store,
        Guid projectId,
        Guid sourceId,
        string imagePath)
    {
        var stage = new ProductionWorkflowImportStage(store, new ImageImportService());
        WorkflowImportSnapshot snapshot = await stage.ImportAsync(
            new WorkflowImportRequest(
                projectId,
                [new WorkflowSourceRequest(sourceId, WorkflowSourceKind.Image, imagePath)]),
            CancellationToken.None);
        return snapshot.Panels.Single();
    }

    private static async Task<string> WriteSourceAsync(string directory)
    {
        string path = Path.Combine(directory, "source.png");
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(OnePixelPng));
        return path;
    }

    private sealed class CancelSecondSourceObserver : IImageImportStageObserver
    {
        private readonly string secondSourcePath;
        private readonly CancellationTokenSource cancellation;

        public CancelSecondSourceObserver(string secondSourcePath, CancellationTokenSource cancellation)
        {
            this.secondSourcePath = Path.GetFullPath(secondSourcePath);
            this.cancellation = cancellation;
        }

        public void Observe(ImageImportStage stage, string path, CancellationToken cancellationToken)
        {
            if (stage == ImageImportStage.BeforeHash &&
                string.Equals(Path.GetFullPath(path), secondSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                cancellation.Cancel();
            }
        }
    }

    private static string GetEvidenceDirectory(string scenario)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "GraphAutoReader.slnx")))
        {
            current = current.Parent;
        }

        Assert.IsNotNull(current, "The repository root could not be located for retained test evidence.");
        string path = Path.Combine(
            current.FullName,
            "artifacts",
            "evidence",
            "production-workflow-adapters",
            scenario);
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] CreateAxisPng()
    {
        const int width = 96;
        const int height = 72;
        var pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int x = 10; x <= 86; x++)
        {
            pixels[(60 * width) + x] = 0;
        }

        for (int y = 10; y <= 60; y++)
        {
            pixels[(y * width) + 10] = 0;
        }

        BitmapSource source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Gray8,
            palette: null,
            pixels,
            stride: width);
        source.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] CreateSolidGrayPng(int width, int height, byte value)
    {
        var pixels = Enumerable.Repeat(value, checked(width * height)).ToArray();
        BitmapSource source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Gray8,
            palette: null,
            pixels,
            stride: width);
        source.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] CreateSolidBgrPng(byte blue, byte green, byte red)
    {
        byte[] pixels = [blue, green, red];
        BitmapSource source = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgr24,
            palette: null,
            pixels,
            stride: 3);
        source.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed class ArtifactMaskAdapter : IProductionArtifactMaskAdapter
    {
        public string AdapterId => "test-artifact-mask";

        public bool IsApproved => true;

        public Task<ProductionArtifactMaskEvidence> DetectAsync(
            ProductionWorkflowDetectionRequest request,
            ProductionDecodedRaster raster,
            ProductionDetectionMaskSeed seed,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(raster.InputSha256, seed.RasterSha256);
            Assert.IsTrue(seed.CopyOcrMask().Values.Span.Contains(1f));
            Assert.IsTrue(seed.CopyArtifactMask().Values.Span.Contains(1f));
            var mask = new float[checked(raster.Width * raster.Height)];
            mask[(40 * raster.Width) + 80] = 1;
            var envelope = new WorkflowVisionEnvelope(
                1,
                request.RunId,
                request.ProjectId,
                request.Panel.ImportedPanel.PanelId,
                "markers",
                "artifact-mask-test-v1",
                request.Image.Sha256,
                new WorkflowVisionModel(
                    "test-artifact-mask",
                    "1",
                    new string('e', 64),
                    "cpu"),
                new WorkflowVisionTiming(1, 1, 1, 3),
                1,
                ["test_fixture_artifact_evidence"]);
            return Task.FromResult(new ProductionArtifactMaskEvidence(
                raster.Width,
                raster.Height,
                raster.InputSha256,
                raster.Variant,
                envelope,
                mask,
                ["artifact_mask_scope:test_fixture_arrow_bracket_legend_intersection"]));
        }
    }

    private sealed class AxisCandidateProviderStub : Axis.ILineCandidateProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<IReadOnlyList<Axis.GeometryLineCandidate>> DetectLinesAsync(
            Axis.GrayscaleLineCandidateFrame frame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(96, frame.Width);
            Assert.AreEqual(72, frame.Height);
            Assert.AreEqual(96, frame.Stride);
            Assert.AreEqual(96 * 72, frame.Pixels.Length);
            CallCount++;
            IReadOnlyList<Axis.GeometryLineCandidate> candidates =
            [
                new(
                    "x-axis",
                    new Axis.GeometryLineSegment(
                        new GraphReader.Axis.PixelPoint(10, 60),
                        new GraphReader.Axis.PixelPoint(86, 60)),
                    Axis.LineCandidateSource.OpenCvLsd,
                    Strength: 1,
                    StrokeWidthPixels: 1),
                new(
                    "y-axis",
                    new Axis.GeometryLineSegment(
                        new GraphReader.Axis.PixelPoint(10, 60),
                        new GraphReader.Axis.PixelPoint(10, 10)),
                    Axis.LineCandidateSource.OpenCvLsd,
                    Strength: 1,
                    StrokeWidthPixels: 1),
            ];
            return ValueTask.FromResult(candidates);
        }
    }

    private sealed class MarkerClassificationServiceStub : MarkerClassification.IMarkerClassificationService
    {
        public int CallCount { get; private set; }

        public ValueTask<MarkerClassification.MarkerClassificationResult> ClassifyAsync(
            MarkerClassification.MarkerClassificationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Assert.HasCount(1, request.Markers);
            MarkerDetection.MarkerCenter marker = request.Markers[0];
            var classified = new MarkerClassification.ClassifiedMarker(
                marker,
                MarkerClassification.MarkerShape.Circle,
                MarkerClassification.MarkerFill.Open,
                "○",
                "Open circle",
                0.01,
                0.98,
                0.97,
                Enumerable.Repeat(0.25f, 12));
            var timing = new MarkerClassification.MarkerClassificationTiming(1, 2, 1, 4);
            var report = new MarkerClassification.MarkerClassificationBatchReport(
                0,
                1,
                InferenceProvider.Cpu,
                [new ProviderAttempt(InferenceProvider.Cpu, true, null)],
                timing,
                false,
                null);
            var result = new MarkerClassification.MarkerClassificationResult(
                MarkerClassification.MarkerClassificationContract.Version,
                Guid.NewGuid().ToString("D"),
                request.ProjectId,
                request.PanelId,
                MarkerClassification.MarkerClassificationContract.Stage,
                request.Options.StageVersion,
                request.InputSha256,
                MarkerClassification.MarkerClassificationContract.CoordinateSpace,
                [classified],
                timing,
                classified.Confidence,
                [],
                [report],
                new MarkerClassification.MarkerClassificationModelReport(
                    request.Model.ModelId,
                    request.Model.Version,
                    request.Model.Sha256,
                    InferenceProvider.Cpu),
                null);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MarkerDetectionServiceStub : MarkerDetection.IMarkerDetectionService
    {
        public int CallCount { get; private set; }

        public ValueTask<MarkerDetection.MarkerDetectionResult> DetectAsync(
            MarkerDetection.MarkerDetectionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var marker = new MarkerDetection.MarkerCenter(
                "center-1",
                new MarkerDetection.MarkerPoint(32, 28),
                4,
                0.01,
                0.96,
                MarkerDetection.MarkerSourceImage.Original);
            var timing = new MarkerDetection.MarkerDetectionTiming(1, 2, 1, 4);
            var frame = new MarkerDetection.MarkerFrameReport(
                MarkerDetection.MarkerSourceImage.Original,
                "cache-key",
                InferenceProvider.Cpu,
                [new ProviderAttempt(InferenceProvider.Cpu, true, null)],
                timing,
                RawCandidateCount: 1,
                AcceptedCandidateCount: 1,
                CacheHit: false,
                Failure: null);
            var result = new MarkerDetection.MarkerDetectionResult(
                MarkerDetection.MarkerContract.Version,
                Guid.NewGuid().ToString("D"),
                request.ProjectId,
                request.PanelId,
                MarkerDetection.MarkerContract.Stage,
                request.Options.StageVersion,
                request.InputSha256,
                MarkerDetection.MarkerContract.CoordinateSpace,
                [marker],
                timing,
                marker.CenterConfidence,
                [],
                [frame],
                new MarkerDetection.MarkerModelReport(
                    request.Model.ModelId,
                    request.Model.Version,
                    request.Model.Sha256,
                    InferenceProvider.Cpu),
                null);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class LegendReasoningServiceStub : LegendReasoning.ILegendReasoningService
    {
        public int CallCount { get; private set; }

        public Task<LegendReasoning.LegendReasoningResult> ResolveAsync(
            LegendReasoning.LegendReasoningRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new AssertFailedException("An unapproved legend adapter must not invoke its service.");
        }
    }

    private sealed class PhaseReasoningServiceStub : PhaseReasoning.IPhaseReasoningService
    {
        public int CallCount { get; private set; }

        public Task<PhaseReasoning.PhaseReasoningResult> ResolveAsync(
            PhaseReasoning.PhaseReasoningRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new AssertFailedException("An unapproved phase adapter must not invoke its service.");
        }
    }

    private sealed class EnhancementAdapter(bool isApproved) : IProductionWorkflowEnhancementAdapter
    {
        public string AdapterId => "test-approved-adapter";

        public bool IsApproved { get; } = isApproved;

        public int CallCount { get; private set; }

        public Task<ProductionWorkflowEnhancementResult> EnhanceAsync(
            ProductionWorkflowEnhancementRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            byte[] bytes = request.CopyOriginalBytes().Concat([(byte)0x2]).ToArray();
            string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var transform = new WorkflowTransformProvenance(
                Guid.Parse("80000000-0000-0000-0000-000000000004").ToString("D"),
                "original_pixels",
                "enhanced_pixels",
                [2, 0, 0, 0, 2, 0, 0, 0, 1],
                [0.5, 0, 0, 0, 0.5, 0, 0, 0, 1],
                lossy: false);
            return Task.FromResult(new ProductionWorkflowEnhancementResult(
                new ProductionWorkflowEnhancedImage(
                    "memory:test-enhanced",
                    sha256,
                    request.Original.Width * 2,
                    request.Original.Height * 2,
                    bytes,
                    [transform]),
                []));
        }
    }

    private sealed class CountingExportService : IExportService
    {
        public int CallCount { get; private set; }

        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new AssertFailedException("The export service must not be called for incomplete evidence.");
        }
    }

    private sealed class CheckpointDetectionStage(
        IReadOnlyDictionary<Guid, WorkflowReviewPanel> expectedPanels,
        CancellationTokenSource cancellation) : IWorkflowDetectionStage
    {
        public Guid CancelBeforePanelId { get; set; }

        public int CallCount { get; private set; }

        public Task<WorkflowDetectionBatch> DetectAsync(
            WorkflowPreparedPanel panel,
            WorkflowImageVariant imageVariant,
            Guid runId,
            Guid projectId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (panel.ImportedPanel.PanelId == CancelBeforePanelId)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            WorkflowReviewPanel expected = expectedPanels[panel.ImportedPanel.PanelId];
            var envelope = new WorkflowVisionEnvelope(
                contractVersion: 1,
                runId,
                projectId,
                panel.ImportedPanel.PanelId,
                stage: "markers",
                stageVersion: "checkpoint-test-v1",
                panel.Original.Sha256,
                new WorkflowVisionModel(
                    "checkpoint-test-model",
                    "checkpoint-test-v1",
                    new string('a', 64),
                    "cpu"),
                new WorkflowVisionTiming(0, 0, 0, 0),
                confidence: 1);
            WorkflowDetectionCandidate[] candidates = expected.Points.Select(point =>
                new WorkflowDetectionCandidate(
                    point.PointId,
                    point.DetectionKey ?? throw new AssertFailedException(
                        "The checkpoint fixture requires a stable detection key."),
                    point.OriginalPixelX,
                    point.OriginalPixelY,
                    point.Confidence,
                    WorkflowImageVariant.Original,
                    point.Symbol,
                    point.Shape,
                    point.Fill,
                    point.SeriesId,
                    point.PhaseId,
                    point.GraphX,
                    point.GraphY,
                    "markers",
                    "checkpoint-test-v1")).ToArray();
            return Task.FromResult(new WorkflowDetectionBatch(
                envelope,
                imageVariant,
                candidates));
        }
    }

    private sealed class UnusedWorkflowExportStage : IWorkflowExportStage
    {
        public Task<WorkflowExportResult> ExportAsync(
            WorkflowReviewState review,
            WorkflowExportRequest request,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("The batch checkpoint test must not invoke export.");
    }

    private sealed class PdfWithoutDetectorBytes : IPdfImportService
    {
        public Task<PdfImportResult> ImportAsync(PdfImportRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid figureId = Guid.Parse("90000000-0000-0000-0000-000000000001");
            var figure = new PdfFigureCandidate(
                figureId,
                pageNumber: 1,
                PdfFigureSourceKind.VectorPageRegion,
                embeddedImageId: null,
                new PdfRectD(0, 0, 100, 100),
                new PdfRectD(0, 0, 100, 100),
                sourcePixelWidth: 100,
                sourcePixelHeight: 100,
                encodedSource: null,
                mediaType: null,
                caption: null,
                evidence: [],
                confidence: 1);
            var panel = new PdfPanelRecord(
                Guid.Parse("90000000-0000-0000-0000-000000000002"),
                figureId,
                pageNumber: 1,
                order: 0,
                new PdfRectD(0, 0, 100, 100),
                new PdfRectD(0, 0, 100, 100),
                new PdfRectD(0, 0, 100, 100),
                participantLabel: null,
                caption: null,
                semanticSuggestions: [],
                evidence: [],
                confidence: 1);
            var document = new PdfDocumentSnapshot(
                new string('a', 64),
                new PdfDocumentMetadata(null, null, null, null, null, null),
                []);
            return Task.FromResult(new PdfImportResult(
                request.RunId,
                request.ProjectId,
                document,
                [figure],
                [panel],
                [],
                [],
                new PdfImportTiming(0, 0, 0, 0)));
        }
    }

    private sealed class PdfWithCroppedDetectorPanel : IPdfImportService
    {
        public Task<PdfImportResult> ImportAsync(PdfImportRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid figureId = Guid.Parse("90000000-0000-0000-0000-000000000011");
            byte[] encoded = CreateSolidGrayPng(40, 30, 0xd0);
            var figure = new PdfFigureCandidate(
                figureId,
                pageNumber: 1,
                PdfFigureSourceKind.RenderedPage,
                embeddedImageId: null,
                new PdfRectD(0, 0, 40, 30),
                new PdfRectD(100, 200, 400, 300),
                sourcePixelWidth: 40,
                sourcePixelHeight: 30,
                new ImmutableByteBuffer(encoded),
                mediaType: "image/png",
                caption: null,
                evidence: [],
                confidence: 1);
            var panel = new PdfPanelRecord(
                Guid.Parse("90000000-0000-0000-0000-000000000012"),
                figureId,
                pageNumber: 1,
                order: 0,
                new PdfRectD(5.2, 4.1, 20.3, 10.2),
                new PdfRectD(5.2, 4.1, 20.3, 10.2),
                new PdfRectD(152, 347, 203, 102),
                participantLabel: null,
                caption: null,
                semanticSuggestions: [],
                evidence: [],
                confidence: 1);
            var document = new PdfDocumentSnapshot(
                new string('b', 64),
                new PdfDocumentMetadata(null, null, null, null, null, null),
                []);
            return Task.FromResult(new PdfImportResult(
                request.RunId,
                request.ProjectId,
                document,
                [figure],
                [panel],
                [],
                [],
                new PdfImportTiming(0, 1, 1, 2)));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "GraphReaderProductionWorkflowTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class ProjectionWorkspace : ManualPreviewWorkspaceService
    {
        public ProductionReviewProjectionResult ProjectWithEvidence(
            WorkflowRunResult result,
            ProductionWorkflowPanelStore store) =>
            ProjectProductionReview(result, store);

        public int Project(WorkflowRunResult result, ProductionWorkflowPanelStore store)
        {
            ProductionReviewProjectionResult projection = ProjectWithEvidence(result, store);
            return projection.Succeeded ? projection.ProjectedPointCount : 0;
        }
    }
}
