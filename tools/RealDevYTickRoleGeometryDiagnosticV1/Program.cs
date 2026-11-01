// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Ocr;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.RealDevYTickRoleGeometryDiagnosticV1;

internal static class Program
{
    private const int ExpectedSelectedProjects = 120;
    private const int ExpectedTrainProjects = 104;
    private const int ExpectedDevelopmentProjects = 16;
    private const int ExpectedStudies = 28;
    private const string PartitionRule = "sha256:graphreader-engauge-real-dev-study-v1:first-byte-lt-204";
    private const string NearAxisDefinition = "candidate box right edge left of oracle plot left boundary, gap <= threshold fraction of oracle plot width";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
        if (args.Length != 3 || args[0] != "--candidate-mode" || args[1] != "v8-cpu" ||
            args[2] != "--explicit-opt-in" || IsContinuousIntegration())
        {
            Console.Error.WriteLine("REAL_DEV_ROLE_GEOMETRY_EXPLICIT_CANDIDATE_MODE_REQUIRED");
            return 2;
        }

        string root;
        string output;
        try
        {
            root = FindRepositoryRoot(Environment.CurrentDirectory);
            output = CreateRunDirectory(root);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("REAL_DEV_ROLE_GEOMETRY_PREFLIGHT_FAILED " + exception.GetType().Name);
            return 2;
        }

        try
        {
            string corpusRoot = Path.Combine(root, "data", "manual data");
            FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpusRoot, CancellationToken.None);
            FrozenRealCorpusSelection inventory = FrozenRealCorpusInventory.Load(
                corpusRoot, FrozenRealCorpusInventory.RealDev, CancellationToken.None);
            IReadOnlyList<FrozenRealCorpusProject> selected = selector.EnumerateSelectedProjects(CancellationToken.None);
            DevelopmentSelection development = ValidateAndSelect(selector, inventory, selected);
            OcrV8ProductionPayloadSet payloads = LoadPayloads(root);
            await using CandidateRuntime candidate = CreateCandidate(payloads);

            var counts = new RoleGeometryAccumulator();
            using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sourceHash.AppendData(Encoding.UTF8.GetBytes("graphreader.real-dev-ytick-role-geometry-v1\n"));
            sourceHash.AppendData(Encoding.UTF8.GetBytes(development.AssignmentSha256 + "\n"));
            foreach (FrozenRealCorpusProject project in development.Projects)
            {
                EngaugeDigWholeWorkflowTruth truth;
                try
                {
                    truth = ReadDevelopmentProject(selector, project);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
                {
                    counts.RecordSourceFailure(exception.GetType().Name);
                    continue;
                }

                sourceHash.AppendData(Encoding.UTF8.GetBytes(
                    project.AnonymizedCaseId + "\n" + truth.ProjectSha256 + "\n" + truth.SourceImageSha256 + "\n"));
                try
                {
                    OcrImage image = DecodeProductionImage(truth);
                    OcrRectangle oracleBounds = PlotBounds(truth.Anchors, image.Width, image.Height);
                    OcrResult result = await candidate.Pipeline.RecognizeAsync(new OcrRequest(
                        "real-dev-role-geometry",
                        "real-dev-role-geometry-panel",
                        truth.SourceImageSha256,
                        image,
                        oracleBounds), CancellationToken.None).ConfigureAwait(false);
                    counts.Record(result, oracleBounds);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
                {
                    counts.RecordCandidateFailure(exception.GetType().Name);
                }
            }

            _ = selector.EnumerateSelectedProjects(CancellationToken.None);
            ExecutionReceipt receipt = CaptureExecutionReceipt(root);
            var report = BuildAggregateReport(
                candidate.Pipeline.CompositionId,
                candidate.Pipeline.ConfigurationFingerprint,
                development,
                Convert.ToHexStringLower(sourceHash.GetHashAndReset()),
                counts.Finish(ExpectedDevelopmentProjects),
                receipt);
            File.WriteAllText(Path.Combine(output, "aggregate-report.json"),
                JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
            Console.WriteLine("REAL_DEV_ROLE_GEOMETRY_CANDIDATE_REPORT_WRITTEN");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            File.WriteAllText(Path.Combine(output, "aggregate-failure.json"), JsonSerializer.Serialize(
                new FailureReport("failed", exception.GetType().Name, true, false, false), JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false));
            Console.Error.WriteLine("REAL_DEV_ROLE_GEOMETRY_FAILED " + exception.GetType().Name);
            return 1;
        }
    }

    private static AggregateReport BuildAggregateReport(
        string compositionId,
        string configurationFingerprint,
        DevelopmentSelection development,
        string developmentContentSha256,
        Evaluation evaluation,
        ExecutionReceipt receipt) => new(
            Schema: "graphreader.real-dev-ytick-role-geometry-v1",
            EvaluationMode: "development_diagnostic",
            CandidateMode: "v8-cpu",
            CompositionId: compositionId,
            ConfigurationFingerprint: configurationFingerprint,
            PayloadSha256: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detector"] = OcrV8ProductionCompositionFactory.DetectorSha256,
                ["official_recognizer"] = OcrV8ProductionCompositionFactory.OfficialRecognizerSha256,
                ["numeric_recognizer"] = OcrV8ProductionCompositionFactory.NumericRecognizerSha256,
                ["ambiguity_recognizer"] = OcrV8ProductionCompositionFactory.AmbiguityRecognizerSha256,
                ["official_alphabet"] = OcrV8ProductionCompositionFactory.OfficialAlphabetSha256,
            },
            PayloadVersions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detector"] = "0.0.21-p2",
                ["official_recognizer"] = "0.0.21-converted",
                ["numeric_recognizer"] = "0.0.21-p1",
                ["ambiguity_recognizer"] = "0.0.21-p2",
            },
            InferenceProvider: InferenceProvider.Cpu.ToString(),
            SelectedProjectCount: development.SelectedProjectCount,
            StudyCount: development.StudyCount,
            TrainProjectCount: development.TrainProjectCount,
            DevelopmentProjectCount: development.Projects.Count,
            AssignmentSha256: development.AssignmentSha256,
            SelectedInventorySha256: development.SelectedInventorySha256,
            DevelopmentContentSha256: developmentContentSha256,
            PartitionRule: PartitionRule,
            FrozenAssignmentVerified: true,
            PreviouslyExposedDevelopmentData: true,
            SealedPayloadsRead: false,
            CaseLevelOutput: false,
            ProductionApproved: false,
            SealedAcceptance: false,
            GeometryOracle: "production raster decoder; full unmasked raster passed to V8 detector; Engauge anchors supply oracle plot bounds; no axis detection or production axis mask",
            NearAxisDefinition: NearAxisDefinition,
            ProjectCountBinDefinition: "Count successful recognized dev projects with zero, one, or two-or-more eligible numeric YTick regions; eligibility excludes rejected regions and requires production GraphNumericParser success and finite confidence; geometry bins further require Y-center within oracle plot bounds, and near-left bins also require the region's right edge to be left of the oracle axis and within 10 percent of oracle plot width",
            NearAxis2_5PercentPlotWidth: 0.025,
            NearAxis5PercentPlotWidth: 0.05,
            NearAxis10PercentPlotWidth: 0.10,
            OnlyAggregateOutput: true,
            NoGroundTruthTickTextOrBoxes: true,
            Aggregate: evaluation,
            SourceSha256: receipt.SourceSha256,
            ProjectSha256: receipt.ProjectSha256,
            LoadedAssemblySha256: receipt.LoadedAssemblySha256);

    private static DevelopmentSelection ValidateAndSelect(
        FrozenDevTrainingSelector selector,
        FrozenRealCorpusSelection inventory,
        IReadOnlyList<FrozenRealCorpusProject> selected)
    {
        if (inventory.SelectedSplit != FrozenRealCorpusInventory.RealDev ||
            inventory.ProjectCount != FrozenRealCorpusInventory.ExpectedProjectCount ||
            inventory.RealDevCount != ExpectedSelectedProjects ||
            inventory.RealSealedCount != FrozenRealCorpusInventory.ExpectedRealSealedCount ||
            inventory.AssignmentSha256 != FrozenRealCorpusInventory.FrozenAssignmentSha256 ||
            selected.Count != ExpectedSelectedProjects ||
            selected.Any(static item => item.Split != FrozenRealCorpusInventory.RealDev))
            throw new InvalidDataException("LOCKED_REAL_DEV_ASSIGNMENT_MISMATCH");

        var groups = selected.GroupBy(static item => item.AnonymizedStudyId, StringComparer.Ordinal).ToArray();
        int train = 0;
        int dev = 0;
        var devProjects = new List<FrozenRealCorpusProject>();
        foreach (IGrouping<string, FrozenRealCorpusProject> group in groups)
        {
            switch (EngaugeDevSourceMaterializer.PartitionForStudy(group.Key))
            {
                case EngaugeDevStudyPartition.Train:
                    train += group.Count();
                    break;
                case EngaugeDevStudyPartition.Development:
                    dev += group.Count();
                    devProjects.AddRange(group);
                    break;
                default:
                    throw new InvalidDataException("LOCKED_REAL_DEV_PARTITION_INVALID");
            }
        }
        if (groups.Length != ExpectedStudies || train != ExpectedTrainProjects ||
            dev != ExpectedDevelopmentProjects || devProjects.Count != ExpectedDevelopmentProjects)
            throw new InvalidDataException("LOCKED_REAL_DEV_PARTITION_COUNTS_MISMATCH");

        _ = selector.EnumerateSelectedProjects(CancellationToken.None);
        return new DevelopmentSelection(
            selected.Count,
            groups.Length,
            train,
            inventory.AssignmentSha256,
            inventory.SelectedInventorySha256,
            Array.AsReadOnly(devProjects.OrderBy(static item => item.AnonymizedCaseId, StringComparer.Ordinal).ToArray()));
    }

    private static EngaugeDigWholeWorkflowTruth ReadDevelopmentProject(
        FrozenDevTrainingSelector selector,
        FrozenRealCorpusProject project)
    {
        if (project.Split != FrozenRealCorpusInventory.RealDev ||
            EngaugeDevSourceMaterializer.PartitionForStudy(project.AnonymizedStudyId) != EngaugeDevStudyPartition.Development)
            throw new InvalidDataException("NON_DEVELOPMENT_PAYLOAD_READ_REJECTED");
        FrozenRealCorpusProject current = selector.RequireProject(
            project.GetProjectPath(), project.AnonymizedCaseId, project.AnonymizedStudyId, CancellationToken.None);
        using var stream = new FileStream(current.GetProjectPath(), FileMode.Open, FileAccess.Read, FileShare.Read);
        EngaugeDigWholeWorkflowTruth truth = EngaugeDigWholeWorkflowTruthAdapter.Read(
            stream,
            current.AnonymizedCaseId,
            () =>
            {
                _ = selector.RequireProject(current.GetProjectPath(), current.AnonymizedCaseId,
                    current.AnonymizedStudyId, CancellationToken.None);
                EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, current.GetProjectPath());
            },
            CancellationToken.None);
        EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, current.GetProjectPath());
        stream.Position = 0;
        if (!string.Equals(HashStream(stream), truth.ProjectSha256, StringComparison.Ordinal))
            throw new InvalidDataException("DEVELOPMENT_SOURCE_CHANGED_DURING_READ");
        return truth;
    }

    private static OcrV8ProductionPayloadSet LoadPayloads(string root)
    {
        ModelIdentity Model(string id, string version, string sha256, string path) =>
            new(id, version, sha256, Path.GetFullPath(Path.Combine(root, path)));
        string yaml = Path.Combine(root,
            "ml/ocr/official_bakeoff/runs/extracted/en_PP-OCRv5_mobile_rec_infer/inference.yml");
        var payloads = new OcrV8ProductionPayloadSet(
            Model("graph-text-spaced-component-recall-v10-p2", "0.0.21-p2",
                OcrV8ProductionCompositionFactory.DetectorSha256,
                "ml/ocr/component_spaced_recall_detector_v10/artifacts/P2-run/graph-text-spaced-component-recall-v10-p2.onnx"),
            Model("en_PP-OCRv5_mobile_rec", "0.0.21-converted",
                OcrV8ProductionCompositionFactory.OfficialRecognizerSha256,
                "ml/ocr/official_bakeoff/runs/conversion/en_PP-OCRv5_mobile_rec.onnx"),
            Model("graph-numeric-component-ensemble-v5", "0.0.21-p1",
                OcrV8ProductionCompositionFactory.NumericRecognizerSha256,
                "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx"),
            Model("graph-ambiguity-source-group-v3-p2", "0.0.21-p2",
                OcrV8ProductionCompositionFactory.AmbiguityRecognizerSha256,
                "ml/ocr/ambiguity_source_group_classifier_v3/artifacts/P2-run/graph-ambiguity-source-group-v3-p2.onnx"),
            ReadAlphabet(yaml));
        OcrV8ProductionCompositionFactory.ValidatePayloads(payloads);
        return payloads;
    }

    private static string ReadAlphabet(string yaml)
    {
        string[] lines = File.ReadAllLines(yaml, Encoding.UTF8);
        int start = Array.FindIndex(lines, static line => line.Trim() == "character_dict:");
        if (start < 0) throw new InvalidDataException("OCR_ALPHABET_SECTION_MISSING");
        var result = new StringBuilder();
        for (int index = start + 1; index < lines.Length && lines[index].StartsWith("  - ", StringComparison.Ordinal); index++)
        {
            string value = lines[index][4..].Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
                value = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            result.Append(value);
        }
        if (!result.ToString().Contains(' ', StringComparison.Ordinal)) result.Append(' ');
        return result.ToString();
    }

    private static CandidateRuntime CreateCandidate(OcrV8ProductionPayloadSet payloads)
    {
        var registry = new OnnxSessionRegistry(
            new OrtExecutionProviderDiscovery(),
            new WindowsExecutionProviderPolicy(),
            new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance),
            CpuThreadConfiguration.Create(1));
        var runtime = new InferenceRuntime(
            registry,
            new BoundedInferenceScheduler(2, 1),
            new NoPersistenceStageCache(),
            allowedProviders: [InferenceProvider.Cpu]);
        try
        {
            OcrV8ProductionCompositionPipeline pipeline = OcrV8ProductionCompositionFactory.Create(
                runtime, payloads, [InferenceProvider.Cpu], bypassCache: true);
            return new CandidateRuntime(runtime, pipeline);
        }
        catch
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static OcrImage DecodeProductionImage(EngaugeDigWholeWorkflowTruth truth)
    {
        var image = new WorkflowImageEvidence(
            "real-development-image", truth.SourceImageSha256, truth.SourceWidth, truth.SourceHeight,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(Guid.NewGuid(), Guid.NewGuid(), "Development", image);
        var panel = new WorkflowPreparedPanel(imported, image, enhanced: null);
        var request = new ProductionWorkflowDetectionRequest(
            panel, image, WorkflowImageVariant.Original, Guid.NewGuid(), Guid.NewGuid(), truth.CopyImageBytes());
        ProductionDecodedRaster decoded = new ProductionRasterFrameDecoder().Decode(request, CancellationToken.None);
        if (decoded.InputSha256 != truth.SourceImageSha256 || decoded.Width != truth.SourceWidth ||
            decoded.Height != truth.SourceHeight)
            throw new InvalidDataException("DEVELOPMENT_IMAGE_DECODE_INVALID");
        return decoded.CreateOcrImage();
    }

    private static OcrRectangle PlotBounds(IReadOnlyList<EngaugeDigAxisAnchor> anchors, int width, int height)
    {
        double left = Math.Clamp(anchors.Min(static item => item.ScreenX), 0, width - 1);
        double top = Math.Clamp(anchors.Min(static item => item.ScreenY), 0, height - 1);
        double right = Math.Clamp(anchors.Max(static item => item.ScreenX), left + 1, width);
        double bottom = Math.Clamp(anchors.Max(static item => item.ScreenY), top + 1, height);
        return new OcrRectangle(left, top, right - left, bottom - top);
    }

    private static ExecutionReceipt CaptureExecutionReceipt(string root)
    {
        string directory = Path.Combine(root, "tools", "RealDevYTickRoleGeometryDiagnosticV1");
        Assembly[] assemblies =
        [
            Assembly.GetExecutingAssembly(),
            typeof(ProductionRasterFrameDecoder).Assembly,
            typeof(GraphReader.Axis.RobustCalibration).Assembly,
            typeof(GraphNumericParser).Assembly,
            typeof(OcrV8ProductionCompositionFactory).Assembly,
            typeof(InferenceRuntime).Assembly,
            typeof(GraphReader.Export.ExportPoint).Assembly,
        ];
        Dictionary<string, string> hashes = assemblies.Distinct().ToDictionary(
            static assembly => assembly.GetName().Name ?? "unknown", HashAssembly, StringComparer.Ordinal);
        Assembly? ort = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(static assembly => assembly.GetName().Name == "Microsoft.ML.OnnxRuntime");
        hashes["Microsoft.ML.OnnxRuntime"] = ort is null ? "not_loaded" : HashAssembly(ort);
        return new ExecutionReceipt(
            HashFile(Path.Combine(directory, "Program.cs")),
            HashFile(Path.Combine(directory, "RealDevYTickRoleGeometryDiagnosticV1.csproj")),
            hashes);
    }

    private static string HashAssembly(Assembly assembly) =>
        string.IsNullOrWhiteSpace(assembly.Location) || !File.Exists(assembly.Location)
            ? "location_unavailable"
            : HashFile(assembly.Location);

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return HashStream(stream);
    }

    private static string HashStream(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) hash.AppendData(buffer, 0, read);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string FindRepositoryRoot(string start)
    {
        DirectoryInfo? directory = new(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tools", "GraphReader.RealAcceptance.Ocr")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("REPOSITORY_ROOT_NOT_FOUND");
    }

    private static string CreateRunDirectory(string root)
    {
        string artifacts = Path.GetFullPath(Path.Combine(root, "artifacts"));
        string outputRoot = Path.GetFullPath(Path.Combine(artifacts, "real-dev-ytick-role-geometry-diagnostic-v1"));
        if (!Path.GetFullPath(outputRoot).StartsWith(Path.TrimEndingDirectorySeparator(artifacts) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OUTPUT_ROOT_OUTSIDE_ARTIFACTS");
        Directory.CreateDirectory(outputRoot);
        if ((File.GetAttributes(artifacts) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("OUTPUT_ROOT_REPARSE_POINT_REJECTED");
        string run = Path.Combine(outputRoot, "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        return run;
    }

    private static bool IsContinuousIntegration()
    {
        string[] names = ["CI", "TF_BUILD", "GITHUB_ACTIONS", "GITLAB_CI", "BUILD_BUILDID", "JENKINS_URL", "TEAMCITY_VERSION"];
        return names.Any(name => Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant()
            is not (null or "" or "0" or "false" or "no" or "off"));
    }

    private static int SelfTest()
    {
        IReadOnlyDictionary<string, int> emptyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var emptyEvaluation = new Evaluation(
            16, 0, 0, 0,
            emptyCounts, emptyCounts, emptyCounts, emptyCounts, emptyCounts,
            emptyCounts, emptyCounts, emptyCounts, emptyCounts, emptyCounts, emptyCounts,
            emptyCounts, emptyCounts, emptyCounts, emptyCounts, emptyCounts);
        var sampleReport = BuildAggregateReport(
            "self-test-composition",
            "self-test-configuration",
            new DevelopmentSelection(120, 28, 104, new string('0', 64), new string('0', 64), []),
            new string('0', 64),
            emptyEvaluation,
            new ExecutionReceipt(new string('0', 64), new string('0', 64), new Dictionary<string, string>()));
        using (JsonDocument serialized = JsonDocument.Parse(JsonSerializer.Serialize(sampleReport, JsonOptions)))
        {
            JsonElement root = serialized.RootElement;
            if (!Flag("frozen_assignment_verified", true) ||
                !Flag("previously_exposed_development_data", true) ||
                !Flag("sealed_payloads_read", false) ||
                !Flag("case_level_output", false) ||
                !Flag("production_approved", false) ||
                !Flag("sealed_acceptance", false) ||
                !Flag("only_aggregate_output", true) ||
                !Flag("no_ground_truth_tick_text_or_boxes", true)) return 1;

            bool Flag(string name, bool expected) => root.GetProperty(name).GetBoolean() == expected;
        }
        OcrRectangle plot = new(100, 20, 400, 200);
        GeometryBucket insideNear = Classify(new OcrRectangle(70, 50, 25, 12), plot);
        GeometryBucket outsideY = Classify(new OcrRectangle(70, 250, 25, 12), plot);
        GeometryBucket tooFar = Classify(new OcrRectangle(10, 50, 25, 12), plot);
        GeometryBucket overlapsAxis = Classify(new OcrRectangle(95, 50, 12, 12), plot);
        if (!insideNear.WithinVerticalExtent || !insideNear.LeftOfAxis ||
            !insideNear.WithinNear2_5Percent || !insideNear.WithinNear5Percent || !insideNear.WithinNear10Percent ||
            outsideY.WithinVerticalExtent || !outsideY.LeftOfAxis ||
            !tooFar.LeftOfAxis || tooFar.WithinNear10Percent || overlapsAxis.WithinNear10Percent)
            return 1;
        if (!(insideNear.WithinVerticalExtent && insideNear.WithinNear5Percent) ||
            outsideY.WithinVerticalExtent && outsideY.WithinNear5Percent)
            return 1;
        if (CreateNumericEvidence(OcrTextRole.YTick, OcrReviewStatus.Rejected, "12", 0.9) is not null ||
            CreateNumericEvidence(OcrTextRole.Other, OcrReviewStatus.Unreviewed, "12", 0.9) is null)
            return 1;
        if (CountBin(0) != "zero" || CountBin(1) != "one" || CountBin(2) != "two_or_more")
            return 1;
        Console.WriteLine("REAL_DEV_ROLE_GEOMETRY_SELF_TEST_PASS");
        return 0;
    }

    private static NumericEvidence? CreateNumericEvidence(
        OcrTextRole role, OcrReviewStatus reviewStatus, string text, double confidence)
    {
        if (reviewStatus == OcrReviewStatus.Rejected ||
            !double.IsFinite(confidence) || confidence is <= 0 or > 1)
            return null;
        NumericParseResult parsed = GraphNumericParser.Parse(text);
        if (!parsed.IsSuccess || parsed.Value is not { } value) return null;
        return new NumericEvidence(value, Math.Min(confidence, parsed.Confidence));
    }

    private static GeometryBucket Classify(OcrRectangle region, OcrRectangle plot)
    {
        double gap = region.Right <= plot.Left ? plot.Left - region.Right : double.PositiveInfinity;
        double relativeGap = gap / plot.Width;
        return new GeometryBucket(
            region.Center.Y >= plot.Top && region.Center.Y <= plot.Bottom,
            region.Right <= plot.Left,
            relativeGap <= 0.025,
            relativeGap <= 0.05,
            relativeGap <= 0.10);
    }

    private sealed class RoleGeometryAccumulator
    {
        private readonly Dictionary<string, int> _regionsByRole = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _rejectedRegionsByRole = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _numericEligibleByRole = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _numericWithinYExtentByRole = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _numericLeftOfAxisByRole = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _numericNearLeft2_5PercentByRole = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _numericNearLeft5PercentByRole = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _numericNearLeft10PercentByRole = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _numericNearLeftAndInY2_5PercentByRole = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _numericNearLeftAndInY5PercentByRole = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _numericNearLeftAndInY10PercentByRole = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _projectEligibleNumericYTickCountBins = NewBins();
        private readonly Dictionary<string, int> _projectNumericYTickWithinYExtentCountBins = NewBins();
        private readonly Dictionary<string, int> _projectNumericYTickNearLeft10PercentAndWithinYCountBins = NewBins();
        private readonly Dictionary<string, int> _sourceFailures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _candidateFailures = new(StringComparer.Ordinal);
        private int _projectsRecognized;

        internal void RecordSourceFailure(string kind) => Increment(_sourceFailures, kind);
        internal void RecordCandidateFailure(string kind) => Increment(_candidateFailures, kind);

        internal void Record(OcrResult result, OcrRectangle plot)
        {
            _projectsRecognized++;
            if (!result.Succeeded)
            {
                Increment(_candidateFailures, "ocr_result_failure");
                return;
            }
            int eligibleYTickCount = 0;
            int yTickWithinExtentCount = 0;
            int yTickNearLeftAndWithinYCount = 0;
            foreach (OcrRegion region in result.Regions)
            {
                string role = region.Role.ToString();
                Increment(_regionsByRole, role);
                if (region.ReviewStatus == OcrReviewStatus.Rejected)
                {
                    Increment(_rejectedRegionsByRole, role);
                    continue;
                }
                NumericEvidence? numeric = CreateNumericEvidence(
                    region.Role, region.ReviewStatus, region.Text, region.Confidence);
                if (numeric is null) continue;
                if (region.Role == OcrTextRole.YTick) eligibleYTickCount++;
                Increment(_numericEligibleByRole, role);
                GeometryBucket bucket = Classify(region.Polygon.Bounds, plot);
                if (region.Role == OcrTextRole.YTick && bucket.WithinVerticalExtent)
                    yTickWithinExtentCount++;
                if (region.Role == OcrTextRole.YTick && bucket.WithinVerticalExtent && bucket.WithinNear10Percent)
                    yTickNearLeftAndWithinYCount++;
                if (bucket.WithinVerticalExtent) Increment(_numericWithinYExtentByRole, role);
                if (bucket.LeftOfAxis) Increment(_numericLeftOfAxisByRole, role);
                if (bucket.WithinNear2_5Percent) Increment(_numericNearLeft2_5PercentByRole, role);
                if (bucket.WithinNear5Percent) Increment(_numericNearLeft5PercentByRole, role);
                if (bucket.WithinNear10Percent) Increment(_numericNearLeft10PercentByRole, role);
                if (bucket.WithinVerticalExtent && bucket.WithinNear2_5Percent)
                    Increment(_numericNearLeftAndInY2_5PercentByRole, role);
                if (bucket.WithinVerticalExtent && bucket.WithinNear5Percent)
                    Increment(_numericNearLeftAndInY5PercentByRole, role);
                if (bucket.WithinVerticalExtent && bucket.WithinNear10Percent)
                    Increment(_numericNearLeftAndInY10PercentByRole, role);
            }
            Increment(_projectEligibleNumericYTickCountBins, CountBin(eligibleYTickCount));
            Increment(_projectNumericYTickWithinYExtentCountBins, CountBin(yTickWithinExtentCount));
            Increment(_projectNumericYTickNearLeft10PercentAndWithinYCountBins, CountBin(yTickNearLeftAndWithinYCount));
        }

        internal Evaluation Finish(int denominator) => new(
            denominator,
            _projectsRecognized,
            _sourceFailures.Values.Sum(),
            _candidateFailures.Values.Sum(),
            _regionsByRole,
            _rejectedRegionsByRole,
            _numericEligibleByRole,
            _numericWithinYExtentByRole,
            _numericLeftOfAxisByRole,
            _numericNearLeft2_5PercentByRole,
            _numericNearLeft5PercentByRole,
            _numericNearLeft10PercentByRole,
            _numericNearLeftAndInY2_5PercentByRole,
            _numericNearLeftAndInY5PercentByRole,
            _numericNearLeftAndInY10PercentByRole,
            _projectEligibleNumericYTickCountBins,
            _projectNumericYTickWithinYExtentCountBins,
            _projectNumericYTickNearLeft10PercentAndWithinYCountBins,
            _sourceFailures,
            _candidateFailures);

        private static Dictionary<string, int> NewBins() => new(StringComparer.Ordinal)
        {
            ["zero"] = 0,
            ["one"] = 0,
            ["two_or_more"] = 0,
        };

        private static void Increment(Dictionary<string, int> dictionary, string key) =>
            dictionary[key] = dictionary.GetValueOrDefault(key) + 1;
    }

    private static string CountBin(int count) => count switch
    {
        0 => "zero",
        1 => "one",
        >= 2 => "two_or_more",
        _ => throw new ArgumentOutOfRangeException(nameof(count)),
    };

    private sealed class CandidateRuntime(InferenceRuntime runtime, OcrV8ProductionCompositionPipeline pipeline)
        : IAsyncDisposable
    {
        internal OcrV8ProductionCompositionPipeline Pipeline { get; } = pipeline;
        public ValueTask DisposeAsync() => runtime.DisposeAsync();
    }

    private sealed record DevelopmentSelection(
        int SelectedProjectCount, int StudyCount, int TrainProjectCount,
        string AssignmentSha256, string SelectedInventorySha256,
        IReadOnlyList<FrozenRealCorpusProject> Projects);

    private sealed record ExecutionReceipt(
        string SourceSha256, string ProjectSha256, IReadOnlyDictionary<string, string> LoadedAssemblySha256);

    private sealed record NumericEvidence(double Value, double Confidence);

    private sealed record GeometryBucket(
        bool WithinVerticalExtent,
        bool LeftOfAxis,
        bool WithinNear2_5Percent,
        bool WithinNear5Percent,
        bool WithinNear10Percent);

    private sealed record Evaluation(
        int DevelopmentProjectDenominator,
        int ProjectsRecognized,
        int SourceFailures,
        int CandidateFailures,
        IReadOnlyDictionary<string, int> RegionCountsByRole,
        IReadOnlyDictionary<string, int> RejectedRegionCountsByRole,
        IReadOnlyDictionary<string, int> EligibleNumericRegionCountsByRole,
        IReadOnlyDictionary<string, int> EligibleNumericWithinOracleYExtentByRole,
        IReadOnlyDictionary<string, int> EligibleNumericLeftOfOracleAxisByRole,
        IReadOnlyDictionary<string, int> EligibleNumericNearLeftAxis2_5PercentByRole,
        IReadOnlyDictionary<string, int> EligibleNumericNearLeftAxis5PercentByRole,
        IReadOnlyDictionary<string, int> EligibleNumericNearLeftAxis10PercentByRole,
        IReadOnlyDictionary<string, int> EligibleNumericNearLeftAndWithinYExtent2_5PercentByRole,
        IReadOnlyDictionary<string, int> EligibleNumericNearLeftAndWithinYExtent5PercentByRole,
        IReadOnlyDictionary<string, int> EligibleNumericNearLeftAndWithinYExtent10PercentByRole,
        IReadOnlyDictionary<string, int> ProjectsByEligibleNumericYTickCountBin,
        IReadOnlyDictionary<string, int> ProjectsByNumericYTickWithinOracleYExtentCountBin,
        IReadOnlyDictionary<string, int> ProjectsByNumericYTickNearLeft10PercentAndWithinYCountBin,
        IReadOnlyDictionary<string, int> SourceFailureKinds,
        IReadOnlyDictionary<string, int> CandidateFailureKinds);

    private sealed record AggregateReport(
        string Schema, string EvaluationMode, string CandidateMode, string CompositionId,
        string ConfigurationFingerprint, IReadOnlyDictionary<string, string> PayloadSha256,
        IReadOnlyDictionary<string, string> PayloadVersions, string InferenceProvider,
        int SelectedProjectCount, int StudyCount, int TrainProjectCount, int DevelopmentProjectCount,
        string AssignmentSha256, string SelectedInventorySha256, string DevelopmentContentSha256,
        string PartitionRule, bool FrozenAssignmentVerified, bool PreviouslyExposedDevelopmentData,
        bool SealedPayloadsRead, bool CaseLevelOutput, bool ProductionApproved, bool SealedAcceptance,
        string GeometryOracle, string NearAxisDefinition, string ProjectCountBinDefinition, double NearAxis2_5PercentPlotWidth,
        double NearAxis5PercentPlotWidth, double NearAxis10PercentPlotWidth,
        bool OnlyAggregateOutput, bool NoGroundTruthTickTextOrBoxes,
        Evaluation Aggregate, string SourceSha256, string ProjectSha256,
        IReadOnlyDictionary<string, string> LoadedAssemblySha256);

    private sealed record FailureReport(
        string Status, string FailureKind, bool PreviouslyExposedDevelopmentData,
        bool SealedPayloadsRead, bool CaseLevelOutput);
}
