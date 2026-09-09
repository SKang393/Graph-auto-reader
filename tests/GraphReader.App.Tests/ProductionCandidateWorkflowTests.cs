// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
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

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionCandidateWorkflowTests
{
    private static readonly PdfRectD SourceCrop = new(10, 20, 80, 60);
    private static readonly string[] ExpectedProvenanceStageVersions =
    [
        "candidate-axis-v1",
        "candidate-ocr-v1",
        "candidate-ocr-v1",
        "candidate-mask-v1",
        "candidate-center-v1",
        "fixed-classifier-v1",
        "fixed-legend-v1",
        "fixed-phase-v1",
    ];
    private static readonly string?[] ExpectedProvenanceModelSha256 =
    [
        new string('a', 64),
        new string('b', 64),
        new string('c', 64),
        new string('d', 64),
        new string('e', 64),
        new string('f', 64),
        null,
        null,
    ];

    [TestMethod]
    public async Task UnapprovedCandidateRunsUntouchedReviewAndWritesSourceMappedCsv()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = WritePng(directory.Path, "source.png", 120, 100);
        var store = new ProductionWorkflowPanelStore();
        ProductionAutomaticDetectionAdapter candidate = CreateCandidate(store);
        WorkflowOrchestrator orchestrator = CreateWorkflow(store, candidate);
        WorkflowRunRequest request = Request(imagePath);

        WorkflowRunResult result = await orchestrator.RunThroughReviewAsync(
            request,
            previousReview: null,
            CancellationToken.None);

        Assert.IsFalse(candidate.IsApproved);
        Assert.HasCount(1, result.Review.Panels);
        Assert.HasCount(0, result.Review.CorrectionJournal);
        WorkflowReviewPanel panel = result.Review.Panels.Single();
        Assert.IsTrue(panel.Points.All(static point => point.ReviewStatus == WorkflowReviewStatus.Unreviewed));
        Assert.IsTrue(panel.Points.All(static point => point.GraphX.HasValue && point.GraphY.HasValue));
        ProductionPanelExportEvidence retained = store.Get(panel.PanelId).ExportEvidence!;
        Assert.HasCount(8, retained.Provenance);
        CollectionAssert.AreEqual(
            ExpectedProvenanceStageVersions,
            retained.Provenance.Select(static envelope => envelope.StageVersion).ToArray());
        CollectionAssert.AreEqual(
            ExpectedProvenanceModelSha256,
            retained.Provenance.Select(static envelope => envelope.Model?.Sha256).ToArray());
        Assert.IsTrue(retained.Provenance.All(envelope =>
            envelope.RunId == result.RunId &&
            envelope.ProjectId == request.Import.ProjectId &&
            envelope.PanelId == panel.PanelId &&
            string.Equals(envelope.InputSha256, panel.PreparedPanel.Original.Sha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(envelope.CoordinateSpace, "original_pixels", StringComparison.Ordinal)));
        var evaluatorTruth = new Dictionary<double, double>
        {
            [1] = 25,
            [2] = 75,
        };
        string output = Path.Combine(directory.Path, "export");

        WorkflowExportResult export = await orchestrator.ExportAsync(
            result.Review,
            new WorkflowExportRequest(Guid.NewGuid(), output),
            CancellationToken.None);

        Assert.IsTrue(export.Succeeded, string.Join(" | ", export.Warnings));
        WorkflowExportArtifact[] minimal = export.Artifacts.Where(static artifact =>
            artifact.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
            !artifact.FileName.Contains("audit", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.IsNotEmpty(minimal);
        var actual = new Dictionary<double, double>();
        foreach (WorkflowExportArtifact artifact in minimal)
        {
            string[] lines = await File.ReadAllLinesAsync(artifact.WrittenPath!);
            Assert.AreEqual(ExportContract.MinimalCsvHeader, lines[0]);
            foreach (string line in lines.Skip(1).Where(static line => line.Length > 0))
            {
                string[] fields = line.Split(',');
                actual[double.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture)] =
                    double.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        CollectionAssert.AreEquivalent(evaluatorTruth.Keys.ToArray(), actual.Keys.ToArray());
        foreach ((double x, double y) in evaluatorTruth)
        {
            Assert.AreEqual(y, actual[x], 1e-9);
        }

        WorkflowExportArtifact audit = export.Artifacts.Single(static artifact =>
            artifact.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
            artifact.FileName.Contains("audit", StringComparison.OrdinalIgnoreCase));
        string[] auditLines = await File.ReadAllLinesAsync(audit.WrittenPath!);
        string[] header = auditLines[0].Split(',');
        int xIndex = Array.IndexOf(header, "original_pixel_x");
        int yIndex = Array.IndexOf(header, "original_pixel_y");
        Assert.IsGreaterThanOrEqualTo(0, xIndex);
        Assert.IsGreaterThanOrEqualTo(0, yIndex);
        var sourcePoints = auditLines.Skip(1)
            .Where(static line => line.Length > 0)
            .Select(line => line.Split(','))
            .Select(fields => (
                X: double.Parse(fields[xIndex], System.Globalization.CultureInfo.InvariantCulture),
                Y: double.Parse(fields[yIndex], System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        CollectionAssert.AreEquivalent(new[] { (30d, 55d), (70d, 45d) }, sourcePoints);
        RasterPanelSourceProvenance provenance = store.Get(panel.PanelId).RasterPanelSource!;
        Assert.AreEqual(SourceCrop, provenance.EncodedCropInSourcePixels);
    }

    [TestMethod]
    public async Task ProductionStageRejectsTheSameUnapprovedAutomaticAdapter()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = WritePng(directory.Path, "source.png", 120, 100);
        var store = new ProductionWorkflowPanelStore();
        ProductionAutomaticDetectionAdapter candidate = CreateCandidate(store);
        var import = new ProductionWorkflowImportStage(
            store,
            new ImageImportService(),
            pdfImportService: null,
            new ProductionRasterPanelizer(new SingleCropPanelizationEngine()));
        WorkflowImportedPanel panel = (await import.ImportAsync(
            Request(imagePath).Import,
            CancellationToken.None)).Panels.Single();
        WorkflowPreparedPanel prepared = await new ProductionWorkflowPrepareStage(store).PrepareAsync(
            panel,
            enhancementEnabled: false,
            CancellationToken.None);
        var productionStage = new ProductionWorkflowDetectionStage(store, candidate);

        ProductionWorkflowStageException exception = await Assert.ThrowsExactlyAsync<ProductionWorkflowStageException>(
            () => productionStage.DetectAsync(
                prepared,
                WorkflowImageVariant.Original,
                Guid.NewGuid(),
                Request(imagePath).Import.ProjectId,
                CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionModelsUnavailable, exception.Failure.Code);
        Assert.IsNull(store.Get(panel.PanelId).ExportEvidence);
    }

    [TestMethod]
    public async Task CandidateExportFailsClosedForInvalidCalibrationAndStaleProvenance()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = WritePng(directory.Path, "source.png", 120, 100);
        var store = new ProductionWorkflowPanelStore();
        WorkflowOrchestrator orchestrator = CreateWorkflow(store, CreateCandidate(store));
        WorkflowRunResult result = await orchestrator.RunThroughReviewAsync(
            Request(imagePath),
            previousReview: null,
            CancellationToken.None);
        Guid panelId = result.Review.Panels.Single().PanelId;
        ProductionPanelExportEvidence valid = store.Get(panelId).ExportEvidence!;
        store.SetExportEvidence(panelId, CopyEvidence(
            valid,
            new ExportCalibration(
                ExportCalibrationStatus.NeedsReview,
                hasYCalibration: false,
                hasPrintedSessionCalibration: false,
                hasAbsoluteSessionOrigin: false,
                firstObservedSession: null,
                confidence: 0,
                ["candidate calibration invalid"]),
            valid.Provenance));

        WorkflowExportResult invalidCalibration = await orchestrator.ExportAsync(
            result.Review,
            new WorkflowExportRequest(Guid.NewGuid(), Path.Combine(directory.Path, "invalid")),
            CancellationToken.None);

        Assert.IsFalse(invalidCalibration.Succeeded);
        Assert.AreEqual(ProductionWorkflowFailureCodes.RecalibrationRequired, invalidCalibration.FailureCode);
        WorkflowVisionEnvelope stale = new(
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            panelId,
            "markers",
            "candidate-stale",
            new string('9', 64),
            new WorkflowVisionModel("candidate", "p1", new string('8', 64), "cpu"),
            new WorkflowVisionTiming(0, 0, 0, 0),
            0.9);
        store.SetExportEvidence(panelId, CopyEvidence(valid, valid.Calibration, [stale]));

        WorkflowExportResult staleResult = await orchestrator.ExportAsync(
            result.Review,
            new WorkflowExportRequest(Guid.NewGuid(), Path.Combine(directory.Path, "stale")),
            CancellationToken.None);

        Assert.IsFalse(staleResult.Succeeded);
        Assert.AreEqual(ProductionWorkflowFailureCodes.RecalibrationRequired, staleResult.FailureCode);
        Assert.IsFalse(Directory.Exists(Path.Combine(directory.Path, "invalid")));
        Assert.IsFalse(Directory.Exists(Path.Combine(directory.Path, "stale")));
    }

    [TestMethod]
    public async Task CancellationAfterFinalCandidateStageDoesNotCommitOrReplaceExportEvidence()
    {
        using var directory = new TemporaryDirectory();
        string imagePath = WritePng(directory.Path, "source.png", 120, 100);

        await VerifyCancellationPreservesEvidenceAsync(imagePath, seedPriorEvidence: false);
        await VerifyCancellationPreservesEvidenceAsync(imagePath, seedPriorEvidence: true);
    }

    private static async Task VerifyCancellationPreservesEvidenceAsync(
        string imagePath,
        bool seedPriorEvidence)
    {
        var store = new ProductionWorkflowPanelStore();
        WorkflowRunRequest request = Request(imagePath);
        ProductionPanelExportEvidence? prior = null;
        Guid? panelId = null;
        if (seedPriorEvidence)
        {
            WorkflowRunResult successful = await CreateWorkflow(store, CreateCandidate(store))
                .RunThroughReviewAsync(request, previousReview: null, CancellationToken.None);
            panelId = successful.Review.Panels.Single().PanelId;
            prior = store.Get(panelId.Value).ExportEvidence;
            Assert.IsNotNull(prior);
        }

        using var cancellation = new CancellationTokenSource();
        WorkflowOrchestrator canceledWorkflow = CreateWorkflow(
            store,
            CreateCandidate(store, cancellation));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            canceledWorkflow.RunThroughReviewAsync(
                request,
                previousReview: null,
                cancellation.Token));

        panelId ??= store.PanelIds.Single();
        ProductionPanelExportEvidence? afterCancellation = store.Get(panelId.Value).ExportEvidence;
        if (seedPriorEvidence)
        {
            Assert.AreSame(prior, afterCancellation);
        }
        else
        {
            Assert.IsNull(afterCancellation);
        }
    }

    private static ProductionPanelExportEvidence CopyEvidence(
        ProductionPanelExportEvidence source,
        ExportCalibration calibration,
        IEnumerable<WorkflowVisionEnvelope> provenance) =>
        new(
            calibration,
            source.Phases,
            source.Series,
            source.Relations,
            source.Points,
            provenance,
            source.Participant,
            source.Mode,
            source.AuditMode,
            source.SessionOriginPolicy,
            source.ProjectionEvidence);

    private static WorkflowOrchestrator CreateWorkflow(
        ProductionWorkflowPanelStore store,
        ProductionAutomaticDetectionAdapter candidate) =>
        ProductionCandidateWorkflowComposition.Create(
            store,
            new ImageImportService(),
            candidate,
            new ExportService(),
            rasterPanelizer: new ProductionRasterPanelizer(new SingleCropPanelizationEngine()));

    private static ProductionAutomaticDetectionAdapter CreateCandidate(
        ProductionWorkflowPanelStore store,
        CancellationTokenSource? cancelAfterPhase = null) =>
        new(
            store,
            new ProductionRasterFrameDecoder(),
            new CandidateAxisAdapter(),
            new CandidateOcrAdapter(),
            new CandidateMaskComposer(),
            new CandidateCenterAdapter(),
            new ClassificationAdapter(),
            new LegendAdapter(),
            new PhaseAdapter(cancelAfterPhase),
            new EmptyConnectionBuilder());

    private static WorkflowRunRequest Request(string imagePath) =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new WorkflowImportRequest(
                Guid.Parse("21111111-1111-1111-1111-111111111111"),
                [new WorkflowSourceRequest(
                    Guid.Parse("31111111-1111-1111-1111-111111111111"),
                    WorkflowSourceKind.Image,
                    imagePath)],
                enhancementEnabled: false));

    private static WorkflowVisionEnvelope Envelope(
        ProductionWorkflowDetectionRequest request,
        string stage,
        string version,
        string modelId,
        char checksum,
        bool deterministic = false) =>
        new(
            1,
            request.RunId,
            request.ProjectId,
            request.Panel.ImportedPanel.PanelId,
            stage,
            version,
            request.Image.Sha256,
            deterministic ? null : new WorkflowVisionModel(modelId, version, new string(checksum, 64), "cpu"),
            new WorkflowVisionTiming(0, 0, 0, 0),
            0.95);

    private sealed class CandidateAxisAdapter : IProductionCandidateAxisGeometryAdapter
    {
        public string AdapterId => "candidate-axis";
        public bool IsApproved => false;

        public Task<ProductionAxisGeometryEvidence> DetectAsync(
            ProductionWorkflowDetectionRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Production entry must remain unavailable.");

        public Task<ProductionAxisGeometryEvidence> DetectForCandidateEvaluationAsync(
            ProductionWorkflowDetectionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xAxis = new AxisLineFit(
                new GeometryLineSegment(new AxisPixelPoint(10, 50), new AxisPixelPoint(70, 50)),
                0.98, 0, 1, ["x"]);
            var yAxis = new AxisLineFit(
                new GeometryLineSegment(new AxisPixelPoint(10, 50), new AxisPixelPoint(10, 10)),
                0.98, 0, 1, ["y"]);
            var geometry = new AxisGeometryResult(
                "original_pixels",
                new PlotPolygon(
                    new AxisPixelPoint(10, 50),
                    new AxisPixelPoint(70, 50),
                    new AxisPixelPoint(70, 10),
                    new AxisPixelPoint(10, 10)),
                xAxis,
                yAxis,
                [Tick("x1", TickAxis.XAxis, 20, 50), Tick("x2", TickAxis.XAxis, 60, 50)],
                [new PhaseDividerGeometry(
                    Guid.Parse("41111111-1111-1111-1111-111111111111").ToString("D"),
                    new GeometryLineSegment(new AxisPixelPoint(40, 10), new AxisPixelPoint(40, 50)),
                    DividerStyle.Solid, 0.95, 1, 1, ["divider"])],
                [],
                0.98,
                new AxisGeometryUncertainty(0, 0, 1, false, []),
                new AxisGeometryDiagnostics(4, 4, 0, 1, 4, 2, 1, 0, TimeSpan.Zero, []));
            return Task.FromResult(new ProductionAxisGeometryEvidence(
                Envelope(request, "axis", "candidate-axis-v1", "candidate-axis", 'a'),
                geometry));
        }

        private static AxisTickGeometry Tick(string id, TickAxis axis, double x, double y) =>
            new(id, axis, new AxisPixelPoint(x, y),
                new GeometryLineSegment(new AxisPixelPoint(x, y - 1), new AxisPixelPoint(x, y + 1)),
                0.95, [id]);
    }

    private sealed class CandidateOcrAdapter : IProductionCandidateOcrAdapter
    {
        public string AdapterId => "candidate-ocr";
        public bool IsApproved => false;

        public Task<ProductionOcrEvidence> RecognizeAsync(
            ProductionWorkflowDetectionRequest request,
            ProductionDecodedRaster originalRaster,
            OcrRectangle plotBounds,
            OcrDetectorImage detectorImage,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Production entry must remain unavailable.");

        public Task<ProductionOcrEvidence> RecognizeForCandidateEvaluationAsync(
            ProductionWorkflowDetectionRequest request,
            ProductionDecodedRaster originalRaster,
            OcrRectangle plotBounds,
            OcrDetectorImage detectorImage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRegion[] regions =
            [
                Region("x1", 18, 52, "1", OcrTextRole.XTick),
                Region("x2", 58, 52, "2", OcrTextRole.XTick),
                Region("y0", 2, 38, "0", OcrTextRole.YTick),
                Region("y100", 2, 18, "100", OcrTextRole.YTick),
                Region("participant", 55, 4, "Synthetic", OcrTextRole.Participant),
            ];
            var result = new OcrResult(
                OcrContract.Version,
                request.RunId.ToString("D"),
                request.ProjectId.ToString("D"),
                request.Panel.ImportedPanel.PanelId.ToString("D"),
                OcrContract.Stage,
                "candidate-ocr-v1",
                request.Image.Sha256,
                OcrContract.CoordinateSpace,
                regions,
                regions.Select(region => new OcrMask(region.RegionId, region.Polygon, region.Confidence)).ToArray(),
                new OcrTiming(0, 0, 0, 0),
                0.95,
                [],
                new OcrCacheDiagnostics(false, "candidate", regions.Length, 1),
                null,
                []);
            return Task.FromResult(new ProductionOcrEvidence(
                result,
                [
                    new ProductionOcrModelEvidence("ocr_detection", Envelope(request, "ocr", "candidate-ocr-v1", "detector", 'b')),
                    new ProductionOcrModelEvidence("ocr_recognition", Envelope(request, "ocr", "candidate-ocr-v1", "recognizer", 'c')),
                ],
                new ProductionOcrConfigurationEvidence(
                    [
                        new ProductionOcrConfiguredModel(
                            "ocr_detection",
                            new ModelIdentity("detector", "p1", new string('b', 64), "candidate-detector.onnx"),
                            InferenceProvider.Cpu),
                        new ProductionOcrConfiguredModel(
                            "ocr_recognition",
                            new ModelIdentity("recognizer", "p1", new string('c', 64), "candidate-recognizer.onnx"),
                            InferenceProvider.Cpu),
                    ],
                    ProductionOcrConfigurationScope.UnapprovedLocalSyntheticCandidate)));
        }

        private static OcrRegion Region(string id, double x, double y, string text, OcrTextRole role) =>
            new(
                id,
                OcrPolygon.FromRectangle(new OcrRectangle(x, y, 4, 4)),
                text,
                [new OcrRecognitionAlternative(text, 0.98, OcrSourceImage.Original)],
                role,
                0.98,
                OcrSourceImage.Original,
                OcrReviewStatus.Unreviewed);
    }

    private sealed class CandidateMaskComposer : IProductionCandidateDetectionMaskComposer
    {
        public string AdapterId => "candidate-mask";
        public bool IsApproved => false;

        public Task<ProductionDetectionMaskEvidence> ComposeAsync(
            ProductionWorkflowDetectionRequest request,
            ProductionDecodedRaster raster,
            ProductionAxisGeometryEvidence axisEvidence,
            ProductionOcrEvidence ocrEvidence,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Production entry must remain unavailable.");

        public Task<ProductionDetectionMaskEvidence> ComposeForCandidateEvaluationAsync(
            ProductionWorkflowDetectionRequest request,
            ProductionDecodedRaster raster,
            ProductionAxisGeometryEvidence axisEvidence,
            ProductionOcrEvidence ocrEvidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProductionDetectionMaskEvidence(
                raster.Width,
                raster.Height,
                request.Image.Sha256,
                request.ImageVariant,
                new[] { axisEvidence.Envelope }.Concat(ocrEvidence.ModelEvidence.Select(static item => item.Envelope)),
                Envelope(request, "markers", "candidate-mask-v1", "candidate-mask", 'd'),
                new float[checked(raster.Width * raster.Height)],
                new float[checked(raster.Width * raster.Height)],
                []));
        }
    }

    private sealed class CandidateCenterAdapter : IProductionCandidateMarkerCenterAdapter
    {
        public string AdapterId => "candidate-center";
        public bool IsApproved => false;
        public ModelIdentity Model { get; } = new(
            "candidate-center", "p1", new string('e', 64), "candidate-center.onnx");

        public Task<ProductionMarkerCenterEvidence> DetectAsync(
            ProductionWorkflowDetectionRequest request,
            MarkerImageFrame originalImage,
            MarkerPolygon plotPolygon,
            MarkerImageFrame? enhancedImage,
            IReadOnlyList<WorkflowTransformProvenance>? enhancedTransforms,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Production entry must remain unavailable.");

        public Task<ProductionMarkerCenterEvidence> DetectForCandidateEvaluationAsync(
            ProductionWorkflowDetectionRequest request,
            MarkerImageFrame originalImage,
            MarkerPolygon plotPolygon,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkerCenter[] markers =
            [
                new("candidate-1", new MarkerPoint(20, 35), 3, 0.01, 0.98, MarkerSourceImage.Original),
                new("candidate-2", new MarkerPoint(60, 25), 3, 0.01, 0.97, MarkerSourceImage.Original),
            ];
            return Task.FromResult(new ProductionMarkerCenterEvidence(
                Envelope(request, "markers", "candidate-center-v1", "candidate-center", 'e'),
                markers,
                []));
        }
    }

    private sealed class ClassificationAdapter : IProductionMarkerClassificationAdapter
    {
        public string AdapterId => "approved-fixed-classifier";
        public bool IsApproved => true;
        public ModelIdentity Model { get; } = new(
            "fixed-classifier", "v1", new string('f', 64), "fixed-classifier.onnx");

        public Task<ProductionMarkerClassificationEvidence> ClassifyAsync(
            ProductionWorkflowDetectionRequest request,
            MarkerImageFrame image,
            IReadOnlyList<MarkerCenter> markers,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClassifiedMarker[] classified =
            [
                new(markers[0], MarkerShape.Circle, MarkerFill.Filled, "●", "filled circle", 0.01, 0.98, 0.98, Enumerable.Repeat(0.1f, 12)),
                new(markers[1], MarkerShape.Square, MarkerFill.Open, "□", "open square", 0.01, 0.97, 0.97, Enumerable.Repeat(0.2f, 12)),
            ];
            return Task.FromResult(new ProductionMarkerClassificationEvidence(
                Envelope(request, "markers", "fixed-classifier-v1", "fixed-classifier", 'f'),
                classified));
        }
    }

    private sealed class LegendAdapter : IProductionLegendReasoningAdapter
    {
        public string AdapterId => "approved-fixed-legend";
        public bool IsApproved => true;

        public Task<ProductionLegendReasoningEvidence> ResolveAsync(
            ProductionWorkflowDetectionRequest request,
            LegendReasoningRequest legendRequest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LegendSeriesResolution[] series = legendRequest.Series.Select(item =>
                new LegendSeriesResolution(
                    item.SeriesId,
                    item.CurrentName ?? item.AccessibleName,
                    item.Symbol,
                    item.AccessibleName,
                    LegendEvidenceSource.SymbolFallback,
                    EntryId: null,
                    SourcePanelId: null,
                    0.9,
                    new LegendSemanticEvidence(LegendSemanticHint.Unknown, string.Empty, 0.9),
                    UserConfirmedPreserved: false)).ToArray();
            return Task.FromResult(new ProductionLegendReasoningEvidence(
                Envelope(request, "legends", "fixed-legend-v1", "fixed-legend", '1', deterministic: true),
                new LegendReasoningPayload(
                    [], series, [], [],
                    [new LegendParticipantMetadata("participant", "Synthetic", new LegendRectangle(55, 4, 20, 4), 0.98)],
                    [])));
        }
    }

    private sealed class PhaseAdapter : IProductionPhaseReasoningAdapter
    {
        private static readonly string DividerId = Guid.Parse("41111111-1111-1111-1111-111111111111").ToString("D");
        private static readonly string BaselineId = Guid.Parse("51111111-1111-1111-1111-111111111111").ToString("D");
        private static readonly string InterventionId = Guid.Parse("61111111-1111-1111-1111-111111111111").ToString("D");
        private readonly CancellationTokenSource? cancelAfterPhase;

        public PhaseAdapter(CancellationTokenSource? cancelAfterPhase = null) =>
            this.cancelAfterPhase = cancelAfterPhase;

        public string AdapterId => "approved-fixed-phase";
        public bool IsApproved => true;

        public Task<ProductionPhaseReasoningEvidence> ResolveAsync(
            ProductionWorkflowDetectionRequest request,
            PhaseReasoningRequest phaseRequest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var divider = new PhaseDivider(
                DividerId, 40, PhaseDividerStyle.Solid, [],
                [request.Panel.ImportedPanel.PanelId.ToString("D")],
                0.95, PhaseEvidenceSource.ProfilePrior);
            PhaseRegion[] phases =
            [
                new(BaselineId, 1, "a", PhaseNormalizedType.Baseline, "Baseline", 10, 40, null, DividerId, 0.95, PhaseEvidenceSource.ProfilePrior),
                new(InterventionId, 2, "b", PhaseNormalizedType.Intervention, "Intervention", 40, 70, DividerId, null, 0.95, PhaseEvidenceSource.ProfilePrior),
            ];
            PhasePointAssignment[] assignments = phaseRequest.Points.Select(point =>
                new PhasePointAssignment(point.PointId, point.Center.X < 40 ? BaselineId : InterventionId, point.Center.X)).ToArray();
            var evidence = new ProductionPhaseReasoningEvidence(
                Envelope(request, "phases", "fixed-phase-v1", "fixed-phase", '2', deterministic: true),
                new PhaseReasoningPayload([divider], phases, assignments, [], new PhaseManualOverrides()));
            cancelAfterPhase?.Cancel();
            return Task.FromResult(evidence);
        }
    }

    private sealed class EmptyConnectionBuilder : IMarkerConnectionGraphBuilder
    {
        public ValueTask<IReadOnlyList<MarkerConnection>> BuildAsync(
            MarkerConnectionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<MarkerConnection>>([]);
        }
    }

    private sealed class SingleCropPanelizationEngine : IPdfPanelizationEngine
    {
        public Task<PdfPanelizationResult> ProposeAsync(
            PdfPanelizationInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfRenderedPage rendered = input.RenderedPage ?? throw new InvalidOperationException("Rendered input required.");
            var evidence = new PdfPanelEvidence(PdfPanelEvidenceKind.DenseLineStructure, 0.9, "candidate workflow fixture");
            Guid figureId = Guid.Parse("71111111-1111-1111-1111-111111111111");
            var figure = new PdfFigureCandidate(
                figureId,
                1,
                PdfFigureSourceKind.RenderedPage,
                null,
                new PdfRectD(0, 0, rendered.Width, rendered.Height),
                new PdfRectD(0, 0, rendered.Width, rendered.Height),
                rendered.Width,
                rendered.Height,
                rendered.PngBytes,
                "image/png",
                null,
                [evidence],
                0.9);
            var panel = new PdfPanelRecord(
                Guid.Parse("81111111-1111-1111-1111-111111111111"),
                figureId,
                1,
                1,
                SourceCrop,
                SourceCrop,
                SourceCrop,
                null,
                null,
                [],
                [evidence],
                0.9);
            return Task.FromResult(new PdfPanelizationResult([figure], [panel]));
        }

        public PdfPanelizationResult ApplySplit(PdfPanelizationResult current, PdfManualSplitCommand command) =>
            throw new NotSupportedException();

        public PdfPanelizationResult ApplyMerge(PdfPanelizationResult current, PdfManualMergeCommand command) =>
            throw new NotSupportedException();
    }

    private static string WritePng(string directory, string fileName, int width, int height)
    {
        byte[] pixels = Enumerable.Repeat((byte)255, checked(width * height * 4)).ToArray();
        BitmapSource bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(directory, fileName);
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "GraphReader.App.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
