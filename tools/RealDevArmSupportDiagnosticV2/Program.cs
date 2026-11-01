// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Ocr;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.RealAcceptance.Ocr;

internal static class Program
{
    private const int ExpectedSelectedProjects = 120;
    private const int ExpectedTrainProjects = 104;
    private const int ExpectedDevelopmentProjects = 16;
    private const int ExpectedStudies = 28;
    private const int ProductionArm = 5;
    private const int DiagnosticArm = 4;
    private const int MaximumArm = 64;
    private const int NmsRadius = 6;
    private const int ForegroundMaximumGray = 196;
    private const string ProtocolPath = "tools/RealDevArmSupportDiagnosticV2/protocol.json";
    private static readonly (int X, int Y)[] Directions = [(1, 0), (0, 1), (1, 1), (1, -1)];
    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("ARM_SUPPORT_DIAGNOSTIC_OUTPUT_PATH_REQUIRED");
            return 2;
        }
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
            string root = FindRoot(Environment.CurrentDirectory);
            string output = Path.GetFullPath(Path.Combine(root, args[0]));
            string artifacts = Path.GetFullPath(Path.Combine(root, "artifacts"));
            if (!output.StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OUTPUT_OUTSIDE_PROJECT_ARTIFACTS");
            JsonElement protocol = ValidateProtocol(root);

            var selector = FrozenDevTrainingSelector.Load(Path.Combine(root, "data", "manual data"), CancellationToken.None);
            FrozenRealCorpusSelection selection = FrozenRealCorpusInventory.Load(
                Path.Combine(root, "data", "manual data"), FrozenRealCorpusInventory.RealDev, CancellationToken.None);
            IReadOnlyList<FrozenRealCorpusProject> selected = selector.EnumerateSelectedProjects(CancellationToken.None);
            FrozenRealCorpusProject[] development = SelectDevelopment(selector, selection, selected);

            string nativePath = ResolveOpenCvRuntimePath();
            string nativeSha = HashFile(nativePath);
            var decoder = new ProductionRasterFrameDecoder();
            var axis = new ProductionAxisGeometryAdapter(nativeSha, isApproved: false);
            var total = new Aggregate();
            var imageHashes = new HashSet<string>(StringComparer.Ordinal);
            var failureTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var timer = Stopwatch.StartNew();

            foreach (FrozenRealCorpusProject project in development)
            {
                try
                {
                    EngaugeDigWholeWorkflowTruth truth = ReadSelectedProject(selector, project);
                    byte[] imageBytes = truth.CopyImageBytes();
                    var evidence = new WorkflowImageEvidence("development-image", truth.SourceImageSha256,
                        truth.SourceWidth, truth.SourceHeight, WorkflowImageVariant.Original);
                    var imported = new WorkflowImportedPanel(Guid.NewGuid(), Guid.NewGuid(), "Development", evidence);
                    var panel = new WorkflowPreparedPanel(imported, evidence, enhanced: null);
                    var request = new ProductionWorkflowDetectionRequest(panel, evidence, WorkflowImageVariant.Original,
                        Guid.NewGuid(), Guid.NewGuid(), imageBytes);
                    ProductionDecodedRaster raster = decoder.Decode(request, CancellationToken.None);
                    if (raster.Width != truth.SourceWidth || raster.Height != truth.SourceHeight ||
                        !string.Equals(raster.InputSha256, truth.SourceImageSha256, StringComparison.Ordinal))
                        throw new InvalidDataException("DEVELOPMENT_RASTER_IDENTITY_MISMATCH");
                    ProductionAxisGeometryEvidence detectedAxis = await axis.DetectForCandidateEvaluationAsync(
                        request, CancellationToken.None).ConfigureAwait(false);
                    byte[] gray = raster.CreateOcrImage().Pixels.ToArray();
                    ScanResult scan4 = Scan(raster.Width, raster.Height, gray, detectedAxis.Geometry.PlotPolygon, DiagnosticArm);
                    ScanResult scan5 = Scan(raster.Width, raster.Height, gray, detectedAxis.Geometry.PlotPolygon, ProductionArm);

                    Point[] truthPoints = truth.TruthCase.Points.Select(point => new Point(point.SourcePixelX, point.SourcePixelY)).ToArray();
                    total.AddSource(truthPoints.Length, checked(raster.Width * raster.Height));
                    total.AddScan(scan4, scan5, truthPoints);
                    imageHashes.Add(truth.SourceImageSha256);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
                {
                    failureTypes.TryGetValue(exception.GetType().Name, out int count);
                    failureTypes[exception.GetType().Name] = count + 1;
                }
            }
            timer.Stop();

            string[] nativeModules = ReadLoadedOpenCvModulePaths();
            string? loadedNativeSha = nativeModules.Length == 1 && File.Exists(nativeModules[0])
                ? HashFile(nativeModules[0]) : null;
            if (loadedNativeSha is not null && loadedNativeSha != nativeSha)
                throw new InvalidDataException("OPEN_CV_NATIVE_HASH_MISMATCH");
            if (total.SuccessfulImages + failureTypes.Values.Sum() != ExpectedDevelopmentProjects)
                throw new InvalidDataException("DEVELOPMENT_PROJECT_ACCOUNTING_MISMATCH");

            object report = new
            {
                schema = "graphreader.real-dev-arm-support-diagnostic-result.v1",
                status = failureTypes.Count == 0 ? "complete" : "partial",
                scope = new
                {
                    cohort = "locked-real-development-only",
                    selected_projects = ExpectedDevelopmentProjects,
                    train_project_metadata_only = ExpectedTrainProjects,
                    sealed_count_metadata_only = selection.RealSealedCount,
                    assignment_sha256 = selection.AssignmentSha256,
                    train_payloads_opened = false,
                    sealed_payloads_opened = false,
                    development_partition_previously_exposed = true,
                    training = false,
                    gpu_inference = false,
                    ocr_model_payloads_opened = false,
                    production_approval = false,
                    sealed_acceptance = false,
                    case_level_output = false,
                    private_paths_or_pixels_serialized = false,
                },
                method = new
                {
                    raster = "ProductionRasterFrameDecoder, original Gray8",
                    plot_bounds = "ProductionAxisGeometryAdapter detected polygon, CPU candidate path",
                    scan = "source-bound four opposing rays, gray<=196, arm cap 64, two extended pairs with total span>13, provider NMS radius 6",
                    paired_arm_lengths = new { production = ProductionArm, diagnostic = DiagnosticArm },
                    ocr_and_seed_masks = "omitted; counts are raster geometry candidates, not production mask output",
                    engauge_usage = "point centers used only for aggregate proximity within 2px; proximity does not establish marker identity or a negative label",
                },
                aggregate = total.ToObject(),
                unique_source_images = imageHashes.Count,
                failures_by_exception_type = failureTypes,
                elapsed_ms = Math.Round(timer.Elapsed.TotalMilliseconds, 3),
                identity = new
                {
                    protocol_sha256 = HashFile(Path.Combine(root, ProtocolPath.Replace('/', Path.DirectorySeparatorChar))),
                    program_sha256 = HashFile(Path.Combine(root, "tools", "RealDevArmSupportDiagnosticV2", "Program.cs")),
                    project_sha256 = HashFile(Path.Combine(root, "tools", "RealDevArmSupportDiagnosticV2", "RealDevArmSupportDiagnosticV2.csproj")),
                    inventory_source_sha256 = HashFile(Path.Combine(root, "tools", "GraphReader.RealAcceptance.Ocr", "FrozenRealCorpusInventory.cs")),
                    selector_source_sha256 = HashFile(Path.Combine(root, "tools", "GraphReader.RealAcceptance.Ocr", "FrozenDevTrainingSelector.cs")),
                    materializer_source_sha256 = HashFile(Path.Combine(root, "tools", "GraphReader.RealAcceptance.Ocr", "EngaugeDevSourceMaterializer.cs")),
                    truth_adapter_source_sha256 = HashFile(Path.Combine(root, "tools", "GraphReader.RealAcceptance.Ocr", "EngaugeDigWholeWorkflowTruthAdapter.cs")),
                    provider_source_sha256 = HashFile(Path.Combine(root, "src", "GraphReader.App", "Integration", "Workflow", "RasterResidualArtifactMaskProvider.cs")),
                    raster_decoder_source_sha256 = HashFile(Path.Combine(root, "src", "GraphReader.App", "Integration", "Workflow", "ProductionRasterFrameDecoder.cs")),
                    axis_adapter_source_sha256 = HashFile(Path.Combine(root, "src", "GraphReader.App", "Integration", "Workflow", "ProductionAxisGeometryAdapter.cs")),
                    app_assembly_sha256 = HashFile(typeof(RasterResidualArtifactMaskProvider).Assembly.Location),
                    axis_assembly_sha256 = HashFile(typeof(ProductionAxisGeometryAdapter).Assembly.Location),
                    runner_assembly_sha256 = HashFile(typeof(Program).Assembly.Location),
                    loaded_opencv_module_count = nativeModules.Length,
                    opencv_native_sha256 = nativeSha,
                    loaded_opencv_native_sha256 = loadedNativeSha,
                },
                limitation = "Neither Engauge points nor this raster scan provide complete marker masks or labeled negative crossings. Spatial proximity is not proof that a short-arm proposal is a marker or artifact. Real-graph need for four-pixel support remains unverified.",
            };
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, ReportOptions));
            Console.WriteLine("REAL_DEV_ARM_SUPPORT_DIAGNOSTIC_WRITTEN");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            Console.Error.WriteLine("REAL_DEV_ARM_SUPPORT_DIAGNOSTIC_FAILED " + exception.GetType().Name);
            return 1;
        }
    }

    private static JsonElement ValidateProtocol(string root)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, ProtocolPath.Replace('/', Path.DirectorySeparatorChar))));
        JsonElement value = document.RootElement;
        JsonElement scope = value.GetProperty("scope");
        if (value.GetProperty("status").GetString() != "preregistered-before-source-read" ||
            scope.GetProperty("expected_projects").GetInt32() != ExpectedDevelopmentProjects ||
            scope.GetProperty("assignment_sha256").GetString() != FrozenRealCorpusInventory.FrozenAssignmentSha256 ||
            scope.GetProperty("train_payload_reads").GetBoolean() ||
            scope.GetProperty("sealed_payload_reads").GetBoolean() ||
            scope.GetProperty("training").GetBoolean() ||
            scope.GetProperty("gpu_inference").GetBoolean())
            throw new InvalidDataException("ARM_SUPPORT_PROTOCOL_SCOPE_INVALID");
        return value;
    }

    private static FrozenRealCorpusProject[] SelectDevelopment(
        FrozenDevTrainingSelector selector,
        FrozenRealCorpusSelection selection,
        IReadOnlyList<FrozenRealCorpusProject> projects)
    {
        if (selection.SelectedSplit != FrozenRealCorpusInventory.RealDev ||
            selection.ProjectCount != FrozenRealCorpusInventory.ExpectedProjectCount ||
            selection.RealDevCount != ExpectedSelectedProjects ||
            selection.RealSealedCount != FrozenRealCorpusInventory.ExpectedRealSealedCount ||
            selection.AssignmentSha256 != FrozenRealCorpusInventory.FrozenAssignmentSha256 ||
            projects.Count != ExpectedSelectedProjects ||
            projects.Any(static project => project.Split != FrozenRealCorpusInventory.RealDev))
            throw new InvalidDataException("LOCKED_REAL_DEV_ASSIGNMENT_MISMATCH");

        var byStudy = projects.GroupBy(static project => project.AnonymizedStudyId, StringComparer.Ordinal).ToArray();
        int train = 0;
        var development = new List<FrozenRealCorpusProject>();
        foreach (IGrouping<string, FrozenRealCorpusProject> study in byStudy)
        {
            EngaugeDevStudyPartition partition = EngaugeDevSourceMaterializer.PartitionForStudy(study.Key);
            if (partition == EngaugeDevStudyPartition.Development) development.AddRange(study);
            else if (partition == EngaugeDevStudyPartition.Train) train += study.Count();
            else throw new InvalidDataException("LOCKED_REAL_DEV_PARTITION_INVALID");
        }
        if (byStudy.Length != ExpectedStudies || train != ExpectedTrainProjects || development.Count != ExpectedDevelopmentProjects)
            throw new InvalidDataException("LOCKED_REAL_DEV_PARTITION_COUNTS_MISMATCH");
        _ = selector.EnumerateSelectedProjects(CancellationToken.None);
        return development.OrderBy(static project => project.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
    }

    private static EngaugeDigWholeWorkflowTruth ReadSelectedProject(FrozenDevTrainingSelector selector, FrozenRealCorpusProject project)
    {
        if (project.Split != FrozenRealCorpusInventory.RealDev ||
            EngaugeDevSourceMaterializer.PartitionForStudy(project.AnonymizedStudyId) != EngaugeDevStudyPartition.Development)
            throw new InvalidDataException("NON_DEVELOPMENT_PAYLOAD_READ_REJECTED");
        FrozenRealCorpusProject current = selector.RequireProject(project.GetProjectPath(), project.AnonymizedCaseId,
            project.AnonymizedStudyId, CancellationToken.None);
        using var stream = new FileStream(current.GetProjectPath(), FileMode.Open, FileAccess.Read, FileShare.Read);
        EngaugeDigWholeWorkflowTruth truth = EngaugeDigWholeWorkflowTruthAdapter.Read(stream,
            current.AnonymizedCaseId, () =>
            {
                _ = selector.RequireProject(current.GetProjectPath(), current.AnonymizedCaseId,
                    current.AnonymizedStudyId, CancellationToken.None);
                EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, current.GetProjectPath());
            }, CancellationToken.None);
        EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, current.GetProjectPath());
        stream.Position = 0;
        if (HashStream(stream) != truth.ProjectSha256) throw new InvalidDataException("DEVELOPMENT_SOURCE_CHANGED_DURING_READ");
        return truth;
    }

    private static ScanResult Scan(int width, int height, byte[] gray, PlotPolygon polygon, int minimumArm)
    {
        OcrRectangle bounds = new(polygon.Points.Min(static p => p.X), polygon.Points.Min(static p => p.Y),
            polygon.Points.Max(static p => p.X) - polygon.Points.Min(static p => p.X),
            polygon.Points.Max(static p => p.Y) - polygon.Points.Min(static p => p.Y));
        int left = Math.Clamp((int)Math.Floor(bounds.Left), 0, width - 1);
        int right = Math.Clamp((int)Math.Ceiling(bounds.Right), 0, width - 1);
        int top = Math.Clamp((int)Math.Floor(bounds.Top), 0, height - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom), 0, height - 1);
        var candidates = new List<Candidate>();
        int raw = 0;
        for (int y = Math.Max(minimumArm, top); y <= Math.Min(height - minimumArm - 1, bottom); y++)
        for (int x = Math.Max(minimumArm, left); x <= Math.Min(width - minimumArm - 1, right); x++)
        {
            int centerIndex = y * width + x;
            if (gray[centerIndex] > ForegroundMaximumGray || !Contains(polygon, x, y)) continue;
            int pairCount = 0, extendedPairCount = 0, strength = 0;
            foreach ((int dx, int dy) in Directions)
            {
                int forward = Ray(gray, width, height, x, y, dx, dy);
                int backward = Ray(gray, width, height, x, y, -dx, -dy);
                if (forward >= minimumArm && backward >= minimumArm)
                {
                    pairCount++;
                    strength += Math.Min(forward, MaximumArm) + Math.Min(backward, MaximumArm);
                    if (forward + backward + 1 > 13) extendedPairCount++;
                }
            }
            if (pairCount >= 2 && extendedPairCount >= 2)
            {
                raw++;
                candidates.Add(new Candidate(x, y, strength));
            }
        }
        var accepted = new List<Candidate>();
        foreach (Candidate candidate in candidates.OrderByDescending(static c => c.Strength))
        {
            if (accepted.Any(existing => DistanceSquared(existing.X, existing.Y, candidate.X, candidate.Y) <= NmsRadius * NmsRadius)) continue;
            accepted.Add(candidate);
        }
        return new ScanResult(raw, accepted);
    }

    private static int Ray(byte[] gray, int width, int height, int ox, int oy, int dx, int dy)
    {
        int n = 0;
        for (int x = ox + dx, y = oy + dy; n < MaximumArm && x >= 0 && x < width && y >= 0 && y < height; x += dx, y += dy)
        {
            if (gray[y * width + x] > ForegroundMaximumGray) break;
            n++;
        }
        return n;
    }

    private static bool Contains(PlotPolygon polygon, int x, int y)
    {
        IReadOnlyList<PixelPoint> vertices = polygon.Points;
        bool inside = false;
        for (int i = 0, j = vertices.Count - 1; i < vertices.Count; j = i++)
        {
            PixelPoint a = vertices[i], b = vertices[j];
            if ((a.Y > y) != (b.Y > y) && x < ((b.X - a.X) * (y - a.Y) / (b.Y - a.Y)) + a.X) inside = !inside;
        }
        return inside;
    }

    private static FrozenRealCorpusSelection LoadSelection(string root) => FrozenRealCorpusInventory.Load(
        Path.Combine(root, "data", "manual data"), FrozenRealCorpusInventory.RealDev, CancellationToken.None);

    private static string FindRoot(string start)
    {
        for (DirectoryInfo? d = new(Path.GetFullPath(start)); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(d.FullName, "tools", "GraphReader.RealAcceptance.Ocr"))) return d.FullName;
        throw new DirectoryNotFoundException("REPOSITORY_ROOT_NOT_FOUND");
    }

    private static string ResolveOpenCvRuntimePath()
    {
        string[] candidates = [Path.Combine(AppContext.BaseDirectory, "OpenCvSharpExtern.dll"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "OpenCvSharpExtern.dll")];
        string[] existing = candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (existing.Length != 1) throw new FileNotFoundException("OPEN_CV_NATIVE_RUNTIME_NOT_UNIQUE");
        return existing[0];
    }

    private static string[] ReadLoadedOpenCvModulePaths() => Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
        .Where(static module => string.Equals(module.ModuleName, "OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase))
        .Select(static module => module.FileName ?? string.Empty).Where(static path => path.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string HashStream(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));
    private static string HashFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static int DistanceSquared(int x1, int y1, int x2, int y2) => ((x1 - x2) * (x1 - x2)) + ((y1 - y2) * (y1 - y2));

    private sealed record Point(double X, double Y);
    private sealed record Candidate(int X, int Y, int Strength);
    private sealed record ScanResult(int RawCandidates, IReadOnlyList<Candidate> Accepted);

    private sealed class Aggregate
    {
        public int SuccessfulImages { get; private set; }
        public long EngaugePointCenters { get; private set; }
        public long RasterPixels { get; private set; }
        public long Arm4RawCandidates { get; private set; }
        public long Arm5RawCandidates { get; private set; }
        public long Arm4NmsCandidates { get; private set; }
        public long Arm5NmsCandidates { get; private set; }
        public long Arm4NmsCandidatesWhoseMaskCoversEngaugePoint { get; private set; }
        public long Arm5NmsCandidatesWhoseMaskCoversEngaugePoint { get; private set; }
        public long EngaugePointsCoveredByArm4NmsMasks { get; private set; }
        public long EngaugePointsCoveredByArm5NmsMasks { get; private set; }
        public long EngaugePointsCoveredOnlyByArm4Masks { get; private set; }
        public long EngaugePointsCoveredOnlyByArm5Masks { get; private set; }
        public long Arm4NovelCandidatesBeyondArm5NmsRadius { get; private set; }
        public long Arm4NovelCandidatesNearEngaugePoint { get; private set; }
        public long Arm4NovelCandidatesNotNearEngaugePoint { get; private set; }

        public void AddSource(int points, int pixels) { SuccessfulImages++; EngaugePointCenters += points; RasterPixels += pixels; }
        public void AddScan(ScanResult arm4, ScanResult arm5, Point[] points)
        {
            Arm4RawCandidates += arm4.RawCandidates;
            Arm5RawCandidates += arm5.RawCandidates;
            Arm4NmsCandidates += arm4.Accepted.Count;
            Arm5NmsCandidates += arm5.Accepted.Count;
            Arm4NmsCandidatesWhoseMaskCoversEngaugePoint += arm4.Accepted.Count(candidate => NearAnyPoint(candidate, points));
            Arm5NmsCandidatesWhoseMaskCoversEngaugePoint += arm5.Accepted.Count(candidate => NearAnyPoint(candidate, points));
            bool[] pointCovered4 = points.Select(point => MaskCoversAny(point, arm4.Accepted)).ToArray();
            bool[] pointCovered5 = points.Select(point => MaskCoversAny(point, arm5.Accepted)).ToArray();
            EngaugePointsCoveredByArm4NmsMasks += pointCovered4.Count(static value => value);
            EngaugePointsCoveredByArm5NmsMasks += pointCovered5.Count(static value => value);
            EngaugePointsCoveredOnlyByArm4Masks += pointCovered4.Zip(pointCovered5).Count(static pair => pair.First && !pair.Second);
            EngaugePointsCoveredOnlyByArm5Masks += pointCovered4.Zip(pointCovered5).Count(static pair => !pair.First && pair.Second);
            Candidate[] novel = arm4.Accepted.Where(candidate => !arm5.Accepted.Any(baseline =>
                DistanceSquared(candidate.X, candidate.Y, baseline.X, baseline.Y) <= NmsRadius * NmsRadius)).ToArray();
            Arm4NovelCandidatesBeyondArm5NmsRadius += novel.Length;
            Arm4NovelCandidatesNearEngaugePoint += novel.Count(candidate => NearAnyPoint(candidate, points));
            Arm4NovelCandidatesNotNearEngaugePoint += novel.Count(candidate => !NearAnyPoint(candidate, points));
        }
        public object ToObject() => new
        {
            successful_images = SuccessfulImages,
            engauge_point_centers = EngaugePointCenters,
            raster_pixels = RasterPixels,
            arm4_raw_candidates = Arm4RawCandidates,
            arm5_raw_candidates = Arm5RawCandidates,
            arm4_nms_candidates = Arm4NmsCandidates,
            arm5_nms_candidates = Arm5NmsCandidates,
            arm4_nms_candidates_whose_5x5_mask_covers_engauge_point = Arm4NmsCandidatesWhoseMaskCoversEngaugePoint,
            arm5_nms_candidates_whose_5x5_mask_covers_engauge_point = Arm5NmsCandidatesWhoseMaskCoversEngaugePoint,
            engauge_points_covered_by_arm4_nms_masks = EngaugePointsCoveredByArm4NmsMasks,
            engauge_points_covered_by_arm5_nms_masks = EngaugePointsCoveredByArm5NmsMasks,
            engauge_points_covered_only_by_arm4_masks = EngaugePointsCoveredOnlyByArm4Masks,
            engauge_points_covered_only_by_arm5_masks = EngaugePointsCoveredOnlyByArm5Masks,
            arm4_novel_candidates_beyond_arm5_nms_radius = Arm4NovelCandidatesBeyondArm5NmsRadius,
            arm4_novel_candidates_near_engauge_point = Arm4NovelCandidatesNearEngaugePoint,
            arm4_novel_candidates_not_near_engauge_point = Arm4NovelCandidatesNotNearEngaugePoint,
        };
        private static bool NearAnyPoint(Candidate c, Point[] points) => points.Any(p => CoversPoint(c, p));
        private static bool MaskCoversAny(Point p, IReadOnlyList<Candidate> candidates) => candidates.Any(c => CoversPoint(c, p));
        private static bool CoversPoint(Candidate c, Point p) =>
            Math.Abs(c.X - (int)Math.Round(p.X, MidpointRounding.AwayFromZero)) <= 2 &&
            Math.Abs(c.Y - (int)Math.Round(p.Y, MidpointRounding.AwayFromZero)) <= 2;
    }
}
