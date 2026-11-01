// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration;
using GraphReader.App.Integration.Workflow;
using GraphReader.Imaging;
using GraphReader.Inference;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using OpenCvSharp;

namespace GraphReader.RealAcceptance.Ocr;

internal static class Program
{
    private const string Root = @"E:\Graph-auto-reader";
    private const string OutputRoot = "artifacts/manual-marker-input-diagnostic-v1";
    private const string RasterRoot = "artifacts/goal22-runs/marker-train-overlay-corpus-render-v7/";
    private const string ReceiptSha = "b7631113c9425cd06c62e4198dd173e8423013d973040e4a05ef5448bcd7a02f";
    private const string OriginalRasterRoot = "artifacts/goal22-runs/marker-train-overlay-corpus-extract-v7/";
    private const string OriginalReceiptSha = "23964f24a72fa7c34344f252f08d17582e6855111945ffd4c360fdeb7977bd21";
    private const string ModelBinding = "artifacts/goal22-runs/marker-context-center-only-binding-v1.json";
    private const string ModelBindingSha = "51c7b65a076579764da6b58c51ea4ff0c10bcd48232e1ac1b625b74cf100be73";
    private const string OpenCvSha = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31";
    private const string ClassifierStore = "artifacts/production-model-store/package";
    private static readonly (string Path, string Sha)[] ClassifierFiles =
    [
        ("production-model-index.json", "2cbd8adb51f80307b1ca4f011d54f108a78c3299c901e680ed92696ec46750a8"),
        ("manifest/graph-marker-classifier/0.1.0/manifest.json", "fb0aebde5785e760c8c10bcf7fdb2c04dba7e63315d16ddcb6dbc6b25d4af312"),
        ("runtime/graph-marker-classifier/0.1.0/marker-classifier-probability-packed.onnx", "26f9304f1689053a0b94aa896a1e239f6ade1e5c1920736a3535c1b32f803b8a"),
        ("notices/graph-marker-classifier/0.1.0/MARKER_CLASSIFIER_MODEL_NOTICE.md", "b89466813efc2841b429b158ab6051686920a7888868869149e60cf9cde39b0c"),
        ("evidence/graph-marker-classifier/0.1.0/marker-classifier-production-approval.json", "c4fb25e45e9c6d77100de8230a30443231445fa71751d685ba66c65da370e7a3"),
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly string[] Sources =
    [
        "tools/GraphReader.ManualOverlayCapture/GraphReader.ManualOverlayCapture.csproj",
        "tools/GraphReader.ManualOverlayCapture/Program.cs",
        "tools/GraphReader.ManualOverlayCapture/prepare_input.py",
        "tools/GraphReader.RealAcceptance.Ocr/FrozenCandidateBinding.cs",
        "tools/GraphReader.RealAcceptance.Ocr/NoPersistenceStageCache.cs",
        "tools/GraphReader.MarkerBoundInputTrainCapture/CandidateInterfaces.cs",
        "tools/GraphReader.MarkerDualViewTrainCapture/PanelProvenance.cs",
        "tools/Shared/ComposedOcrExecutionIdentity.cs",
    ];

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] is "--describe" or "--describe-workflow")
            {
                string path = OutputPath(args[1]);
                byte[] bytes = Describe(args[0] == "--describe-workflow");
                await WriteNew(path, bytes);
                Console.WriteLine($"runtime_description_sha256={Hash(bytes)}");
                return 0;
            }
            if (args.Length == 6 && args[0] is "--capture" or "--capture-workflow" or "--capture-workflow-smoke")
                return await Capture(args[1], args[2], args[3], args[4], args[5],
                    workflow: args[0] != "--capture", smoke: args[0] == "--capture-workflow-smoke");
            Console.Error.WriteLine("Usage: --describe[-workflow] <new-json> OR --capture[-workflow|-workflow-smoke] <raster-manifest> <manifest-sha256> <runtime-description> <description-sha256> <new-output-directory>");
            return 2;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"BLOCKED: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static byte[] Describe(bool workflow)
    {
        string native = NativePath();
        Require(Hash(File.ReadAllBytes(native)) == OpenCvSha, "Reviewed OpenCV bytes differ.");
        var artifact = new RasterResidualArtifactMaskAdapter();
        var runtimeFiles = Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new { file = Relative(path), sha256 = Hash(File.ReadAllBytes(path)) }).ToArray();
        var sourceFiles = Sources.Select(path => new { file = path, sha256 = Hash(File.ReadAllBytes(ProjectPath(path))) }).ToArray();
        var classifierFiles = workflow ? ClassifierFiles.Select(item =>
        {
            string path = $"{ClassifierStore}/{item.Path}";
            string sha = Hash(File.ReadAllBytes(ProjectPath(path)));
            Require(sha == item.Sha, "Classifier binding differs.");
            return new { file = path, sha256 = sha };
        }).ToArray() : [];
        return JsonSerializer.SerializeToUtf8Bytes(new { schema = "graphreader.manual-marker-capture-runtime.v1",
            scope = "train-only-unapproved-candidate-no-optimizer", model_binding_sha256 = ModelBindingSha,
            opencv_sha256 = OpenCvSha, artifact_configuration_sha256 = artifact.Identity.ConfigurationSha256,
            artifact_app_sha256 = artifact.Identity.AssemblySha256, artifact_ocr_sha256 = artifact.OcrAssemblySha256,
            source_files = sourceFiles, runtime_files = runtimeFiles, classifier_files = classifierFiles,
            workflow_enabled = workflow, optimizer_steps = 0, production_approval = false }, JsonOptions);
    }

    private static async Task<int> Capture(string inputPath, string inputSha, string descriptionPath, string descriptionSha,
        string outputName, bool workflow, bool smoke)
    {
        using var classifierLocks = workflow ? new ClassifierInputLocks() : null;
        byte[] description = await ReadPinned(descriptionPath, descriptionSha, 1_000_000);
        Require(description.AsSpan().SequenceEqual(Describe(workflow)), "Frozen runtime description differs.");
        byte[] input = await ReadPinned(inputPath, inputSha, 1_000_000);
        using JsonDocument manifest = JsonDocument.Parse(input);
        JsonElement root = manifest.RootElement;
        Exact(root, "schema", "scope", "receipt_sha256", "selection", "training_approved", "rows");
        string selection = root.GetProperty("selection").GetString() ?? "";
        bool original = selection == "original";
        string rasterRoot = original ? OriginalRasterRoot : RasterRoot;
        string inputScope = original ? "train-only-original-raster-no-truth" : "train-only-raster-no-truth";
        string receiptSha = original ? OriginalReceiptSha : ReceiptSha;
        string idPrefix = original ? "source-" : "overlay-";
        Require(root.GetProperty("schema").GetString() == "graphreader.manual-marker-raster-input.v1" &&
            root.GetProperty("scope").GetString() == inputScope &&
            root.GetProperty("receipt_sha256").GetString() == receiptSha &&
            root.GetProperty("training_approved").ValueKind == JsonValueKind.False, "Input scope differs.");
        var entries = new List<(string Id, string Path, string Sha)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var imageHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in root.GetProperty("rows").EnumerateArray())
        {
            Exact(row, "id", "image_path", "image_sha256");
            string id = row.GetProperty("id").GetString() ?? "";
            string relative = row.GetProperty("image_path").GetString() ?? "";
            string sha = row.GetProperty("image_sha256").GetString() ?? "";
            string rasterDirectory = ProjectPath(rasterRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Require(id.Length == idPrefix.Length + 3 && id.StartsWith(idPrefix, StringComparison.Ordinal) && id[idPrefix.Length..].All(char.IsAsciiDigit) &&
                ids.Add(id) && imageHashes.Add(sha) && relative.StartsWith(rasterRoot, StringComparison.Ordinal) &&
                ProjectPath(relative).StartsWith(rasterDirectory, StringComparison.OrdinalIgnoreCase) && relative.EndsWith(".png", StringComparison.Ordinal),
                "Invalid or repeated train raster identity.");
            entries.Add((id, relative, sha));
        }
        Require(((selection is "smoke" or "original") && entries.Count == 20) || (selection == "full" && entries.Count == 200),
            "Only the fixed 20-image or 200-image inputs are supported.");
        Require(!workflow || (original && inputSha == "b9044372d5d6b37257ef0e1823935eaed840b99f9dc5a66a2c5359a1b7beeba6"),
            "Workflow diagnostics require the pinned original train input.");
        if (smoke) entries = entries.Take(1).ToList();
        string output = OutputPath(outputName);
        Require(!Directory.Exists(output) && !File.Exists(output), "Output already exists.");
        Directory.CreateDirectory(output);

        byte[] modelBytes = await ReadPinned(ModelBinding, ModelBindingSha, 1_000_000);
        using JsonDocument modelDocument = JsonDocument.Parse(modelBytes);
        FrozenCandidateModel detector = ReadModel(modelDocument.RootElement.GetProperty("ocr_detection"), "ocr_detection");
        FrozenCandidateModel recognizer = ReadModel(modelDocument.RootElement.GetProperty("ocr_recognition"), "ocr_recognition");
        FrozenCandidateModel centerModel = ReadModel(modelDocument.RootElement.GetProperty("marker_center"), "marker_center");
        Require(detector.Payload.Sha256 == "497a3527820ce0359496084e5afb1e0d3241aca79e192187f7ab737b27818b7f" &&
            recognizer.Payload.Sha256 == "0980049401eceb506952f769765a6b95be07cb1197dcbb13c93f107a328f8418" &&
            centerModel.Payload.Sha256 == "4979777a404be48c6c75b490218d1de31d2edb2550f5bc71c7aa6894c1bcca04",
            "Candidate model payload identities differ.");
        nint native = NativeLibrary.Load(NativePath());
        NativeLibrary.SetDllImportResolver(typeof(Mat).Assembly,
            (name, _, _) => name == "OpenCvSharpExtern" ? native : nint.Zero);
        var artifact = new RasterResidualArtifactMaskAdapter();
        CpuThreadConfiguration cpu = CpuThreadConfiguration.Create(2);
        await using var runtime = new ProductionInferenceRuntimeHost(new OrtExecutionProviderDiscovery(),
            new WindowsExecutionProviderPolicy(), new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled),
            cpu, [InferenceProvider.Cpu], Path.Combine(output, "inference-cache"), 1, 1, new NoPersistenceStageCache());
        FrozenCandidateOcrModelDescriptor OcrDescriptor(FrozenCandidateModel model) => new(
            new ModelIdentity(model.ModelId, model.Version, model.Payload.Sha256, ProjectPath(model.Payload.RelativePath)),
            ProjectPath(model.Manifest.RelativePath), model.Manifest.Sha256);
        ProductionOcrAdapter ocr = await ProductionOcrAdapter.CreateForFrozenCandidateEvaluationAsync(
            OcrDescriptor(detector), OcrDescriptor(recognizer), runtime, OpenCvSha, CancellationToken.None,
            GraphStructureConsensusGeometry.ModelPolygon);
        Require(!ocr.IsApproved && ocr.ConfigurationScope == "unapproved_frozen_candidate", "OCR approval scope differs.");
        var markerDescriptor = new FrozenCandidateMarkerCenterModelDescriptor(
            new ModelIdentity(centerModel.ModelId, centerModel.Version, centerModel.Payload.Sha256, ProjectPath(centerModel.Payload.RelativePath)),
            ProjectPath(centerModel.Manifest.RelativePath), centerModel.Manifest.Sha256);
        ProductionProposalMarkerCenterAdapter center = ProductionProposalMarkerCenterAdapter.CreateForFrozenCandidateCascadeEvaluation(markerDescriptor, runtime.Runtime);
        Require(!center.IsApproved, "Marker model must remain unapproved.");
        IProductionMarkerClassificationAdapter classifier = new NoClassification();
        if (workflow)
        {
            var classifierStore = new ProductionModelStore(ProjectPath(ClassifierStore));
            ResolvedProductionModel model = await classifierStore.ResolveAsync(
                "graph-marker-classifier", "0.1.0", InferenceProvider.Cpu, CancellationToken.None);
            Require(model.Task == "marker_classifier" &&
                string.Equals(model.Identity.Sha256, ClassifierFiles[2].Sha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(model.ManifestSha256, ClassifierFiles[1].Sha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(model.NoticeSha256, ClassifierFiles[3].Sha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(model.BenchmarkEvidenceSha256, ClassifierFiles[4].Sha, StringComparison.OrdinalIgnoreCase),
                "Resolved classifier binding differs.");
            classifier = ProductionMarkerClassificationAdapter.Create(model, runtime);
            Require(classifier.IsApproved, "Classifier approval scope differs.");
        }

        var results = new List<object>();
        int failed = 0;
        foreach ((string id, string path, string sha) in entries)
        {
            var panelRows = new List<object>();
            bool sourceFailed = false;
            try
            {
                string sourcePath = ProjectPath(path);
                await using var sourceLock = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                _ = await ReadPinned(path, sha, 64_000_000);
                var store = new ProductionWorkflowPanelStore();
                var axis = new ObservedAxis(new ProductionAxisGeometryAdapter(OpenCvSha, isApproved: false));
                var observedOcr = new ObservedOcr(ocr);
                var automatic = new ProductionAutomaticDetectionAdapter(store, new ProductionRasterFrameDecoder(), axis, observedOcr,
                    new ProductionDetectionMaskComposer(artifact), center, classifier,
                    new ProductionLegendReasoningAdapter(), new ProductionPhaseReasoningAdapter());
                var importer = new ProductionWorkflowImportStage(store, new ImageImportService());
                Guid project = ProductionWorkflowPanelStore.CreateStableId("manual-overlay-capture-project-v1", inputSha);
                Guid sourceId = ProductionWorkflowPanelStore.CreateStableId("manual-overlay-capture-source-v1", inputSha, id, sha);
                WorkflowImportSnapshot snapshot = await importer.ImportAsync(new WorkflowImportRequest(project,
                    [new WorkflowSourceRequest(sourceId, WorkflowSourceKind.Image, sourcePath)], enhancementEnabled: false), CancellationToken.None);
                Require(snapshot.Panels.Count is > 0 and <= 8, "Unsupported imported panel count.");
                int panelIndex = 0;
                foreach (WorkflowImportedPanel panel in snapshot.Panels)
                {
                    ProductionPanelEvidence evidence = store.Get(panel.PanelId);
                    Require(panel.SourceId == sourceId && evidence.Panel.SourceId == sourceId &&
                        evidence.Panel.PanelId == panel.PanelId, "Imported panel identity differs.");
                    object provenance = PanelProvenance.Validate(evidence, sha, panel.Original.Sha256);
                    var prepared = new WorkflowPreparedPanel(panel, panel.Original, enhanced: null);
                    var request = new ProductionWorkflowDetectionRequest(prepared, panel.Original, WorkflowImageVariant.Original,
                        Guid.NewGuid(), project, evidence.CopyOriginalBytes());
                    if (workflow)
                    {
                        (object row, bool panelFailed) = await CaptureWorkflowPanel(automatic, request, panel, provenance);
                        panelRows.Add(row);
                        sourceFailed |= panelFailed;
                        continue;
                    }
                    ProductionCandidateMarkerFrameBundle bundle = await automatic.PrepareMarkerFrameBundleForCandidateEvaluationAsync(request, CancellationToken.None);
                    ProductionAxisGeometryEvidence geometry = axis.ForPanel(panel.PanelId);
                    var plot = new MarkerPolygon(geometry.Geometry.PlotPolygon.Points.Select(point => new MarkerPoint(point.X, point.Y)).ToArray());
                    ProductionMarkerCenterEvidence markers = await ((IProductionCandidateMarkerCenterAdapter)center)
                        .DetectForCandidateEvaluationAsync(request, bundle.Frame, plot, CancellationToken.None);
                    ProposalMarkerCandidateDiagnosticResult diagnostic = markers.CandidateDiagnostics
                        ?? throw new InvalidDataException("Candidate stage diagnostics are unavailable.");
                    string frameName = $"{id}-panel-{panelIndex++:D2}.bin.gz";
                    string framePath = Path.Combine(output, frameName);
                    WriteFrame(framePath, bundle.Frame);
                    string attributionName = frameName.Replace(".bin.gz", "-attribution.bin.gz", StringComparison.Ordinal);
                    string attributionPath = Path.Combine(output, attributionName);
                    WriteAttribution(attributionPath, bundle);
                    string stageName = frameName.Replace(".bin.gz", "-stages.json.gz", StringComparison.Ordinal);
                    string stagePath = Path.Combine(output, stageName);
                    await WriteStages(stagePath, geometry, diagnostic);
                    panelRows.Add(new { panel_id = panel.PanelId, panel_sha256 = panel.Original.Sha256, provenance,
                        frame_file = frameName, frame_sha256 = Hash(await File.ReadAllBytesAsync(framePath)),
                        attribution_file = attributionName, attribution_sha256 = Hash(await File.ReadAllBytesAsync(attributionPath)),
                        stage_file = stageName, stage_sha256 = Hash(await File.ReadAllBytesAsync(stagePath)),
                        frame_width = bundle.Frame.Width, frame_height = bundle.Frame.Height,
                        axis_needs_review = geometry.Geometry.Uncertainty.NeedsReview,
                        axis_reasons = geometry.Geometry.Uncertainty.Reasons, markers = markers.Markers });
                }
                Require(Hash(await File.ReadAllBytesAsync(sourcePath)) == sha, "Source bytes differ after import.");
                if (sourceFailed) failed++;
                results.Add(new { id, image_sha256 = sha, status = sourceFailed ? "failed" : "completed", panels = panelRows });
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                failed++;
                results.Add(new { id, image_sha256 = sha, status = "failed", error_type = error.GetType().Name,
                    error = error.Message, panels = panelRows });
            }
            Console.WriteLine($"processed={results.Count}/{entries.Count}; failed={failed}");
        }
        object reportObject = workflow
            ? new { schema = "graphreader.manual-marker-workflow-capture.v1",
                scope = "train-only-unapproved-candidate-not-acceptance", input_sha256 = inputSha,
                runtime_description_sha256 = descriptionSha, model_binding_sha256 = ModelBindingSha,
                classifier_sha256 = ClassifierFiles[2].Sha, opencv_sha256 = OpenCvSha,
                smoke, optimizer_steps = 0, training_approved = false, classification_requested = true,
                truth_read = false, sealed_payload_reads = 0, rows = results }
            : new { schema = "graphreader.manual-marker-input-capture.v1",
                scope = "train-only-unapproved-candidate-not-acceptance", input_sha256 = inputSha,
                runtime_description_sha256 = descriptionSha, model_binding_sha256 = ModelBindingSha,
                opencv_sha256 = OpenCvSha, optimizer_steps = 0, training_approved = false,
                classification_executed = false, truth_read = false, sealed_payload_reads = 0, rows = results };
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(reportObject, JsonOptions);
        await WriteNew(Path.Combine(output, "report.json"), report);
        Console.WriteLine($"completed={results.Count - failed}; failed={failed}; report_sha256={Hash(report)}");
        return failed == 0 ? 0 : 3;
    }

    private static async Task<(object Row, bool Failed)> CaptureWorkflowPanel(
        ProductionAutomaticDetectionAdapter automatic, ProductionWorkflowDetectionRequest request,
        WorkflowImportedPanel panel, object provenance)
    {
        ProductionCandidateCalibrationObservation? observation = null;
        WorkflowDetectionBatch? batch = null;
        Exception? failure = null;
        automatic.CandidateCalibrationObserver = value =>
        {
            Require(observation is null && value.PanelId == panel.PanelId &&
                string.Equals(value.PanelSha256, panel.Original.Sha256, StringComparison.OrdinalIgnoreCase),
                "Candidate workflow observation identity differs.");
            observation = value;
        };
        try
        {
            batch = await automatic.DetectForCandidateEvaluationAsync(request, CancellationToken.None);
            Require(observation is not null, "Candidate workflow omitted its marker-stage observation.");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            failure = error;
        }
        finally
        {
            automatic.CandidateCalibrationObserver = null;
        }

        object row = new { panel_id = panel.PanelId, panel_sha256 = panel.Original.Sha256, provenance,
            status = failure is null ? "completed" : "failed", error_type = failure?.GetType().Name,
            failure_code = (failure as ProductionWorkflowStageException)?.Failure.Code,
            error = failure?.Message, marker_stage_reached = observation is not null,
            axis_needs_review = observation?.Axis.Uncertainty.NeedsReview,
            calibration_validity = observation?.Calibration.Validity.ToString(),
            centers_before_classification = observation?.MarkerPipeline.CentersBeforeClassification ?? [],
            classified_plot = observation?.MarkerPipeline.ClassifiedPlotMarkers.Select(item => new
            {
                marker = item.Marker, artifact_probability = item.ArtifactProbability,
                shape = item.Shape.ToString(), fill = item.Fill.ToString(),
            }).ToArray() ?? [],
            text_excluded_marker_ids = observation?.MarkerPipeline.TextExcludedMarkerIds ?? [],
            markers = observation?.AcceptedMarkers.Select(item => item.Marker).ToArray() ?? [],
            workflow_points = batch?.Candidates.Select(item => new
            {
                x = item.OriginalPixelX, y = item.OriginalPixelY, item.GraphX, item.GraphY,
            }).ToArray() ?? [] };
        return (row, failure is not null);
    }

    private sealed class ClassifierInputLocks : IDisposable
    {
        private readonly List<FileStream> streams = [];

        public ClassifierInputLocks()
        {
            try
            {
                foreach ((string path, string sha) in ClassifierFiles)
                {
                    var stream = new FileStream(ProjectPath($"{ClassifierStore}/{path}"),
                        FileMode.Open, FileAccess.Read, FileShare.Read);
                    streams.Add(stream);
                    Require(Convert.ToHexStringLower(SHA256.HashData(stream)) == sha, "Classifier input differs.");
                    stream.Position = 0;
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (FileStream stream in streams) stream.Dispose();
        }
    }

    private static FrozenCandidateModel ReadModel(JsonElement item, string task)
    {
        MethodInfo method = typeof(FrozenCandidateBinding).GetMethod("ReadModel", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Shared model validator is unavailable.");
        try { return (FrozenCandidateModel)(method.Invoke(null, [Root, item, task, CancellationToken.None])
            ?? throw new InvalidDataException("Shared model validator returned no result.")); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static void WriteFrame(string path, MarkerImageFrame frame)
    {
        int pixels = checked(frame.Width * frame.Height);
        Require(frame.ChannelCount == 1 && frame.ChannelsFirstPixels.Length == pixels &&
            frame.OcrMask.Width == frame.Width && frame.OcrMask.Height == frame.Height && frame.OcrMask.Values.Length == pixels &&
            frame.ArtifactMask.Width == frame.Width && frame.ArtifactMask.Height == frame.Height && frame.ArtifactMask.Values.Length == pixels,
            "Unexpected marker frame planes.");
        Require(BitConverter.IsLittleEndian, "Frame format requires little-endian values.");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new BinaryWriter(gzip);
        writer.Write(0x31465247); // GRF1, little endian
        writer.Write(frame.Width);
        writer.Write(frame.Height);
        writer.Flush();
        gzip.Write(MemoryMarshal.AsBytes(frame.ChannelsFirstPixels.Span));
        gzip.Write(MemoryMarshal.AsBytes(frame.OcrMask.Values.Span));
        gzip.Write(MemoryMarshal.AsBytes(frame.ArtifactMask.Values.Span));
    }

    private static void WriteAttribution(string path, ProductionCandidateMarkerFrameBundle bundle)
    {
        int pixels = checked(bundle.Frame.Width * bundle.Frame.Height);
        Require(BitConverter.IsLittleEndian && bundle.ArtifactSeedMask.Values.Length == pixels &&
            bundle.ResidualArtifactMask.Values.Length == pixels && bundle.ResidualCategoryBits.Length == pixels,
            "Unexpected artifact attribution planes.");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new BinaryWriter(gzip);
        writer.Write(0x314d5247); // GRM1, little endian
        writer.Write(bundle.Frame.Width);
        writer.Write(bundle.Frame.Height);
        writer.Flush();
        gzip.Write(MemoryMarshal.AsBytes(bundle.ArtifactSeedMask.Values.Span));
        gzip.Write(MemoryMarshal.AsBytes(bundle.ResidualArtifactMask.Values.Span));
        gzip.Write(bundle.ResidualCategoryBits);
    }

    private static async Task WriteStages(string path, ProductionAxisGeometryEvidence geometry,
        ProposalMarkerCandidateDiagnosticResult diagnostic)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await JsonSerializer.SerializeAsync(gzip, new { schema = "graphreader.manual-marker-stage-evidence.v1",
            plot = geometry.Geometry.PlotPolygon.Points, counters = diagnostic.StageCounters,
            emitted_proposals = diagnostic.EmittedProposalCenters,
            above_threshold_decoded = diagnostic.AboveThresholdDecodedPoints,
            pre_nms_candidates = diagnostic.PreNmsCandidates }, JsonOptions);
    }

    private static string NativePath() => Path.Combine(AppContext.BaseDirectory, "reviewed-native", "OpenCvSharpExtern.dll");
    private static string Relative(string path) => Path.GetRelativePath(Root, ProjectPath(path)).Replace('\\', '/');
    private static string OutputPath(string relative)
    {
        Require(relative.StartsWith(OutputRoot + "/", StringComparison.Ordinal), "Output must be in the diagnostic directory.");
        string directory = ProjectPath(OutputRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = ProjectPath(relative);
        Require(path.StartsWith(directory, StringComparison.OrdinalIgnoreCase), "Output escaped the diagnostic directory.");
        return path;
    }
    private static string ProjectPath(string relative)
    {
        string prefix = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(prefix, relative));
        Require(path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "Path escaped the project root.");
        string current = Path.GetPathRoot(path)!;
        foreach (string part in Path.GetRelativePath(current, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse paths are not allowed.");
        }
        return path;
    }
    private static async Task<byte[]> ReadPinned(string path, string expected, int maxBytes)
    {
        Require(expected.Length == 64 && expected.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "Invalid expected hash.");
        string full = ProjectPath(path);
        Require(new FileInfo(full).Length <= maxBytes, "Input exceeds its size limit.");
        byte[] bytes = await File.ReadAllBytesAsync(full);
        Require(bytes.Length <= maxBytes && Hash(bytes) == expected, "Input hash differs.");
        return bytes;
    }
    private static async Task WriteNew(string path, byte[] bytes)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await file.WriteAsync(bytes);
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Exact(JsonElement value, params string[] expected)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Expected an object.");
        string[] names = value.EnumerateObject().Select(property => property.Name).ToArray();
        Require(names.Length == expected.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
            names.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)), "Unexpected object fields.");
    }
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
}
