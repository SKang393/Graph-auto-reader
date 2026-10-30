// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.IO.Compression;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;
using GraphReader.Domain;
using GraphReader.Export;
using GraphReader.Imaging;
using GraphReader.Inference;
using GraphReader.Legends;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Markers.Grouping;
using GraphReader.Ocr;
using GraphReader.Pdf;
using GraphReader.Phases;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AxisPixelPoint = GraphReader.Axis.PixelPoint;
using AxisPhaseNormalizedType = GraphReader.Phases.PhaseNormalizedType;
using VisionMarkerFill = GraphReader.Markers.Classification.MarkerFill;
using VisionMarkerShape = GraphReader.Markers.Classification.MarkerShape;
using DomainCalibrationAnchor = GraphReader.Domain.CalibrationAnchor;
using DomainCalibrationAnchorKind = GraphReader.Domain.CalibrationAnchorKind;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class MultiPanelManualCalibrationWorkflowTests
{
    [TestMethod]
    [DataRow(1, 20)]
    [DataRow(29, 35)]
    public async Task UserAnchorsCalibrateEveryLabelFreeRasterPanelThroughProductionWorkflow(int firstSession, int finalSession)
    {
        using var directory = new TemporaryDirectory();
        string imagePath = Path.Combine(directory.Path, "synthetic-stacked-graphs.png");
        byte[] sourceBytes = CreateStackedGraphPng();
        await File.WriteAllBytesAsync(imagePath, sourceBytes);

        var store = new ProductionWorkflowPanelStore();
        var imageImporter = new ImageImportService();
        var axisCases = new Dictionary<Guid, SyntheticPanelGeometry>();
        var adapter = new ProductionAutomaticDetectionAdapter(
            store,
            new ProductionRasterFrameDecoder(),
            new SyntheticAxisAdapter(axisCases),
            new SyntheticOcrAdapter(),
            new SyntheticMaskComposer(),
            new SyntheticMarkerCenterAdapter(axisCases),
            new SyntheticMarkerClassificationAdapter(),
            new SyntheticLegendAdapter(),
            new SyntheticPhaseAdapter(),
            new EmptyConnectionBuilder());
        var orchestrator = new WorkflowOrchestrator(new WorkflowServiceSet(
            new ProductionWorkflowImportStage(store, imageImporter),
            new ProductionWorkflowPrepareStage(store),
            new ProductionWorkflowDetectionStage(store, adapter),
            new UnusedExportStage()));
        var workspace = new ProductionWorkspaceService(
            imageImportService: imageImporter,
            automaticStages: ApprovedStages(),
            workflowOrchestrator: orchestrator,
            panelStore: store);

        WorkspaceTabViewModel[] tabs = (await workspace.ImportImagesAsync(
            [imagePath], CancellationToken.None)).ToArray();
        Assert.IsGreaterThan(1, tabs.Length, "The generated source must panelize into multiple graphs.");

        foreach (WorkspaceTabViewModel tab in tabs)
        {
            PanelRecord panel = workspace.CurrentProject.Panels.Single(candidate =>
                candidate.PanelId.Value.ToString("D") == tab.PanelId);
            SyntheticPanelGeometry geometry = GeometryFor(panel, tab.PixelWidth, tab.PixelHeight);
            axisCases.Add(panel.PanelId.Value, geometry);

            // The fixture contains axes and synthetic markers but no text or session labels.
            // These are explicit user-provided anchors, not inputs to any detector.
            workspace.Calibrate(
                tab.TabId,
                new ManualCalibrationRequest(
                    geometry.Session1Y0,
                    geometry.Session1YMaximum,
                    geometry.SessionMaximumY0,
                    YMaximum: 100,
                    XMaximum: finalSession,
                    FirstVisibleSession: firstSession));
        }

        IReadOnlyList<WorkflowManualCalibrationBinding> bindings =
            workspace.CreateConfirmedManualCalibrationBindings();
        Assert.HasCount(tabs.Length, bindings);
        CollectionAssert.AreEquivalent(
            tabs.Select(static tab => Guid.Parse(tab.PanelId!)).ToArray(),
            bindings.Select(static binding => binding.PanelId).ToArray());
        foreach (WorkspaceTabViewModel tab in tabs)
        {
            WorkflowManualCalibrationBinding binding = bindings.Single(candidate =>
                candidate.PanelId == Guid.Parse(tab.PanelId!));
            Assert.AreEqual(tab.SourceSha256, binding.OriginalImageSha256);
        }

        WorkflowRunResult result = await workspace.RunAutomaticDetectionAsync(CancellationToken.None);

        Assert.HasCount(tabs.Length, result.Review.Panels);
        foreach (WorkflowReviewPanel reviewPanel in result.Review.Panels)
        {
            PanelRecord persistedPanel = workspace.CurrentProject.Panels.Single(candidate =>
                candidate.PanelId.Value == reviewPanel.PanelId);
            CalibrationRecord persistedCalibration = persistedPanel.Calibration ??
                throw new AssertFailedException("The manual calibration was not persisted to the workspace panel.");
            Assert.AreEqual(CalibrationStatus.Valid, persistedCalibration.Status);
            Assert.IsTrue(persistedCalibration.UserConfirmed);
            Assert.AreEqual("manual_three_anchor", persistedCalibration.SessionLattice?.Source);

            ProductionPanelExportEvidence export = store.Get(reviewPanel.PanelId).ExportEvidence ??
                throw new AssertFailedException("The production adapter did not retain export evidence.");
            ProductionPanelProjectionEvidence projection = export.ProjectionEvidence ??
                throw new AssertFailedException("The production adapter did not retain its projection.");
            CalibrationRecord calibration = projection.Calibration;
            Assert.AreEqual("manual_three_anchor", calibration.SessionLattice?.Source);
            Assert.IsTrue(calibration.UserConfirmed);
            Assert.AreEqual(3, calibration.Anchors.Count);
            if (firstSession > 1)
            {
                Assert.IsTrue(export.SessionOriginPolicy.HasExplicitOverride);
                Assert.IsTrue(export.SessionOriginPolicy.RequireFirstObservedSessionOne);
            }

            SyntheticPanelGeometry geometry = axisCases[reviewPanel.PanelId];
            DomainCalibrationAnchor persistedY0 = persistedCalibration.Anchors.Single(anchor =>
                anchor.Kind == (firstSession > 1 ? DomainCalibrationAnchorKind.FirstVisibleSessionY0 : DomainCalibrationAnchorKind.Session1Y0));
            DomainCalibrationAnchor persistedYMaximum = persistedCalibration.Anchors.Single(anchor =>
                anchor.Kind == (firstSession > 1 ? DomainCalibrationAnchorKind.FirstVisibleSessionYmax : DomainCalibrationAnchorKind.Session1Ymax));
            DomainCalibrationAnchor persistedXMaximum = persistedCalibration.Anchors.Single(anchor =>
                anchor.Kind == DomainCalibrationAnchorKind.SessionmaxY0);
            Assert.AreEqual(geometry.X0 + persistedPanel.Crop.X, persistedY0.Screen.X, 0);
            Assert.AreEqual(geometry.Y0 + persistedPanel.Crop.Y, persistedY0.Screen.Y, 0);
            Assert.AreEqual(geometry.X0 + persistedPanel.Crop.X, persistedYMaximum.Screen.X, 0);
            Assert.AreEqual(geometry.YMax + persistedPanel.Crop.Y, persistedYMaximum.Screen.Y, 0);
            Assert.AreEqual(geometry.XMax + persistedPanel.Crop.X, persistedXMaximum.Screen.X, 0);
            Assert.AreEqual(geometry.Y0 + persistedPanel.Crop.Y, persistedXMaximum.Screen.Y, 0);

            WorkflowPoint[] points = reviewPanel.Points.OrderBy(static point => point.OriginalPixelX).ToArray();
            Assert.HasCount(3, points);
            for (int index = 0; index < points.Length; index++)
            {
                SyntheticMarker marker = geometry.Markers.OrderBy(static item => item.PixelX).ElementAt(index);
                double expectedX = firstSession + (marker.ExpectedGraphX - 1) * (finalSession - firstSession) / 19d;
                Assert.AreEqual(expectedX, points[index].GraphX!.Value, 1e-8);
                Assert.AreEqual(marker.ExpectedGraphY, points[index].GraphY!.Value, 1e-8);
            }

            Assert.IsTrue(projection.Points.All(static point =>
                point.PrintedXValue is null && point.EstimatedXValue.HasValue));
        }

        if (firstSession > 1)
        {
            foreach (WorkspaceTabViewModel tab in tabs)
            {
                SeriesCardViewModel series = tab.SeriesCards.First();
                workspace.UpdateSeries(tab.TabId, series.SeriesId,
                    new ManualSeriesDefinition(series.Label, series.Symbol, series.Shape, series.Fill, SemanticRole.Intervention));
            }
            string savePath = Path.Combine(directory.Path, "production-manual-origin.garproj");
            var saved = await workspace.SaveProjectAsync(savePath, CancellationToken.None);
            Assert.IsTrue(saved.IsSuccess, string.Join(" | ", saved.Errors.Select(static error => error.TechnicalMessage)));
            var capture = new CapturingExportService();
            var reopened = new ProductionWorkspaceService(exportService: capture);
            WorkspaceTabViewModel[] reopenedTabs = (await reopened.OpenProjectAsync(savePath, CancellationToken.None)).ToArray();
            foreach (WorkspaceTabViewModel reopenedTab in reopenedTabs)
            {
                Assert.AreEqual(firstSession, reopenedTab.Calibration!.FirstVisibleSession);
                ExportResult exported = await reopened.ExportAsync(reopenedTab.TabId,
                    Path.Combine(directory.Path, "export-" + reopenedTab.PanelId), CancellationToken.None);
                Assert.IsFalse(exported.Preview.FinalExportBlocked,
                    string.Join(" | ", exported.Failures.Select(static error => error.TechnicalMessage)));
                Assert.IsTrue(exported.AuditArtifacts.SelectMany(static artifact => artifact.Rows).All(static row =>
                    row.XSource == ExportXValueSource.Estimated && row.SessionOriginOverrideApplied));
                var firstPoint = reopenedTab.Points.OrderBy(static point => point.PixelX).First();
                reopened.DeletePoint(reopenedTab.TabId, firstPoint.PointId);
                double expectedFirst = reopenedTab.Points.OrderBy(static point => point.PixelX).First().GraphX;
                ExportResult afterEdit = await reopened.ExportAsync(reopenedTab.TabId,
                    Path.Combine(directory.Path, "export-after-edit-" + reopenedTab.PanelId), CancellationToken.None);
                Assert.IsFalse(afterEdit.Preview.FinalExportBlocked);
                Assert.AreEqual(expectedFirst, capture.LastRequest!.Calibration.FirstObservedSession!.Value, 1e-8);
            }
        }
    }

    private static SyntheticPanelGeometry GeometryFor(PanelRecord panel, int width, int height)
    {
        CropRectangle crop = panel.Crop;
        int[] baselines = [270, 530, 790];
        int baseline = baselines.Single(value => value >= crop.Y && value <= crop.Y + crop.Height);
        double x0 = 80 - crop.X;
        double xMax = 580 - crop.X;
        double y0 = baseline - crop.Y;
        double yMax = baseline - 180 - crop.Y;
        var anchorY0 = new AxisPixelPoint(x0, y0);
        var anchorYMax = new AxisPixelPoint(x0, yMax);
        var anchorXMax = new AxisPixelPoint(xMax, y0);
        SyntheticMarker[] markers =
        [
            Marker(170, baseline - 60),
            Marker(280, baseline - 110),
            Marker(400, baseline - 80),
        ];
        return new SyntheticPanelGeometry(
            panel.PanelId.Value,
            width,
            height,
            x0,
            xMax,
            yMax,
            y0,
            anchorY0,
            anchorYMax,
            anchorXMax,
            markers);

        SyntheticMarker Marker(double sourceX, double sourceY)
        {
            double localX = sourceX - crop.X;
            double localY = sourceY - crop.Y;
            return new SyntheticMarker(
                localX,
                localY,
                1 + ((sourceX - 80) * 19d / 500d),
                (baseline - sourceY) * 100d / 180d);
        }
    }

    private static byte[] CreateStackedGraphPng()
    {
        const int width = 640;
        const int height = 900;
        byte[] scanlines = new byte[height * (width + 1)];
        Array.Fill(scanlines, byte.MaxValue);
        for (int y = 0; y < height; y++) scanlines[y * (width + 1)] = 0;
        foreach (int baseline in new[] { 270, 530, 790 })
        {
            DrawHorizontal(scanlines, width, 80, 580, baseline, 2);
            DrawVertical(scanlines, width, 80, baseline - 180, baseline, 2);
            DrawHorizontal(scanlines, width, 120, 220, baseline - 60, 1);
            DrawHorizontal(scanlines, width, 220, 340, baseline - 110, 1);
            DrawHorizontal(scanlines, width, 340, 460, baseline - 80, 1);
            DrawVertical(scanlines, width, 300, baseline - 150, baseline, 1);
            DrawDot(scanlines, width, 170, baseline - 60);
            DrawDot(scanlines, width, 280, baseline - 110);
            DrawDot(scanlines, width, 400, baseline - 80);
        }

        byte[] compressed;
        using (var stream = new MemoryStream())
        {
            using (var zlib = new ZLibStream(stream, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(scanlines);
            compressed = stream.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 0;
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void DrawHorizontal(byte[] scanlines, int width, int x0, int x1, int y, int thickness)
    {
        for (int dy = 0; dy < thickness; dy++)
        for (int x = x0; x <= x1; x++) SetPixel(scanlines, width, x, y + dy);
    }

    private static void DrawVertical(byte[] scanlines, int width, int x, int y0, int y1, int thickness)
    {
        for (int dx = 0; dx < thickness; dx++)
        for (int y = y0; y <= y1; y++) SetPixel(scanlines, width, x + dx, y);
    }

    private static void DrawDot(byte[] scanlines, int width, int x, int y)
    {
        for (int dy = -3; dy <= 3; dy++)
        for (int dx = -3; dx <= 3; dx++)
            if ((dx * dx) + (dy * dy) <= 9) SetPixel(scanlines, width, x + dx, y + dy);
    }

    private static void SetPixel(byte[] scanlines, int width, int x, int y) =>
        scanlines[(y * (width + 1)) + x + 1] = 0;

    private static void WriteChunk(Stream destination, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        destination.Write(length);
        byte[] name = System.Text.Encoding.ASCII.GetBytes(type);
        destination.Write(name);
        destination.Write(data);
        uint crc = 0xffffffff;
        foreach (byte value in name) crc = UpdateCrc(crc, value);
        foreach (byte value in data) crc = UpdateCrc(crc, value);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ~crc);
        destination.Write(checksum);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }

    private static WorkflowVisionEnvelope Envelope(
        ProductionWorkflowDetectionRequest request,
        string stage,
        string version,
        char checksum,
        bool deterministic = false) => new(
        1,
        request.RunId,
        request.ProjectId,
        request.Panel.ImportedPanel.PanelId,
        stage,
        version,
        request.Image.Sha256,
        deterministic ? null : new WorkflowVisionModel($"synthetic-{stage}", version, new string(checksum, 64), "cpu"),
        new WorkflowVisionTiming(0, 0, 0, 0),
        1,
        transforms: request.Transforms);

    private sealed record SyntheticMarker(double PixelX, double PixelY, double ExpectedGraphX, double ExpectedGraphY);

    private sealed record SyntheticPanelGeometry(
        Guid PanelId,
        int Width,
        int Height,
        double X0,
        double XMax,
        double YMax,
        double Y0,
        GraphReader.Axis.PixelPoint Session1Y0,
        GraphReader.Axis.PixelPoint Session1YMaximum,
        GraphReader.Axis.PixelPoint SessionMaximumY0,
        IReadOnlyList<SyntheticMarker> Markers);

    private static AutomaticStageStatus[] ApprovedStages() =>
    [
        new("axis", AutomaticStageState.Approved, "Test-only deterministic adapters."),
        new("ocr", AutomaticStageState.Approved, "Test-only deterministic adapters."),
        new("markers", AutomaticStageState.Approved, "Test-only deterministic adapters."),
        new("legends", AutomaticStageState.Approved, "Test-only deterministic adapters."),
        new("phases", AutomaticStageState.Approved, "Test-only deterministic adapters."),
    ];

    private sealed class SyntheticAxisAdapter(IReadOnlyDictionary<Guid, SyntheticPanelGeometry> cases)
        : IProductionAxisGeometryAdapter
    {
        public string AdapterId => "test-synthetic-axis";
        public bool IsApproved => true;

        public Task<ProductionAxisGeometryEvidence> DetectAsync(
            ProductionWorkflowDetectionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntheticPanelGeometry geometry = cases[request.Panel.ImportedPanel.PanelId];
            var xAxis = new AxisLineFit(
                new GeometryLineSegment(new AxisPixelPoint(geometry.X0, geometry.Y0), new AxisPixelPoint(geometry.XMax, geometry.Y0)),
                1, 0, 1, ["synthetic-axis"]);
            var yAxis = new AxisLineFit(
                new GeometryLineSegment(new AxisPixelPoint(geometry.X0, geometry.Y0), new AxisPixelPoint(geometry.X0, geometry.YMax)),
                1, 0, 1, ["synthetic-axis"]);
            var plot = new PlotPolygon(
                new AxisPixelPoint(geometry.X0, geometry.Y0),
                new AxisPixelPoint(geometry.XMax, geometry.Y0),
                new AxisPixelPoint(geometry.XMax, geometry.YMax),
                new AxisPixelPoint(geometry.X0, geometry.YMax));
            var result = new AxisGeometryResult(
                "original_pixels", plot, xAxis, yAxis, [], [], [], 1,
                new AxisGeometryUncertainty(0, 0, 1, false, []),
                new AxisGeometryDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, []));
            return Task.FromResult(new ProductionAxisGeometryEvidence(
                Envelope(request, "axis", "test-v1", 'a'), result));
        }
    }

    private sealed class SyntheticOcrAdapter : IProductionOcrAdapter
    {
        public string AdapterId => "test-synthetic-ocr-no-x-labels";
        public bool IsApproved => true;

        public Task<ProductionOcrEvidence> RecognizeAsync(
            ProductionWorkflowDetectionRequest request,
            ProductionDecodedRaster originalRaster,
            OcrRectangle plotBounds,
            OcrDetectorImage detectorImage,
            IReadOnlyList<double>? phaseDividerXs,
            CancellationToken cancellationToken) => RecognizeAsync(
                request, originalRaster, plotBounds, detectorImage, cancellationToken);

        public Task<ProductionOcrEvidence> RecognizeAsync(
            ProductionWorkflowDetectionRequest request,
            ProductionDecodedRaster originalRaster,
            OcrRectangle plotBounds,
            OcrDetectorImage detectorImage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new OcrResult(
                OcrContract.Version,
                request.RunId.ToString("D"),
                request.ProjectId.ToString("D"),
                request.Panel.ImportedPanel.PanelId.ToString("D"),
                OcrContract.Stage,
                "synthetic-no-x-ticks-v1",
                request.Image.Sha256,
                OcrContract.CoordinateSpace,
                [], [], new OcrTiming(0, 0, 0, 0), 1,
                ["synthetic_fixture_contains_no_printed_session_labels"],
                new OcrCacheDiagnostics(false, "test-synthetic", 0, 0), null, []);
            return Task.FromResult(new ProductionOcrEvidence(
                result,
                [
                    new ProductionOcrModelEvidence("ocr_detection", Envelope(request, "ocr", "test-v1", 'b')),
                    new ProductionOcrModelEvidence("ocr_recognition", Envelope(request, "ocr", "test-v1", 'c')),
                ],
                new ProductionOcrConfigurationEvidence(
                [
                    new ProductionOcrConfiguredModel("ocr_detection",
                        new ModelIdentity("test-ocr-det", "test-v1", new string('b', 64), "synthetic-detector.onnx"),
                        InferenceProvider.Cpu),
                    new ProductionOcrConfiguredModel("ocr_recognition",
                        new ModelIdentity("test-ocr-rec", "test-v1", new string('c', 64), "synthetic-recognizer.onnx"),
                        InferenceProvider.Cpu),
                ],
                ProductionOcrConfigurationScope.ApprovedProduction)));
        }
    }

    private sealed class SyntheticMaskComposer : IProductionDetectionMaskComposer
    {
        public string AdapterId => "test-synthetic-mask";
        public bool IsApproved => true;

        public Task<ProductionDetectionMaskEvidence> ComposeAsync(
            ProductionWorkflowDetectionRequest request,
            ProductionDecodedRaster raster,
            ProductionAxisGeometryEvidence axisEvidence,
            ProductionOcrEvidence ocrEvidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = checked(raster.Width * raster.Height);
            return Task.FromResult(new ProductionDetectionMaskEvidence(
                raster.Width, raster.Height, request.Image.Sha256, request.ImageVariant,
                [axisEvidence.Envelope, .. ocrEvidence.ModelEvidence.Select(static item => item.Envelope)],
                Envelope(request, "markers", "test-mask-v1", 'd'), new float[count], new float[count], []));
        }
    }

    private sealed class SyntheticMarkerCenterAdapter(IReadOnlyDictionary<Guid, SyntheticPanelGeometry> cases)
        : IProductionMarkerCenterAdapter
    {
        public string AdapterId => "test-synthetic-centers";
        public bool IsApproved => true;
        public ModelIdentity Model { get; } = new("test-centers", "test-v1", new string('e', 64), "synthetic-centers.onnx");

        public Task<ProductionMarkerCenterEvidence> DetectAsync(
            ProductionWorkflowDetectionRequest request,
            MarkerImageFrame originalImage,
            MarkerPolygon plotPolygon,
            MarkerImageFrame? enhancedImage,
            IReadOnlyList<WorkflowTransformProvenance>? enhancedTransforms,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntheticPanelGeometry geometry = cases[request.Panel.ImportedPanel.PanelId];
            MarkerCenter[] markers = geometry.Markers.Select((item, index) => new MarkerCenter(
                $"synthetic-marker-{index + 1}", new MarkerPoint(item.PixelX, item.PixelY), 3, 0.01, 0.99,
                MarkerSourceImage.Original)).ToArray();
            return Task.FromResult(new ProductionMarkerCenterEvidence(
                Envelope(request, "markers", "test-centers-v1", 'e'), markers, []));
        }
    }

    private sealed class SyntheticMarkerClassificationAdapter : IProductionMarkerClassificationAdapter
    {
        public string AdapterId => "test-synthetic-classifier";
        public bool IsApproved => true;
        public ModelIdentity Model { get; } = new("test-classifier", "test-v1", new string('f', 64), "synthetic-classifier.onnx");

        public Task<ProductionMarkerClassificationEvidence> ClassifyAsync(
            ProductionWorkflowDetectionRequest request,
            MarkerImageFrame image,
            IReadOnlyList<MarkerCenter> markers,
            IReadOnlyDictionary<string, MarkerRectangle> originalPixelContentBounds,
            CancellationToken cancellationToken) => ClassifyAsync(request, image, markers, cancellationToken);

        public Task<ProductionMarkerClassificationEvidence> ClassifyAsync(
            ProductionWorkflowDetectionRequest request,
            MarkerImageFrame image,
            IReadOnlyList<MarkerCenter> markers,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClassifiedMarker[] classified = markers.Select(marker => new ClassifiedMarker(
                marker, VisionMarkerShape.Circle, VisionMarkerFill.Filled, "●", "filled circle", 0.01, 0.99, 0.99,
                Enumerable.Repeat(0.1f, 12))).ToArray();
            return Task.FromResult(new ProductionMarkerClassificationEvidence(
                Envelope(request, "markers", "test-classifier-v1", 'f'), classified));
        }
    }

    private sealed class SyntheticLegendAdapter : IProductionLegendReasoningAdapter
    {
        public string AdapterId => "test-synthetic-legend";
        public bool IsApproved => true;

        public Task<ProductionLegendReasoningEvidence> ResolveAsync(
            ProductionWorkflowDetectionRequest request,
            LegendReasoningRequest legendRequest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LegendSeriesResolution[] series = legendRequest.Series.Select(item => new LegendSeriesResolution(
                item.SeriesId, item.CurrentName ?? item.AccessibleName, item.Symbol, item.AccessibleName,
                LegendEvidenceSource.SymbolFallback, null, null, 1,
                new LegendSemanticEvidence(LegendSemanticHint.Unknown, string.Empty, 1), false)).ToArray();
            return Task.FromResult(new ProductionLegendReasoningEvidence(
                Envelope(request, "legends", "test-v1", '1', deterministic: true),
                new LegendReasoningPayload([], series, [], [], [], [])));
        }
    }

    private sealed class SyntheticPhaseAdapter : IProductionPhaseReasoningAdapter
    {
        public string AdapterId => "test-synthetic-phase";
        public bool IsApproved => true;

        public Task<ProductionPhaseReasoningEvidence> ResolveAsync(
            ProductionWorkflowDetectionRequest request,
            PhaseReasoningRequest phaseRequest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string phaseId = Guid.NewGuid().ToString("D");
            var phase = new PhaseRegion(phaseId, 1, "a", AxisPhaseNormalizedType.Baseline, "Synthetic baseline",
                0, request.Image.Width, null, null, 1, PhaseEvidenceSource.ProfilePrior);
            PhasePointAssignment[] assignments = phaseRequest.Points.Select(point =>
                new PhasePointAssignment(point.PointId, phaseId, point.Center.X)).ToArray();
            return Task.FromResult(new ProductionPhaseReasoningEvidence(
                Envelope(request, "phases", "test-v1", '2', deterministic: true),
                new PhaseReasoningPayload([], [phase], assignments, [], new PhaseManualOverrides())));
        }
    }

    private sealed class EmptyConnectionBuilder : IMarkerConnectionGraphBuilder
    {
        public ValueTask<IReadOnlyList<MarkerConnection>> BuildAsync(
            MarkerConnectionRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<MarkerConnection>>([]);
    }

    private sealed class UnusedExportStage : IWorkflowExportStage
    {
        public Task<WorkflowExportResult> ExportAsync(
            WorkflowReviewState review,
            WorkflowExportRequest request,
            CancellationToken cancellationToken) => throw new AssertFailedException(
                "Detection workflow tests must not start an export.");
    }

    private sealed class CapturingExportService : IExportService
    {
        private readonly ExportService _inner = new();
        public ExportRequest? LastRequest { get; private set; }
        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return _inner.ExportAsync(request, cancellationToken);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GraphReader-ManualCalibration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
