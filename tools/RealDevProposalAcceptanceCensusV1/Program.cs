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

namespace GraphReader.RealDevProposalAcceptanceCensusV1;

internal static class Program
{
    private const int ExpectedSelectedProjects = 120;
    private const int ExpectedTrainProjects = 104;
    private const int ExpectedDevelopmentProjects = 16;
    private const int ExpectedStudies = 28;
    private const string PartitionRule = "sha256:graphreader-engauge-real-dev-study-v1:first-byte-lt-204";
    private const string WarningPrefix = "ocr_v8_";
    private static readonly string[] Outcomes =
    [
        "accepted_with_recognition",
        "rejected_by_composition",
        "accepted_without_recognition",
        "unclassified",
    ];
    private static readonly string[] GeometryKinds =
    [
        "within_oracle_y_extent",
        "left_of_oracle_axis",
        "near_left_2_5_percent",
        "near_left_5_percent",
        "near_left_10_percent",
        "near_left_2_5_percent_and_y",
        "near_left_5_percent_and_y",
        "near_left_10_percent_and_y",
    ];
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
            Console.Error.WriteLine("REAL_DEV_PROPOSAL_CENSUS_EXPLICIT_CANDIDATE_MODE_REQUIRED");
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
            Console.Error.WriteLine("REAL_DEV_PROPOSAL_CENSUS_PREFLIGHT_FAILED " + exception.GetType().Name);
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

            var counts = new ProposalCensusAccumulator();
            using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sourceHash.AppendData(Encoding.UTF8.GetBytes("graphreader.real-dev-proposal-acceptance-census-v1\n"));
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
                    OcrRectangle plotBounds = PlotBounds(truth.Anchors, image.Width, image.Height);
                    IReadOnlyList<OcrDetectedRegion> proposals = await candidate.ProposalDetector
                        .DetectProposalsAsync(image, CancellationToken.None).ConfigureAwait(false);
                    OcrResult result = await candidate.Pipeline.RecognizeAsync(new OcrRequest(
                        "real-dev-proposal-census",
                        "real-dev-proposal-census-panel",
                        truth.SourceImageSha256,
                        image,
                        plotBounds), CancellationToken.None).ConfigureAwait(false);
                    counts.Record(proposals, result, plotBounds);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
                {
                    counts.RecordCandidateFailure(exception.GetType().Name);
                }
            }

            _ = selector.EnumerateSelectedProjects(CancellationToken.None);
            ExecutionReceipt receipt = CaptureExecutionReceipt(root);
            var report = BuildReport(
                candidate.Pipeline.CompositionId,
                candidate.Pipeline.ConfigurationFingerprint,
                development,
                Convert.ToHexStringLower(sourceHash.GetHashAndReset()),
                counts.Finish(ExpectedDevelopmentProjects),
                receipt);
            File.WriteAllText(Path.Combine(output, "aggregate-report.json"),
                JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
            Console.WriteLine("REAL_DEV_PROPOSAL_CENSUS_REPORT_WRITTEN");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            File.WriteAllText(Path.Combine(output, "aggregate-failure.json"), JsonSerializer.Serialize(
                new FailureReport("failed", exception.GetType().Name, true, false, false), JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false));
            Console.Error.WriteLine("REAL_DEV_PROPOSAL_CENSUS_FAILED " + exception.GetType().Name);
            return 1;
        }
    }

    private static AggregateReport BuildReport(
        string compositionId,
        string configurationFingerprint,
        DevelopmentSelection development,
        string developmentContentSha256,
        Evaluation evaluation,
        ExecutionReceipt receipt) => new(
            Schema: "graphreader.real-dev-proposal-acceptance-census-v1",
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
            ProposalPopulation: "Returned by LocalOnnxProposalTextRegionDetector.DetectProposalsAsync after its 0.82 proposal confidence floor; lower-scoring component candidates inside the detector are not exposed by this API",
            OutcomeRules: "For every returned proposal the fixed V8 composition warning classifies it as proposal_rejected, accepted_with_recognition, or accepted_without_recognition; detector-pass UUIDs are matched to one pipeline outcome UUID without writing identifiers",
            GeometryDefinition: "Oracle bounds are the clamped extrema of the source Engauge axis anchors; within-Y uses proposal center Y; left-of-axis requires proposal right edge <= oracle left; near-left requires nonnegative edge gap <= the listed fraction of oracle plot width",
            NearAxis2_5PercentPlotWidth: 0.025,
            NearAxis5PercentPlotWidth: 0.05,
            NearAxis10PercentPlotWidth: 0.10,
            OnlyAggregateOutput: true,
            NoGroundTruthProposalTextOrBoxes: true,
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
        return new DevelopmentSelection(selected.Count, groups.Length, train, inventory.AssignmentSha256,
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
        var runtime = new InferenceRuntime(registry, new BoundedInferenceScheduler(2, 1),
            new NoPersistenceStageCache(), allowedProviders: [InferenceProvider.Cpu]);
        try
        {
            var detector = new LocalOnnxProposalTextRegionDetector(runtime,
                new LocalOnnxProposalTextRegionDetectorOptions(payloads.Detector)
                {
                    StageVersion = "0.0.21-v10-p2",
                    AllowedProviders = [InferenceProvider.Cpu],
                    BypassCache = true,
                });
            OcrV8ProductionCompositionPipeline pipeline = OcrV8ProductionCompositionFactory.Create(
                runtime, payloads, [InferenceProvider.Cpu], bypassCache: true);
            return new CandidateRuntime(runtime, pipeline, detector);
        }
        catch
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static OcrImage DecodeProductionImage(EngaugeDigWholeWorkflowTruth truth)
    {
        var image = new WorkflowImageEvidence("real-development-image", truth.SourceImageSha256,
            truth.SourceWidth, truth.SourceHeight, WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(Guid.NewGuid(), Guid.NewGuid(), "Development", image);
        var panel = new WorkflowPreparedPanel(imported, image, enhanced: null);
        var request = new ProductionWorkflowDetectionRequest(panel, image, WorkflowImageVariant.Original,
            Guid.NewGuid(), Guid.NewGuid(), truth.CopyImageBytes());
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
        string directory = Path.Combine(root, "tools", "RealDevProposalAcceptanceCensusV1");
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
        return new ExecutionReceipt(HashFile(Path.Combine(directory, "Program.cs")),
            HashFile(Path.Combine(directory, "RealDevProposalAcceptanceCensusV1.csproj")), hashes);
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
        string outputRoot = Path.GetFullPath(Path.Combine(artifacts, "real-dev-proposal-acceptance-census-v1"));
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
        OutcomePartition partition = PartitionOutcomes(
            ["p1", "p2", "p3"],
            ["ocr_v8_proposal_rejected:p1", "ocr_v8_acceptance_route:p2:detector", "ocr_v8_accepted_proposal_without_recognition:p3"]);
        if (!partition.Complete || partition.Rejected != 1 || partition.AcceptedWithRecognition != 1 ||
            partition.AcceptedWithoutRecognition != 1 || partition.Unclassified != 0 ||
            CountBin(0) != "zero" || CountBin(1) != "one" || CountBin(2) != "two_to_five" ||
            CountBin(6) != "six_to_ten" || CountBin(11) != "more_than_ten")
        {
            Console.Error.WriteLine("SELF_TEST_OUTCOME_PARTITION_FAILED");
            return 1;
        }
        if (PartitionOutcomes(["p1", "p2"], ["ocr_v8_proposal_rejected:p1"]).Complete ||
            PartitionOutcomes(["p1"], ["ocr_v8_proposal_rejected:p1", "ocr_v8_acceptance_route:p1:detector"]).Complete ||
            PartitionOutcomes(["p1"], ["ocr_v8_proposal_rejected:foreign"]).Complete)
        {
            Console.Error.WriteLine("SELF_TEST_PARTITION_BOUNDARIES_FAILED");
            return 1;
        }
        OcrRectangle plot = new(100, 20, 400, 200);
        Geometry geometry = Classify(new OcrRectangle(70, 50, 25, 12), plot);
        if (!geometry.WithinY || !geometry.LeftOfAxis || !geometry.Near2_5 || !geometry.Near5 || !geometry.Near10)
        {
            Console.Error.WriteLine("SELF_TEST_GEOMETRY_FAILED");
            return 1;
        }

        IReadOnlyDictionary<string, int> emptyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, long> emptyLongCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> emptyGeometry =
            new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> emptyHistograms =
            new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        var report = BuildReport("self-test", "self-test", new DevelopmentSelection(
                SelectedProjectCount: 120,
                StudyCount: 28,
                TrainProjectCount: 104,
                AssignmentSha256: new string('0', 64),
                SelectedInventorySha256: new string('0', 64),
                Projects: Array.AsReadOnly(Enumerable.Range(0, ExpectedDevelopmentProjects)
                    .Select(index => new FrozenRealCorpusProject(string.Empty, "case" + index, "study" + index,
                        FrozenRealCorpusInventory.RealDev)).ToArray())), new string('0', 64),
            new Evaluation(16, 0, 0, 0, 0, 0, 0, 0, 0, emptyLongCounts, emptyGeometry, emptyHistograms,
                emptyCounts, emptyCounts),
            new ExecutionReceipt(new string('0', 64), new string('0', 64), emptyCounts.ToDictionary(
                static pair => pair.Key, static pair => pair.Value.ToString(CultureInfo.InvariantCulture))));
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(report, JsonOptions));
        JsonElement root = json.RootElement;
        if (!Flag("frozen_assignment_verified", true) || !Flag("previously_exposed_development_data", true) ||
            !Flag("sealed_payloads_read", false) || !Flag("case_level_output", false) ||
            !Flag("production_approved", false) || !Flag("sealed_acceptance", false) ||
            !Flag("only_aggregate_output", true) || !Flag("no_ground_truth_proposal_text_or_boxes", true))
        {
            Console.Error.WriteLine("SELF_TEST_DISCLOSURE_FLAGS_FAILED");
            return 1;
        }
        if (root.GetProperty("schema").GetString() != "graphreader.real-dev-proposal-acceptance-census-v1" ||
            root.GetProperty("candidate_mode").GetString() != "v8-cpu" ||
            root.GetProperty("development_project_count").GetInt32() != ExpectedDevelopmentProjects ||
            !root.GetProperty("proposal_population").GetString()!.Contains("0.82", StringComparison.Ordinal) ||
            root.GetProperty("near_axis10_percent_plot_width").GetDouble() != 0.10 ||
            root.GetProperty("aggregate").GetProperty("proposal_counts_by_outcome").EnumerateObject().Any())
        {
            Console.Error.WriteLine("SELF_TEST_REPORT_METADATA_FAILED");
            return 1;
        }
        Console.WriteLine("REAL_DEV_PROPOSAL_CENSUS_SELF_TEST_PASS");
        return 0;

        bool Flag(string name, bool expected) => root.GetProperty(name).GetBoolean() == expected;
    }

    private static OutcomePartition PartitionOutcomes(IReadOnlyCollection<string> proposalIds, IReadOnlyList<string> warnings)
    {
        var proposalSet = proposalIds.ToHashSet(StringComparer.Ordinal);
        var eventsById = new Dictionary<string, List<ProposalOutcome>>(StringComparer.Ordinal);
        int foreignWarnings = 0;
        foreach (string warning in warnings)
        {
            if (!TryReadOutcome(warning, out string? id, out ProposalOutcome outcome)) continue;
            if (!proposalSet.Contains(id)) foreignWarnings++;
            if (!eventsById.TryGetValue(id, out List<ProposalOutcome>? events))
                eventsById.Add(id, events = []);
            events.Add(outcome);
        }

        int accepted = 0;
        int rejected = 0;
        int acceptedWithoutRecognition = 0;
        int unclassified = 0;
        var outcomesById = new Dictionary<string, ProposalOutcome>(StringComparer.Ordinal);
        bool exact = foreignWarnings == 0 && eventsById.Count == proposalSet.Count;
        foreach (string id in proposalSet)
        {
            if (!eventsById.TryGetValue(id, out List<ProposalOutcome>? events) || events.Count != 1)
            {
                exact = false;
                unclassified++;
                continue;
            }
            switch (events[0])
            {
                case ProposalOutcome.AcceptedWithRecognition:
                    accepted++;
                    outcomesById.Add(id, events[0]);
                    break;
                case ProposalOutcome.Rejected:
                    rejected++;
                    outcomesById.Add(id, events[0]);
                    break;
                case ProposalOutcome.AcceptedWithoutRecognition:
                    acceptedWithoutRecognition++;
                    outcomesById.Add(id, events[0]);
                    break;
                default: exact = false; unclassified++; break;
            }
        }
        if (eventsById.Values.Any(static events => events.Count != 1)) exact = false;
        return new OutcomePartition(exact, accepted, rejected, acceptedWithoutRecognition, unclassified, foreignWarnings)
        {
            OutcomeById = outcomesById,
        };
    }

    private static bool TryReadOutcome(string warning, out string id, out ProposalOutcome outcome)
    {
        const string rejected = "ocr_v8_proposal_rejected:";
        const string acceptedWithout = "ocr_v8_accepted_proposal_without_recognition:";
        const string accepted = "ocr_v8_acceptance_route:";
        if (warning.StartsWith(rejected, StringComparison.Ordinal))
        {
            id = warning[rejected.Length..];
            outcome = ProposalOutcome.Rejected;
            return id.Length != 0;
        }
        if (warning.StartsWith(acceptedWithout, StringComparison.Ordinal))
        {
            id = warning[acceptedWithout.Length..];
            outcome = ProposalOutcome.AcceptedWithoutRecognition;
            return id.Length != 0;
        }
        if (warning.StartsWith(accepted, StringComparison.Ordinal))
        {
            string eventTail = warning[accepted.Length..];
            int separator = eventTail.IndexOf(':', StringComparison.Ordinal);
            id = separator < 0 ? string.Empty : eventTail[..separator];
            outcome = ProposalOutcome.AcceptedWithRecognition;
            return id.Length != 0 && separator < eventTail.Length - 1;
        }
        id = string.Empty;
        outcome = default;
        return false;
    }

    private static Geometry Classify(OcrRectangle region, OcrRectangle plot)
    {
        double gap = region.Right <= plot.Left ? plot.Left - region.Right : double.PositiveInfinity;
        double relativeGap = gap / plot.Width;
        bool withinY = region.Center.Y >= plot.Top && region.Center.Y <= plot.Bottom;
        bool left = region.Right <= plot.Left;
        bool near2_5 = left && relativeGap <= 0.025;
        bool near5 = left && relativeGap <= 0.05;
        bool near10 = left && relativeGap <= 0.10;
        return new Geometry(withinY, left, near2_5, near5, near10,
            withinY && near2_5, withinY && near5, withinY && near10);
    }

    private static string CountBin(int count) => count switch
    {
        0 => "zero",
        1 => "one",
        >= 2 and <= 5 => "two_to_five",
        >= 6 and <= 10 => "six_to_ten",
        > 10 => "more_than_ten",
        _ => throw new ArgumentOutOfRangeException(nameof(count)),
    };

    private sealed class ProposalCensusAccumulator
    {
        private readonly List<PanelCounts> _panels = [];
        private readonly Dictionary<string, int> _sourceFailures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _candidateFailures = new(StringComparer.Ordinal);
        private int _panelsRecognized;
        private int _panelsWithExactProposalIdAccounting;
        private int _panelsWithMismatch;
        private int _panelsWithResult;
        private int _unmatchedWarningCount;

        internal void RecordSourceFailure(string kind) => Increment(_sourceFailures, kind);
        internal void RecordCandidateFailure(string kind) => Increment(_candidateFailures, kind);

        internal void Record(IReadOnlyList<OcrDetectedRegion> proposals, OcrResult result, OcrRectangle plot)
        {
            _panelsWithResult++;
            if (result.Succeeded) _panelsRecognized++;
            else Increment(_candidateFailures, result.Failure?.Code ?? "ocr_result_failure");
            string[] proposalIds = proposals.Select(static proposal => proposal.RegionId).ToArray();
            if (proposalIds.Distinct(StringComparer.Ordinal).Count() != proposalIds.Length)
            {
                Increment(_candidateFailures, "duplicate_detector_proposal_id");
                _panelsWithMismatch++;
                return;
            }
            OutcomePartition partition = PartitionOutcomes(proposalIds, result.Warnings);
            if (partition.Complete) _panelsWithExactProposalIdAccounting++;
            else _panelsWithMismatch++;
            _unmatchedWarningCount += partition.ForeignWarnings;

            var panel = new PanelCounts();
            foreach (OcrDetectedRegion proposal in proposals)
            {
                string outcome = partition.OutcomeById.TryGetValue(proposal.RegionId, out ProposalOutcome value)
                    ? OutcomeName(value)
                    : "unclassified";
                panel.IncrementOutcome("all_proposals");
                panel.IncrementOutcome(outcome);
                Geometry geometry = Classify(proposal.Polygon.Bounds, plot);
                foreach (string geometryKind in GeometryNames(geometry))
                {
                    panel.IncrementGeometry("all_proposals", geometryKind);
                    panel.IncrementGeometry(outcome, geometryKind);
                }
            }
            _panels.Add(panel);
        }

        internal Evaluation Finish(int denominator)
        {
            Dictionary<string, long> outcomes = Outcomes.Append("all_proposals").ToDictionary(
                static name => name, static _ => 0L, StringComparer.Ordinal);
            var geometryByOutcome = Outcomes.Append("all_proposals").ToDictionary(
                static outcome => outcome,
                static _ => GeometryKinds.ToDictionary(static geometry => geometry, static _ => 0L, StringComparer.Ordinal),
                StringComparer.Ordinal);
            var panelHistograms = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (string outcome in Outcomes.Append("all_proposals"))
            {
                string[] categories = new[] { outcome }.Concat(GeometryKinds.Select(geometry => outcome + "." + geometry)).ToArray();
                foreach (string category in categories)
                {
                    var bins = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["zero"] = 0, ["one"] = 0, ["two_to_five"] = 0,
                        ["six_to_ten"] = 0, ["more_than_ten"] = 0,
                    };
                    foreach (PanelCounts panel in _panels)
                    {
                        int count = category.Contains('.', StringComparison.Ordinal)
                            ? panel.GeometryCount(category[..category.IndexOf('.', StringComparison.Ordinal)],
                                category[(category.IndexOf('.', StringComparison.Ordinal) + 1)..])
                            : panel.OutcomeCount(category);
                        Increment(bins, CountBin(count));
                    }
                    panelHistograms.Add(category, bins);
                }
            }

            foreach (PanelCounts panel in _panels)
            {
                foreach (string outcome in Outcomes.Append("all_proposals"))
                {
                    outcomes[outcome] += panel.OutcomeCount(outcome);
                    foreach (string geometry in GeometryKinds)
                        geometryByOutcome[outcome][geometry] += panel.GeometryCount(outcome, geometry);
                }
            }
            return new Evaluation(
                denominator,
                _panels.Count,
                _panelsWithResult,
                _panelsRecognized,
                _sourceFailures.Values.Sum(),
                _candidateFailures.Values.Sum(),
                _panelsWithExactProposalIdAccounting,
                _panelsWithMismatch,
                _unmatchedWarningCount,
                outcomes,
                geometryByOutcome.ToDictionary(static pair => pair.Key,
                    static pair => (IReadOnlyDictionary<string, long>)pair.Value, StringComparer.Ordinal),
                panelHistograms.ToDictionary(static pair => pair.Key,
                    static pair => (IReadOnlyDictionary<string, int>)pair.Value, StringComparer.Ordinal),
                _sourceFailures,
                _candidateFailures);
        }

        private static IEnumerable<string> GeometryNames(Geometry value)
        {
            if (value.WithinY) yield return "within_oracle_y_extent";
            if (value.LeftOfAxis) yield return "left_of_oracle_axis";
            if (value.Near2_5) yield return "near_left_2_5_percent";
            if (value.Near5) yield return "near_left_5_percent";
            if (value.Near10) yield return "near_left_10_percent";
            if (value.Near2_5AndY) yield return "near_left_2_5_percent_and_y";
            if (value.Near5AndY) yield return "near_left_5_percent_and_y";
            if (value.Near10AndY) yield return "near_left_10_percent_and_y";
        }

        private static string OutcomeName(ProposalOutcome outcome) => outcome switch
        {
            ProposalOutcome.AcceptedWithRecognition => "accepted_with_recognition",
            ProposalOutcome.Rejected => "rejected_by_composition",
            ProposalOutcome.AcceptedWithoutRecognition => "accepted_without_recognition",
            _ => "unclassified",
        };
    }

    private sealed class PanelCounts
    {
        private readonly Dictionary<string, int> _outcomes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, int>> _geometry = new(StringComparer.Ordinal);
        internal void IncrementOutcome(string outcome) => Increment(_outcomes, outcome);
        internal void IncrementGeometry(string outcome, string geometry)
        {
            if (!_geometry.TryGetValue(outcome, out Dictionary<string, int>? counts))
                _geometry.Add(outcome, counts = new Dictionary<string, int>(StringComparer.Ordinal));
            Increment(counts, geometry);
        }
        internal int OutcomeCount(string outcome) => _outcomes.GetValueOrDefault(outcome);
        internal int GeometryCount(string outcome, string geometry) =>
            _geometry.TryGetValue(outcome, out Dictionary<string, int>? counts) ? counts.GetValueOrDefault(geometry) : 0;
    }

    private static void Increment(Dictionary<string, int> values, string key) =>
        values[key] = values.GetValueOrDefault(key) + 1;

    private static void Increment(Dictionary<string, long> values, string key) =>
        values[key] = values.GetValueOrDefault(key) + 1;

    private enum ProposalOutcome { AcceptedWithRecognition, Rejected, AcceptedWithoutRecognition }
    private sealed record OutcomePartition(
        bool Complete, int AcceptedWithRecognition, int Rejected, int AcceptedWithoutRecognition,
        int Unclassified, int ForeignWarnings)
    {
        internal Dictionary<string, ProposalOutcome> OutcomeById { get; init; } =
            new Dictionary<string, ProposalOutcome>(StringComparer.Ordinal);
    }
    private sealed record Geometry(
        bool WithinY, bool LeftOfAxis, bool Near2_5, bool Near5, bool Near10,
        bool Near2_5AndY, bool Near5AndY, bool Near10AndY);
    private sealed record CandidateRuntime(
        InferenceRuntime Runtime,
        OcrV8ProductionCompositionPipeline Pipeline,
        LocalOnnxProposalTextRegionDetector ProposalDetector) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }
    private sealed record DevelopmentSelection(
        int SelectedProjectCount, int StudyCount, int TrainProjectCount,
        string AssignmentSha256, string SelectedInventorySha256,
        IReadOnlyList<FrozenRealCorpusProject> Projects);
    private sealed record ExecutionReceipt(
        string SourceSha256, string ProjectSha256, IReadOnlyDictionary<string, string> LoadedAssemblySha256);
    private sealed record Evaluation(
        int DevelopmentPanelDenominator, int PanelCensusCount, int PanelsWithResult, int PanelsRecognized,
        int SourceFailures, int CandidateFailures, int PanelsWithExactProposalIdAccounting,
        int PanelsWithProposalIdMismatch, int UnmatchedOutcomeWarningCount,
        IReadOnlyDictionary<string, long> ProposalCountsByOutcome,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> ProposalGeometryCountsByOutcome,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> PanelCountHistograms,
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
        string ProposalPopulation, string OutcomeRules, string GeometryDefinition,
        double NearAxis2_5PercentPlotWidth, double NearAxis5PercentPlotWidth, double NearAxis10PercentPlotWidth,
        bool OnlyAggregateOutput, bool NoGroundTruthProposalTextOrBoxes, Evaluation Aggregate,
        string SourceSha256, string ProjectSha256, IReadOnlyDictionary<string, string> LoadedAssemblySha256);
    private sealed record FailureReport(
        string Status, string FailureKind, bool PreviouslyExposedDevelopmentData,
        bool SealedPayloadsRead, bool CaseLevelOutput);
}
