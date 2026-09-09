// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Integration;
using GraphReader.Axis;
using GraphReader.Export;
using GraphReader.Inference;
using ImageImportService = GraphReader.Imaging.ImageImportService;
using GraphReader.Legends;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Markers.Grouping;
using GraphReader.Ocr;
using GraphReader.Phases;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record WorkflowSyntheticExportRow(double X, double Y, string Phase);

internal sealed record WorkflowSyntheticExportResult(
    bool DetectionRan,
    bool SyntheticBoundaryAccepted,
    bool ExportSucceeded,
    bool SourceUnchanged,
    IReadOnlyList<WorkflowStep> Steps,
    IReadOnlyList<WorkflowSyntheticExportRow> Rows);

/// <summary>
/// Project-owned workflow boundary fixture. It deliberately uses deterministic
/// test-only stage adapters, but exercises image import, the production
/// detection interface, review projection, and the real ExportService. Its
/// accepted boundary is fixture readiness only, never production approval.
/// </summary>
internal static class WorkflowSyntheticAcceptance
{
    private static readonly string[] ExpectedStages = ["axis", "ocr", "ocr", "markers", "markers", "markers", "legends", "phases"];

    internal static async Task<object> RunGroupedAdapterAsync()
    {
        string root = Path.Combine(Path.GetFullPath("artifacts/goal22-runs/grouped-workflow-adapter-selftest"),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string imagePath = Path.Combine(root, "fictitious-source.png");
        WriteSyntheticPng(imagePath, 100, 100);
        byte[] source = await File.ReadAllBytesAsync(imagePath).ConfigureAwait(false);
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source));
        var store = new ProductionWorkflowPanelStore();
        var detector = new ProductionAutomaticDetectionAdapter(
            store,
            new ProductionRasterFrameDecoder(),
            new SyntheticAxisAdapter(),
            CreateSyntheticOcrAdapter(),
            new ProductionDetectionMaskComposer(new SyntheticArtifactMaskAdapter()),
            new SyntheticCenterAdapter(),
            new SyntheticClassificationAdapter(),
            new SyntheticLegendAdapter(),
            new SyntheticPhaseAdapter(),
            new SyntheticConnectionBuilder());
        var orchestrator = new WorkflowOrchestrator(new WorkflowServiceSet(
            new ProductionWorkflowImportStage(store, new ImageImportService()),
            new ProductionWorkflowPrepareStage(store),
            new ProductionWorkflowDetectionStage(store, detector),
            new ProductionWorkflowExportStage(store, new ExportService())));
        var adapter = new FrozenCandidateGroupedWorkflowAdapter(orchestrator, root,
            Path.Combine(root, "artifacts", "private-acceptance", "synthetic-run"),
            "fictitious-integration-candidate");
        var image = new EngaugeGroupedWorkflowImageInput(
            "fictitious-image", sourceSha256, 100, 100, source);
        WholeWorkflowCaseOutput result = await adapter.ExecuteAsync(image, CancellationToken.None)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Grouped adapter returned no result.");
        if (!result.WorkflowSucceeded || result.CaseKey != image.CaseKey || result.SourceSha256 != sourceSha256 ||
            result.Artifacts.Count < 3 || result.Artifacts.Any(static artifact =>
                !File.Exists(artifact.WrittenPath) || artifact.RowCount == 0))
        {
            throw new InvalidOperationException("Grouped adapter did not write actual workflow exports.");
        }
        foreach (WholeWorkflowCsvArtifact artifact in result.Artifacts)
        {
            byte[] bytes = await File.ReadAllBytesAsync(artifact.WrittenPath!).ConfigureAwait(false);
            if (artifact.Sha256 != Convert.ToHexStringLower(SHA256.HashData(bytes)))
            {
                throw new InvalidOperationException("Grouped adapter artifact checksum mismatch.");
            }
        }
        WholeWorkflowCaseOutput duplicate = await adapter.ExecuteAsync(image, CancellationToken.None)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Grouped adapter lost duplicate failure.");
        if (duplicate.WorkflowSucceeded || duplicate.FailureCode != "GROUPED_WORKFLOW_SOURCE_ALREADY_EXECUTED" ||
            sourceSha256 != Convert.ToHexStringLower(SHA256.HashData(
                await File.ReadAllBytesAsync(imagePath).ConfigureAwait(false))))
        {
            throw new InvalidOperationException("Grouped adapter source preservation failed.");
        }
        string memoryRoot = Path.Combine(root, "artifacts", "private-acceptance", "memory-only-run");
        var memoryAdapter = new FrozenCandidateGroupedWorkflowAdapter(orchestrator, root,
            memoryRoot, "fictitious-in-memory-candidate", aggregateOnly: true);
        WholeWorkflowCaseOutput memoryResult = await memoryAdapter.ExecuteAsync(image, CancellationToken.None)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("In-memory adapter returned no result.");
        if (!memoryResult.WorkflowSucceeded || memoryResult.Artifacts.Count != result.Artifacts.Count ||
            Directory.Exists(memoryRoot) || memoryResult.Artifacts.Any(static artifact =>
                artifact.WrittenPath is not null || artifact.Content is null ||
                artifact.Sha256 != Convert.ToHexStringLower(SHA256.HashData(artifact.Content.CopyBytes()))))
        {
            throw new InvalidOperationException("In-memory workflow persisted or changed an export artifact.");
        }
        string[] expectedMinimalHashes = result.Artifacts.Where(static artifact =>
                artifact.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
                !artifact.FileName.EndsWith(".audit.csv", StringComparison.OrdinalIgnoreCase))
            .Select(static artifact => artifact.Sha256).Order(StringComparer.Ordinal).ToArray();
        string[] memoryMinimalHashes = memoryResult.Artifacts.Where(static artifact =>
                artifact.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
                !artifact.FileName.EndsWith(".audit.csv", StringComparison.OrdinalIgnoreCase))
            .Select(static artifact => artifact.Sha256).Order(StringComparer.Ordinal).ToArray();
        WholeWorkflowCaseOutput memoryDuplicate = await memoryAdapter.ExecuteAsync(image, CancellationToken.None)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("In-memory adapter lost duplicate failure.");
        if (!expectedMinimalHashes.SequenceEqual(memoryMinimalHashes, StringComparer.Ordinal) ||
            memoryDuplicate.WorkflowSucceeded || memoryDuplicate.FailureCode != "GROUPED_WORKFLOW_SOURCE_ALREADY_EXECUTED" ||
            Directory.Exists(memoryRoot))
        {
            throw new InvalidOperationException("In-memory workflow changed CSV values or repeated an image.");
        }
        string unusedCacheRoot = Path.Combine(root, "no-persistence-cache");
        var noPersistenceCache = new NoPersistenceStageCache();
        var cacheKey = new StageCacheKey("fictitious-test-key");
        await noPersistenceCache.PutAsync(cacheKey, new byte[] { 1, 2, 3 }, CancellationToken.None).ConfigureAwait(false);
        await using (var runtime = new ProductionInferenceRuntimeHost(
            new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(),
            new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled),
            CpuThreadConfiguration.Create(1), [InferenceProvider.Cpu], unusedCacheRoot, 1, 1, noPersistenceCache))
        {
            _ = runtime.Runtime;
            if (await noPersistenceCache.TryGetAsync(cacheKey, CancellationToken.None).ConfigureAwait(false) is not null ||
                Directory.Exists(unusedCacheRoot))
            {
                throw new InvalidOperationException("Aggregate inference cache retained case bytes.");
            }
        }
        return new
        {
            status = "pass", actual_workflow_import_prepare_detect_review_export = true,
            artifact_count = result.Artifacts.Count, source_unchanged = true,
            repeated_source_rejected = true, reference_truth_sent_to_adapter = false,
            in_memory_workflow_verified = true, in_memory_case_artifacts_persisted = false,
            in_memory_minimal_csv_matches_file_export = true,
            inference_cache_persistence_disabled = true,
            stage_adapters = "deterministic-fictitious-test-only", model_runs = 0,
            private_reads = 0, sealed_reads = 0, production_approved = false,
            evidence_directory = Path.GetRelativePath(Environment.CurrentDirectory, root).Replace('\\', '/'),
        };
    }

    public static async Task<WorkflowSyntheticExportResult> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"graphreader-goal22-workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string imagePath = Path.Combine(root, "synthetic.png");
            WriteSyntheticPng(imagePath, 100, 100);
            byte[] sourceBytes = await File.ReadAllBytesAsync(imagePath).ConfigureAwait(false);
            string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
            Guid projectId = Guid.Parse("21000000-0000-0000-0000-000000000022");
            Guid sourceId = Guid.Parse("31000000-0000-0000-0000-000000000022");
            Guid runId = Guid.Parse("41000000-0000-0000-0000-000000000022");
            var store = new ProductionWorkflowPanelStore();
            var adapter = new ProductionAutomaticDetectionAdapter(
                store,
                new ProductionRasterFrameDecoder(),
                new SyntheticAxisAdapter(),
                CreateSyntheticOcrAdapter(),
                new ProductionDetectionMaskComposer(new SyntheticArtifactMaskAdapter()),
                new SyntheticCenterAdapter(),
                new SyntheticClassificationAdapter(),
                new SyntheticLegendAdapter(),
                new SyntheticPhaseAdapter(),
                new SyntheticConnectionBuilder());
            var orchestrator = new WorkflowOrchestrator(new WorkflowServiceSet(
                new ProductionWorkflowImportStage(store, new ImageImportService()),
                new ProductionWorkflowPrepareStage(store),
                new ProductionWorkflowDetectionStage(store, adapter),
                new ProductionWorkflowExportStage(store, new ExportService())));
            var request = new WorkflowRunRequest(
                runId,
                new WorkflowImportRequest(
                    projectId,
                    [new WorkflowSourceRequest(sourceId, WorkflowSourceKind.Image, imagePath)],
                    enhancementEnabled: false));

            WorkflowRunResult workflow = await orchestrator
                .RunThroughReviewAsync(request, previousReview: null, CancellationToken.None)
                .ConfigureAwait(false);
            WorkflowReviewPanel panel = workflow.Review.Panels.Single();
            string outputDirectory = Path.Combine(root, "export");
            WorkflowExportResult export = await orchestrator
                .ExportAsync(
                    workflow.Review,
                    new WorkflowExportRequest(
                        Guid.Parse("51000000-0000-0000-0000-000000000022"),
                        outputDirectory),
                    CancellationToken.None)
                .ConfigureAwait(false);
            var rows = new List<WorkflowSyntheticExportRow>();
            foreach (WorkflowExportArtifact artifact in export.Artifacts.Where(static item =>
                         item.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
                         !item.FileName.Contains("audit", StringComparison.OrdinalIgnoreCase)))
            {
                if (artifact.WrittenPath is null || !File.Exists(artifact.WrittenPath))
                {
                    throw new InvalidOperationException("Workflow export reported a missing CSV.");
                }

                string[] lines = await File.ReadAllLinesAsync(artifact.WrittenPath).ConfigureAwait(false);
                if (lines.Length == 0 || !string.Equals(lines[0], ExportContract.MinimalCsvHeader, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Workflow export wrote an invalid minimal CSV header.");
                }

                foreach (string line in lines.Skip(1).Where(static line => line.Length > 0))
                {
                    string[] fields = line.Split(',');
                    if (fields.Length != 3 ||
                        !double.TryParse(fields[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x) ||
                        !double.TryParse(fields[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y))
                    {
                        throw new InvalidOperationException("Workflow export wrote an invalid minimal CSV row.");
                    }

                    rows.Add(new WorkflowSyntheticExportRow(x, y, fields[2]));
                }
            }

            ProductionPanelExportEvidence evidence = store.Get(panel.PanelId).ExportEvidence ??
                throw new InvalidOperationException("Workflow detection did not retain export evidence.");
            if (!export.Succeeded || rows.Count == 0 || !adapter.IsApproved ||
                !panel.Points.All(static point => point.GraphX.HasValue && point.GraphY.HasValue) ||
                !evidence.Provenance.Select(static item => item.Stage).SequenceEqual(ExpectedStages, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Workflow synthetic acceptance boundary did not complete.");
            }

            bool sourceUnchanged = sourceSha256 == Convert.ToHexStringLower(
                SHA256.HashData(await File.ReadAllBytesAsync(imagePath).ConfigureAwait(false)));
            return new WorkflowSyntheticExportResult(
                DetectionRan: workflow.Steps.Any(static step => step.Step == WorkflowStep.Detect),
                SyntheticBoundaryAccepted: adapter.IsApproved,
                ExportSucceeded: export.Succeeded,
                SourceUnchanged: sourceUnchanged,
                Steps: workflow.Steps.Select(static step => step.Step).ToArray(),
                Rows: rows);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static WorkflowVisionEnvelope Envelope(
        ProductionWorkflowDetectionRequest request,
        string stage,
        string version,
        string modelId,
        char checksum,
        bool deterministic = false) => new(
        1,
        request.RunId,
        request.ProjectId,
        request.Panel.ImportedPanel.PanelId,
        stage,
        version,
        request.Image.Sha256,
        deterministic ? null : new WorkflowVisionModel(modelId, version, new string(checksum, 64), "cpu"),
        new WorkflowVisionTiming(1, 1, 1, 3),
        0.95,
        transforms: request.Transforms);

    private sealed class SyntheticAxisAdapter : IProductionAxisGeometryAdapter
    {
        public string AdapterId => "synthetic-axis";
        public bool IsApproved => true;

        public Task<ProductionAxisGeometryEvidence> DetectAsync(ProductionWorkflowDetectionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xAxis = new AxisLineFit(new GeometryLineSegment(new PixelPoint(10, 90), new PixelPoint(90, 90)), .98, 0, 1, ["x"]);
            var yAxis = new AxisLineFit(new GeometryLineSegment(new PixelPoint(10, 90), new PixelPoint(10, 10)), .98, 0, 1, ["y"]);
            var geometry = new AxisGeometryResult(
                AxisGeometryCoordinateSpaces.OriginalPixels,
                new PlotPolygon(new PixelPoint(10, 90), new PixelPoint(90, 90), new PixelPoint(90, 10), new PixelPoint(10, 10)),
                xAxis,
                yAxis,
                [Tick("x-1", TickAxis.XAxis, 20, 90), Tick("x-2", TickAxis.XAxis, 80, 90)],
                [new PhaseDividerGeometry("50000000-0000-0000-0000-000000000022", new GeometryLineSegment(new PixelPoint(50, 10), new PixelPoint(50, 90)), DividerStyle.Solid, .95, 1, 1, ["divider"])],
                [], .98, new AxisGeometryUncertainty(0, 0, 1, false, []),
                new AxisGeometryDiagnostics(5, 5, 0, 1, 4, 2, 1, 0, TimeSpan.Zero, []));
            return Task.FromResult(new ProductionAxisGeometryEvidence(Envelope(request, "axis", "axis-v1", "synthetic-axis", 'a'), geometry));
        }

        private static AxisTickGeometry Tick(string id, TickAxis axis, double x, double y) =>
            new(id, axis, new PixelPoint(x, y), new GeometryLineSegment(new PixelPoint(x, y - 1), new PixelPoint(x, y + 1)), .95, [id]);
    }

    private static ProductionOcrAdapter CreateSyntheticOcrAdapter()
    {
        var pipeline = new OcrPipeline(
            new SyntheticTextDetector(),
            new SyntheticTextRecognizer(),
            new SyntheticOcrCache(),
            new OcrPipelineOptions { CropPaddingPixels = 0, MaskPaddingPixels = 0 });
        // This private, parameterless self-test generates its own fixed image.
        // The trusted test seam exercises production-path wiring with fixtures;
        // it never registers model-store approval or accepts corpus inputs.
        return ProductionOcrAdapter.CreateFromValidatedApprovedPipeline(
            pipeline,
            new ModelIdentity("synthetic-ocr-detection", "ocr-v1", new string('b', 64), "memory:synthetic-ocr-detection.onnx"),
            InferenceProvider.Cpu,
            new ModelIdentity("synthetic-ocr-recognition", "ocr-v1", new string('c', 64), "memory:synthetic-ocr-recognition.onnx"),
            InferenceProvider.Cpu,
            new string('a', 64));
    }

    private sealed class SyntheticTextDetector : ITextRegionDetector
    {
        public string ConfigurationFingerprint => "synthetic-text-detector-v1";

        public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(OcrImage image, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<OcrDetectedRegion>>(
            [
                Region("x1", 18, 92, OcrTextRole.XTick),
                Region("x2", 78, 92, OcrTextRole.XTick),
                Region("y0", 1, 78, OcrTextRole.YTick),
                Region("y100", 1, 18, OcrTextRole.YTick),
                Region("participant", 82, 84, OcrTextRole.Participant),
            ]);
        }

        private static OcrDetectedRegion Region(string id, double x, double y, OcrTextRole role) =>
            new(id, OcrPolygon.FromRectangle(new OcrRectangle(x, y, 4, 4)), 0, .98,
                new OcrRegionContext(ExplicitRoleHint: role));
    }

    private sealed class SyntheticTextRecognizer : ITextRecognizer
    {
        private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
        {
            ["x1"] = "1", ["x2"] = "2", ["y0"] = "0", ["y100"] = "100", ["participant"] = "Synthetic participant",
        };

        public string ModelId => "synthetic-ocr-recognition";
        public string ModelVersion => "ocr-v1";
        public string ModelSha256 => new('c', 64);
        public string ConfigurationFingerprint => "synthetic-text-recognizer-v1";

        public ValueTask<IReadOnlyList<OcrRecognition>> RecognizeBatchAsync(IReadOnlyList<OcrCrop> crops, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                new OcrRecognition(crop.RegionId, OcrSourceImage.Original,
                    [new OcrRecognitionAlternative(Texts[crop.RegionId], .98, OcrSourceImage.Original)], 0)).ToArray());
        }
    }

    private sealed class SyntheticOcrCache : IOcrResultCache
    {
        public ValueTask<OcrCachedPayload?> TryGetAsync(string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult<OcrCachedPayload?>(null);
        public ValueTask PutAsync(string key, OcrCachedPayload payload, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask<OcrRecognitionCachePayload?> TryGetRecognitionAsync(string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult<OcrRecognitionCachePayload?>(null);
        public ValueTask PutRecognitionAsync(string key, OcrRecognitionCachePayload payload, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class SyntheticArtifactMaskAdapter : IProductionArtifactMaskAdapter
    {
        public string AdapterId => "synthetic-masks";
        public bool IsApproved => true;

        public Task<ProductionArtifactMaskEvidence> DetectAsync(
            ProductionWorkflowDetectionRequest request,
            ProductionDecodedRaster raster,
            ProductionDetectionMaskSeed seed,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProductionArtifactMaskEvidence(
                raster.Width, raster.Height, request.Image.Sha256, request.ImageVariant,
                Envelope(request, "markers", "artifact-mask-v1", "synthetic-artifact-mask", 'f'),
                new float[checked(raster.Width * raster.Height)]));
        }
    }

    private sealed class SyntheticCenterAdapter : IProductionMarkerCenterAdapter
    {
        public string AdapterId => "synthetic-centers";
        public bool IsApproved => true;
        public ModelIdentity Model { get; } = new("synthetic-center", "center-v1", new string('d', 64), "memory:synthetic-center.onnx");

        public Task<ProductionMarkerCenterEvidence> DetectAsync(
            ProductionWorkflowDetectionRequest request,
            MarkerImageFrame originalImage,
            MarkerPolygon plotPolygon,
            MarkerImageFrame? enhancedImage,
            IReadOnlyList<WorkflowTransformProvenance>? enhancedTransforms,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkerCenter[] markers =
            [
                new("raw-1", new MarkerPoint(20, 70), 3, .01, .98, MarkerSourceImage.Original),
                new("raw-2", new MarkerPoint(80, 30), 3, .01, .97, MarkerSourceImage.Original),
            ];
            return Task.FromResult(new ProductionMarkerCenterEvidence(Envelope(request, "markers", "center-v1", "synthetic-center", 'd'), markers, []));
        }
    }

    private sealed class SyntheticClassificationAdapter : IProductionMarkerClassificationAdapter
    {
        public string AdapterId => "synthetic-classifier";
        public bool IsApproved => true;
        public ModelIdentity Model { get; } = new("synthetic-classifier", "classifier-v1", new string('e', 64), "memory:synthetic-classifier.onnx");

        public Task<ProductionMarkerClassificationEvidence> ClassifyAsync(
            ProductionWorkflowDetectionRequest request,
            MarkerImageFrame image,
            IReadOnlyList<MarkerCenter> markers,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClassifiedMarker[] classified =
            [
                new(markers[0], MarkerShape.Circle, MarkerFill.Filled, "●", "filled circle", .01, .98, .98, Enumerable.Repeat(.1f, 12)),
                new(markers[1], MarkerShape.Square, MarkerFill.Open, "□", "open square", .01, .97, .97, Enumerable.Repeat(.2f, 12)),
            ];
            return Task.FromResult(new ProductionMarkerClassificationEvidence(Envelope(request, "markers", "classifier-v1", "synthetic-classifier", 'e'), classified));
        }
    }

    private sealed class SyntheticLegendAdapter : IProductionLegendReasoningAdapter
    {
        public string AdapterId => "synthetic-legends";
        public bool IsApproved => true;

        public Task<ProductionLegendReasoningEvidence> ResolveAsync(ProductionWorkflowDetectionRequest request, LegendReasoningRequest legendRequest, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LegendSeriesResolution[] series = legendRequest.Series.Select(item => new LegendSeriesResolution(item.SeriesId, item.CurrentName ?? item.AccessibleName, item.Symbol, item.AccessibleName, LegendEvidenceSource.SymbolFallback, null, null, .9, new LegendSemanticEvidence(LegendSemanticHint.Unknown, string.Empty, .9), false)).ToArray();
            var payload = new LegendReasoningPayload([], series, [], [], [new LegendParticipantMetadata("participant", "Synthetic participant", new LegendRectangle(82, 84, 12, 4), .98)], []);
            return Task.FromResult(new ProductionLegendReasoningEvidence(Envelope(request, "legends", "legend-v1", "synthetic-legend", 'f', deterministic: true), payload));
        }
    }

    private sealed class SyntheticPhaseAdapter : IProductionPhaseReasoningAdapter
    {
        private static readonly string DividerId = "60000000-0000-0000-0000-000000000022";
        private static readonly string BaselineId = "70000000-0000-0000-0000-000000000022";
        private static readonly string InterventionId = "80000000-0000-0000-0000-000000000022";
        public string AdapterId => "synthetic-phases";
        public bool IsApproved => true;

        public Task<ProductionPhaseReasoningEvidence> ResolveAsync(ProductionWorkflowDetectionRequest request, PhaseReasoningRequest phaseRequest, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var divider = new PhaseDivider(DividerId, 50, PhaseDividerStyle.Solid, [], [request.Panel.ImportedPanel.PanelId.ToString("D")], .95, PhaseEvidenceSource.ProfilePrior);
            PhaseRegion[] phases =
            [
                new(BaselineId, 1, "a", PhaseNormalizedType.Baseline, "Baseline", 10, 50, null, DividerId, .95, PhaseEvidenceSource.ProfilePrior),
                new(InterventionId, 2, "b", PhaseNormalizedType.Intervention, "Intervention", 50, 90, DividerId, null, .95, PhaseEvidenceSource.ProfilePrior),
            ];
            PhasePointAssignment[] assignments = phaseRequest.Points.Select(point => new PhasePointAssignment(point.PointId, point.Center.X < 50 ? BaselineId : InterventionId, point.Center.X)).ToArray();
            var payload = new PhaseReasoningPayload([divider], phases, assignments, [], new PhaseManualOverrides());
            return Task.FromResult(new ProductionPhaseReasoningEvidence(Envelope(request, "phases", "phase-v1", "synthetic-phase", '1', deterministic: true), payload));
        }
    }

    private sealed class SyntheticConnectionBuilder : IMarkerConnectionGraphBuilder
    {
        public ValueTask<IReadOnlyList<MarkerConnection>> BuildAsync(MarkerConnectionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<MarkerConnection>>([]);
        }
    }

    private static void WriteSyntheticPng(string path, int width, int height)
    {
        byte[] pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
