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
    private const string ModelBinding = "artifacts/goal22-runs/marker-context-center-only-binding-v1.json";
    private const string ModelBindingSha = "51c7b65a076579764da6b58c51ea4ff0c10bcd48232e1ac1b625b74cf100be73";
    private const string OpenCvSha = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31";
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
            if (args.Length == 2 && args[0] == "--describe")
            {
                string path = OutputPath(args[1]);
                byte[] bytes = Describe();
                await WriteNew(path, bytes);
                Console.WriteLine($"runtime_description_sha256={Hash(bytes)}");
                return 0;
            }
            if (args.Length == 6 && args[0] == "--capture")
                return await Capture(args[1], args[2], args[3], args[4], args[5]);
            Console.Error.WriteLine("Usage: --describe <new-json> OR --capture <raster-manifest> <manifest-sha256> <runtime-description> <description-sha256> <new-output-directory>");
            return 2;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"BLOCKED: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static byte[] Describe()
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
        return JsonSerializer.SerializeToUtf8Bytes(new { schema = "graphreader.manual-marker-capture-runtime.v1",
            scope = "train-only-unapproved-candidate-no-optimizer", model_binding_sha256 = ModelBindingSha,
            opencv_sha256 = OpenCvSha, artifact_configuration_sha256 = artifact.Identity.ConfigurationSha256,
            artifact_app_sha256 = artifact.Identity.AssemblySha256, artifact_ocr_sha256 = artifact.OcrAssemblySha256,
            source_files = sourceFiles, runtime_files = runtimeFiles, optimizer_steps = 0, production_approval = false }, JsonOptions);
    }

    private static async Task<int> Capture(string inputPath, string inputSha, string descriptionPath, string descriptionSha, string outputName)
    {
        byte[] description = await ReadPinned(descriptionPath, descriptionSha, 1_000_000);
        Require(description.AsSpan().SequenceEqual(Describe()), "Frozen runtime description differs.");
        byte[] input = await ReadPinned(inputPath, inputSha, 1_000_000);
        using JsonDocument manifest = JsonDocument.Parse(input);
        JsonElement root = manifest.RootElement;
        Exact(root, "schema", "scope", "receipt_sha256", "selection", "training_approved", "rows");
        Require(root.GetProperty("schema").GetString() == "graphreader.manual-marker-raster-input.v1" &&
            root.GetProperty("scope").GetString() == "train-only-raster-no-truth" &&
            root.GetProperty("receipt_sha256").GetString() == ReceiptSha &&
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
            string rasterDirectory = ProjectPath(RasterRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Require(id.Length == 11 && id.StartsWith("overlay-", StringComparison.Ordinal) && id[8..].All(char.IsAsciiDigit) &&
                ids.Add(id) && imageHashes.Add(sha) && relative.StartsWith(RasterRoot, StringComparison.Ordinal) &&
                ProjectPath(relative).StartsWith(rasterDirectory, StringComparison.OrdinalIgnoreCase) && relative.EndsWith(".png", StringComparison.Ordinal),
                "Invalid or repeated train raster identity.");
            entries.Add((id, relative, sha));
        }
        string selection = root.GetProperty("selection").GetString() ?? "";
        Require((selection == "smoke" && entries.Count == 20) || (selection == "full" && entries.Count == 200),
            "Only the fixed 20-image smoke or 200-image input is supported.");
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

        var results = new List<object>();
        int failed = 0;
        foreach ((string id, string path, string sha) in entries)
        {
            var panelRows = new List<object>();
            try
            {
                string sourcePath = ProjectPath(path);
                await using var sourceLock = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                _ = await ReadPinned(path, sha, 64_000_000);
                var store = new ProductionWorkflowPanelStore();
                var axis = new ObservedAxis(new ProductionAxisGeometryAdapter(OpenCvSha, isApproved: false));
                var observedOcr = new ObservedOcr(ocr);
                var automatic = new ProductionAutomaticDetectionAdapter(store, new ProductionRasterFrameDecoder(), axis, observedOcr,
                    new ProductionDetectionMaskComposer(artifact), center, new NoClassification(),
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
                    ProductionCandidateMarkerFrameBundle bundle = await automatic.PrepareMarkerFrameBundleForCandidateEvaluationAsync(request, CancellationToken.None);
                    ProductionAxisGeometryEvidence geometry = axis.ForPanel(panel.PanelId);
                    var plot = new MarkerPolygon(geometry.Geometry.PlotPolygon.Points.Select(point => new MarkerPoint(point.X, point.Y)).ToArray());
                    ProductionMarkerCenterEvidence markers = await ((IProductionCandidateMarkerCenterAdapter)center)
                        .DetectForCandidateEvaluationAsync(request, bundle.Frame, plot, CancellationToken.None);
                    string frameName = $"{id}-panel-{panelIndex++:D2}.bin.gz";
                    string framePath = Path.Combine(output, frameName);
                    WriteFrame(framePath, bundle.Frame);
                    panelRows.Add(new { panel_id = panel.PanelId, panel_sha256 = panel.Original.Sha256, provenance,
                        frame_file = frameName, frame_sha256 = Hash(await File.ReadAllBytesAsync(framePath)),
                        frame_width = bundle.Frame.Width, frame_height = bundle.Frame.Height,
                        axis_needs_review = geometry.Geometry.Uncertainty.NeedsReview,
                        axis_reasons = geometry.Geometry.Uncertainty.Reasons, markers = markers.Markers });
                }
                Require(Hash(await File.ReadAllBytesAsync(sourcePath)) == sha, "Source bytes differ after import.");
                results.Add(new { id, image_sha256 = sha, status = "completed", panels = panelRows });
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                failed++;
                results.Add(new { id, image_sha256 = sha, status = "failed", error_type = error.GetType().Name,
                    error = error.Message, panels = panelRows });
            }
            Console.WriteLine($"processed={results.Count}/{entries.Count}; failed={failed}");
        }
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(new { schema = "graphreader.manual-marker-input-capture.v1",
            scope = "train-only-unapproved-candidate-not-acceptance", input_sha256 = inputSha,
            runtime_description_sha256 = descriptionSha, model_binding_sha256 = ModelBindingSha,
            opencv_sha256 = OpenCvSha, optimizer_steps = 0, training_approved = false,
            classification_executed = false, truth_read = false, sealed_payload_reads = 0, rows = results }, JsonOptions);
        await WriteNew(Path.Combine(output, "report.json"), report);
        Console.WriteLine($"completed={results.Count - failed}; failed={failed}; report_sha256={Hash(report)}");
        return failed == 0 ? 0 : 3;
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
