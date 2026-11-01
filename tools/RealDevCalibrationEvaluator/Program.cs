// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Inference;
using GraphReader.Ocr;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.RealDevCalibrationEvaluator;

internal static class Program
{
    private const int ExpectedSelectedProjects = 120;
    private const int ExpectedTrainProjects = 104;
    private const int ExpectedDevelopmentProjects = 16;
    private const int ExpectedStudies = 28;
    private const string PartitionRule = "sha256:graphreader-engauge-real-dev-study-v1:first-byte-lt-204";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            return SelfTest();
        }

        bool explicitOptIn = args.Contains("--explicit-opt-in", StringComparer.Ordinal);
        bool preflight = args.Contains("--preflight", StringComparer.Ordinal);
        int candidateIndex = Array.IndexOf(args, "--candidate-mode");
        bool candidateMode = candidateIndex >= 0 && candidateIndex + 1 < args.Length &&
            args[candidateIndex + 1] == "v8-cpu";
        if (!explicitOptIn || IsContinuousIntegration() ||
            (!preflight && !candidateMode) || (preflight && candidateMode) ||
            args.Any(argument => argument is not ("--explicit-opt-in" or "--preflight" or "--candidate-mode" or "v8-cpu")))
        {
            Console.Error.WriteLine("REAL_DEV_CALIBRATION_EXPLICIT_MODE_REQUIRED");
            return 2;
        }

        string repositoryRoot;
        string outputDirectory;
        try
        {
            repositoryRoot = FindRepositoryRoot(Environment.CurrentDirectory);
            outputDirectory = CreateRunDirectory(repositoryRoot);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("REAL_DEV_CALIBRATION_PREFLIGHT_FAILED " + exception.GetType().Name);
            return 2;
        }

        try
        {
            FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(
                Path.Combine(repositoryRoot, "data", "manual data"), CancellationToken.None);
            FrozenRealCorpusSelection selection = FrozenRealCorpusInventory.Load(
                Path.Combine(repositoryRoot, "data", "manual data"),
                FrozenRealCorpusInventory.RealDev,
                CancellationToken.None);
            IReadOnlyList<FrozenRealCorpusProject> projects = selector.EnumerateSelectedProjects(CancellationToken.None);
            SplitSelection split = ValidateAndSelectDevelopment(selector, selection, projects);
            OcrV8ProductionPayloadSet payloads = LoadAndValidatePayloads(repositoryRoot);

            if (preflight)
            {
                ExecutionReceipt receipt = CaptureExecutionReceipt(repositoryRoot);
                var report = new PreflightReport(
                    "graphreader.real-dev-calibration-preflight.v1",
                    "pass",
                    split.ProjectCount,
                    split.StudyCount,
                    split.TrainProjectCount,
                    split.DevelopmentProjectCount,
                    split.AssignmentSha256,
                    split.SelectedInventorySha256,
                    PartitionRule,
                    true,
                    true,
                    false,
                    false,
                    false,
                    "preflight_only_no_source_payloads_opened",
                    receipt.SourceSha256,
                    receipt.ProjectSha256,
                    receipt.LoadedAssemblySha256);
                WriteReport(outputDirectory, report);
                Console.WriteLine("REAL_DEV_CALIBRATION_PREFLIGHT_PASS");
                return 0;
            }

            await using CandidateRuntime candidateRuntime = CreateCandidate(payloads);
            OcrV8ProductionCompositionPipeline pipeline = candidateRuntime.Pipeline;
            var totals = new AggregateAccumulator();
            using var corpusContentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            corpusContentHash.AppendData(Encoding.UTF8.GetBytes("graphreader.real-dev-calibration-content.v1\n"));
            corpusContentHash.AppendData(Encoding.UTF8.GetBytes(split.AssignmentSha256 + "\n"));
            foreach (FrozenRealCorpusProject project in split.DevelopmentProjects)
            {
                EngaugeDigWholeWorkflowTruth truth;
                try
                {
                    truth = ReadSelectedProject(selector, project);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
                {
                    totals.RecordSourceFailure(exception.GetType().Name);
                    continue;
                }

                totals.RecordTruth(truth);
                corpusContentHash.AppendData(Encoding.UTF8.GetBytes(
                    project.AnonymizedCaseId + "\n" + truth.ProjectSha256 + "\n" +
                    truth.SourceImageSha256 + "\n"));

                try
                {
                    OcrResult result = await RecognizeAsync(pipeline, truth).ConfigureAwait(false);
                    totals.RecordCandidate(truth, result);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
                {
                    totals.RecordCandidateFailure(exception.GetType().Name);
                }
            }

            _ = selector.EnumerateSelectedProjects(CancellationToken.None);
            ExecutionReceipt candidateReceipt = CaptureExecutionReceipt(repositoryRoot);
            var evaluation = totals.Finish(split.DevelopmentProjectCount);
            var candidateReport = new CandidateReport(
                "graphreader.real-dev-calibration-aggregate.v1",
                "development_diagnostic",
                "v8-cpu",
                pipeline.CompositionId,
                pipeline.ConfigurationFingerprint,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["detector"] = OcrV8ProductionCompositionFactory.DetectorSha256,
                    ["official_recognizer"] = OcrV8ProductionCompositionFactory.OfficialRecognizerSha256,
                    ["numeric_recognizer"] = OcrV8ProductionCompositionFactory.NumericRecognizerSha256,
                    ["ambiguity_recognizer"] = OcrV8ProductionCompositionFactory.AmbiguityRecognizerSha256,
                    ["official_alphabet"] = OcrV8ProductionCompositionFactory.OfficialAlphabetSha256,
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["detector"] = "0.0.21-p2",
                    ["official_recognizer"] = "0.0.21-converted",
                    ["numeric_recognizer"] = "0.0.21-p1",
                    ["ambiguity_recognizer"] = "0.0.21-p2",
                },
                InferenceProvider.Cpu.ToString(),
                split.ProjectCount,
                split.StudyCount,
                split.TrainProjectCount,
                split.DevelopmentProjectCount,
                split.AssignmentSha256,
                split.SelectedInventorySha256,
                Convert.ToHexStringLower(corpusContentHash.GetHashAndReset()),
                PartitionRule,
                true,
                true,
                false,
                false,
                false,
                false,
                "production raster decoder; full unmasked raster passed to V8 detector; Engauge anchors supply oracle plot bounds and three-point Y validation; no axis detector or production axis mask",
                "non-rejected YTick-role regions use GraphNumericParser.Parse with Math.Min(region, parser) confidence weights and RobustCalibration.FitY defaults",
                "sampled Engauge curve points are not asserted exhaustive; no complete marker masks or semantic series/phase labels; five graph units is scale-dependent and diagnostic only, not an accuracy gate",
                candidateReceipt.SourceSha256,
                candidateReceipt.ProjectSha256,
                candidateReceipt.LoadedAssemblySha256,
                evaluation);
            WriteReport(outputDirectory, candidateReport);
            Console.WriteLine("REAL_DEV_CALIBRATION_CANDIDATE_REPORT_WRITTEN");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            WriteReport(outputDirectory, new FailureReport(
                "graphreader.real-dev-calibration-failure.v1",
                "failed",
                exception.GetType().Name,
                true,
                true,
                false));
            Console.Error.WriteLine("REAL_DEV_CALIBRATION_FAILED " + exception.GetType().Name);
            return 1;
        }
    }

    private static SplitSelection ValidateAndSelectDevelopment(
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
        {
            throw new InvalidDataException("LOCKED_REAL_DEV_ASSIGNMENT_MISMATCH");
        }

        Dictionary<string, FrozenRealCorpusProject[]> studies = projects
            .GroupBy(static project => project.AnonymizedStudyId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        int trainCount = 0;
        int developmentCount = 0;
        var development = new List<FrozenRealCorpusProject>();
        foreach ((string studyId, FrozenRealCorpusProject[] studyProjects) in studies)
        {
            EngaugeDevStudyPartition partition = EngaugeDevSourceMaterializer.PartitionForStudy(studyId);
            if (studyProjects.Any(project => project.AnonymizedStudyId != studyId))
                throw new InvalidDataException("LOCKED_REAL_DEV_STUDY_GROUP_MISMATCH");
            if (partition == EngaugeDevStudyPartition.Train)
            {
                trainCount += studyProjects.Length;
                continue;
            }
            if (partition != EngaugeDevStudyPartition.Development)
                throw new InvalidDataException("LOCKED_REAL_DEV_PARTITION_INVALID");
            developmentCount += studyProjects.Length;
            development.AddRange(studyProjects);
        }
        if (studies.Count != ExpectedStudies || trainCount != ExpectedTrainProjects ||
            developmentCount != ExpectedDevelopmentProjects || development.Count != ExpectedDevelopmentProjects)
        {
            throw new InvalidDataException("LOCKED_REAL_DEV_PARTITION_COUNTS_MISMATCH");
        }

        _ = selector.EnumerateSelectedProjects(CancellationToken.None);
        return new SplitSelection(
            projects.Count,
            studies.Count,
            trainCount,
            developmentCount,
            selection.AssignmentSha256,
            selection.SelectedInventorySha256,
            Array.AsReadOnly(development.OrderBy(static project => project.AnonymizedCaseId, StringComparer.Ordinal).ToArray()));
    }

    private static EngaugeDigWholeWorkflowTruth ReadSelectedProject(
        FrozenDevTrainingSelector selector,
        FrozenRealCorpusProject project)
    {
        if (project.Split != FrozenRealCorpusInventory.RealDev ||
            EngaugeDevSourceMaterializer.PartitionForStudy(project.AnonymizedStudyId) != EngaugeDevStudyPartition.Development)
            throw new InvalidDataException("NON_DEVELOPMENT_PAYLOAD_READ_REJECTED");

        FrozenRealCorpusProject current = selector.RequireProject(
            project.GetProjectPath(), project.AnonymizedCaseId, project.AnonymizedStudyId, CancellationToken.None);
        using var stream = new FileStream(
            current.GetProjectPath(), FileMode.Open, FileAccess.Read, FileShare.Read);
        EngaugeDigWholeWorkflowTruth truth = EngaugeDigWholeWorkflowTruthAdapter.Read(
            stream,
            current.AnonymizedCaseId,
            () =>
            {
                _ = selector.RequireProject(
                    current.GetProjectPath(), current.AnonymizedCaseId, current.AnonymizedStudyId,
                    CancellationToken.None);
                EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, current.GetProjectPath());
            },
            CancellationToken.None);
        EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, current.GetProjectPath());
        stream.Position = 0;
        string after = HashStream(stream);
        if (!string.Equals(after, truth.ProjectSha256, StringComparison.Ordinal))
            throw new InvalidDataException("DEVELOPMENT_SOURCE_CHANGED_DURING_READ");
        return truth;
    }

    private static OcrV8ProductionPayloadSet LoadAndValidatePayloads(string root)
    {
        ModelIdentity Model(string id, string version, string sha256, string relativePath) =>
            new(id, version, sha256, Path.GetFullPath(Path.Combine(root, relativePath)));

        string yamlPath = Path.Combine(root,
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
            ReadAlphabet(yamlPath));
        OcrV8ProductionCompositionFactory.ValidatePayloads(payloads);
        return payloads;
    }

    private static string ReadAlphabet(string yamlPath)
    {
        string[] lines = File.ReadAllLines(yamlPath, Encoding.UTF8);
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

    private static CandidateRuntime CreateCandidate(
        OcrV8ProductionPayloadSet payloads)
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

    private static async Task<OcrResult> RecognizeAsync(
        OcrV8ProductionCompositionPipeline pipeline,
        EngaugeDigWholeWorkflowTruth truth)
    {
        OcrImage image = DecodeProductionImage(truth);
        OcrRectangle plot = PlotBounds(truth.Anchors, image.Width, image.Height);
        return await pipeline.RecognizeAsync(new OcrRequest(
            "real-dev-calibration-eval",
            "real-dev-calibration-panel",
            truth.SourceImageSha256,
            image,
            plot), CancellationToken.None).ConfigureAwait(false);
    }

    private static OcrImage DecodeProductionImage(EngaugeDigWholeWorkflowTruth truth)
    {
        byte[] bytes = truth.CopyImageBytes();
        var image = new WorkflowImageEvidence(
            "real-development-image",
            truth.SourceImageSha256,
            truth.SourceWidth,
            truth.SourceHeight,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(Guid.NewGuid(), Guid.NewGuid(), "Development", image);
        var panel = new WorkflowPreparedPanel(imported, image, enhanced: null);
        var request = new ProductionWorkflowDetectionRequest(
            panel,
            image,
            WorkflowImageVariant.Original,
            Guid.NewGuid(),
            Guid.NewGuid(),
            bytes);
        ProductionDecodedRaster decoded = new ProductionRasterFrameDecoder().Decode(request, CancellationToken.None);
        if (!string.Equals(decoded.InputSha256, truth.SourceImageSha256, StringComparison.Ordinal) ||
            decoded.Width != truth.SourceWidth || decoded.Height != truth.SourceHeight)
            throw new InvalidDataException("DEVELOPMENT_IMAGE_DECODE_INVALID");
        return decoded.CreateOcrImage();
    }

    private static OcrRectangle PlotBounds(
        IReadOnlyList<EngaugeDigAxisAnchor> anchors,
        int width,
        int height)
    {
        double left = Math.Clamp(anchors.Min(static item => item.ScreenX), 0, width - 1);
        double top = Math.Clamp(anchors.Min(static item => item.ScreenY), 0, height - 1);
        double right = Math.Clamp(anchors.Max(static item => item.ScreenX), left + 1, width);
        double bottom = Math.Clamp(anchors.Max(static item => item.ScreenY), top + 1, height);
        return new OcrRectangle(left, top, right - left, bottom - top);
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

    private static string CreateRunDirectory(string repositoryRoot)
    {
        string artifacts = Path.GetFullPath(Path.Combine(repositoryRoot, "artifacts"));
        string root = Path.GetFullPath(Path.Combine(artifacts, "real-dev-calibration-evaluator"));
        if (!IsUnder(root, artifacts)) throw new InvalidDataException("OUTPUT_ROOT_OUTSIDE_ARTIFACTS");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(artifacts) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("OUTPUT_ROOT_REPARSE_POINT_REJECTED");
        string run = Path.Combine(root, "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        return run;
    }

    private static bool IsUnder(string path, string root)
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static string HashStream(Stream stream)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) sha.AppendData(buffer, 0, read);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static ExecutionReceipt CaptureExecutionReceipt(string repositoryRoot)
    {
        string toolDirectory = Path.Combine(repositoryRoot, "tools", "RealDevCalibrationEvaluator");
        Assembly[] assemblies =
        [
            Assembly.GetExecutingAssembly(),
            typeof(ProductionRasterFrameDecoder).Assembly,
            typeof(RobustCalibration).Assembly,
            typeof(GraphNumericParser).Assembly,
            typeof(OcrV8ProductionCompositionFactory).Assembly,
            typeof(InferenceRuntime).Assembly,
            typeof(GraphReader.Export.ExportPoint).Assembly,
        ];
        Dictionary<string, string> hashes = assemblies
            .Distinct()
            .ToDictionary(static assembly => assembly.GetName().Name ?? "unknown", HashAssembly, StringComparer.Ordinal);
        Assembly? onnxRuntime = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(static assembly => assembly.GetName().Name == "Microsoft.ML.OnnxRuntime");
        hashes["Microsoft.ML.OnnxRuntime"] = onnxRuntime is null ? "not_loaded" : HashAssembly(onnxRuntime);
        return new ExecutionReceipt(
            HashFile(Path.Combine(toolDirectory, "Program.cs")),
            HashFile(Path.Combine(toolDirectory, "RealDevCalibrationEvaluator.csproj")),
            hashes);
    }

    private static string HashAssembly(Assembly assembly)
    {
        string location = assembly.Location;
        return string.IsNullOrWhiteSpace(location) || !File.Exists(location)
            ? "location_unavailable"
            : HashFile(location);
    }

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return HashStream(stream);
    }

    private static bool IsContinuousIntegration()
    {
        string[] names = ["CI", "TF_BUILD", "GITHUB_ACTIONS", "GITLAB_CI", "BUILD_BUILDID", "JENKINS_URL", "TEAMCITY_VERSION"];
        return names.Any(name => Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant()
            is not (null or "" or "0" or "false" or "no" or "off"));
    }

    private static void WriteReport(string outputDirectory, object report)
    {
        string path = Path.Combine(outputDirectory, "aggregate-report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static int SelfTest()
    {
        var ticks = new[] { (10d, 100d, 1d), (20d, 50d, 1d), (30d, 0d, 1d) };
        if (!TryFitTickCalibration(ticks, out double intercept, out double slope, out _)) return 1;
        if (Math.Abs(intercept - 150) > 1e-9 || Math.Abs(slope + 5) > 1e-9) return 1;
        if (!TryFitTickCalibration([(0d, 100d, 1d), (10d, 50d, 1d), (30d, -50d, 1d)],
                out double unevenIntercept, out double unevenSlope, out _) ||
            Math.Abs(unevenIntercept - 100) > 1e-9 || Math.Abs(unevenSlope + 5) > 1e-9) return 1;
        if (TryFitTickCalibration([(10d, 10d, 1d)], out _, out _, out _)) return 1;
        if (TryFitTickCalibration([(10d, 0d, 1d), (20d, 10d, 1d), (30d, 5d, 1d)], out _, out _, out _)) return 1;
        NumericParseResult alternateRead = GraphNumericParser.Parse("O");
        NumericTickEvidence? parserLimited = CreateYTickEvidence(
            "self-test-parser-confidence", OcrTextRole.YTick, OcrReviewStatus.Unreviewed, "O", 1d, 12d);
        NumericTickEvidence? regionLimited = CreateYTickEvidence(
            "self-test-region-confidence", OcrTextRole.YTick, OcrReviewStatus.Unreviewed, "12", 0.72d, 12d);
        if (!alternateRead.IsSuccess || parserLimited is null ||
            Math.Abs(parserLimited.Confidence - alternateRead.Confidence) > 1e-12 ||
            regionLimited is null || Math.Abs(regionLimited.Confidence - 0.72d) > 1e-12) return 1;
        if (CreateYTickEvidence("self-test-rejected", OcrTextRole.YTick, OcrReviewStatus.Rejected, "12", 0.9d, 12d) is not null ||
            CreateYTickEvidence("self-test-other-role", OcrTextRole.Other, OcrReviewStatus.Unreviewed, "12", 0.9d, 12d) is not null ||
            CreateYTickEvidence("self-test-invalid-confidence", OcrTextRole.YTick, OcrReviewStatus.Unreviewed, "12", 0d, 12d) is not null) return 1;
        EvaluationReport emptyEvaluation = new AggregateAccumulator().Finish(16);
        if (emptyEvaluation.MaximumAxisAnchorErrorPixels is not null ||
            emptyEvaluation.MaximumAbsoluteYErrorGraphUnits is not null ||
            AvailableMaximum(1.5d, 0) is not null || AvailableMaximum(1.5d, 1) != 1.5d) return 1;
        Console.WriteLine("REAL_DEV_CALIBRATION_SELF_TEST_PASS");
        return 0;
    }

    private static NumericTickEvidence? CreateYTickEvidence(
        string id,
        OcrTextRole role,
        OcrReviewStatus reviewStatus,
        string text,
        double regionConfidence,
        double screenY)
    {
        if (role != OcrTextRole.YTick || reviewStatus == OcrReviewStatus.Rejected ||
            !double.IsFinite(regionConfidence) || regionConfidence is <= 0 or > 1)
            return null;
        NumericParseResult parsed = GraphNumericParser.Parse(text);
        if (!parsed.IsSuccess || parsed.Value is not { } value)
            return null;
        return new NumericTickEvidence(id, screenY, value, Math.Min(regionConfidence, parsed.Confidence));
    }

    private static double? AvailableMaximum(double maximum, long count) => count == 0 ? null : maximum;

    private static bool TryFitTickCalibration(
        (double ScreenY, double Value, double Confidence)[] source,
        out double intercept,
        out double slope,
        out string failure)
    {
        intercept = 0;
        slope = 0;
        failure = string.Empty;
        NumericTickEvidence[] evidence = source.Select((tick, index) => new NumericTickEvidence(
            $"detected-y-{index.ToString(CultureInfo.InvariantCulture)}",
            tick.ScreenY,
            tick.Value,
            tick.Confidence)).ToArray();
        LinearTransformFitResult fit = RobustCalibration.FitY(evidence);
        if (!fit.IsValid || fit.Transform is not { } transform)
        {
            failure = evidence.Length < 2 ? "insufficient_numeric_y_ticks" : "robust_y_calibration_not_valid";
            return false;
        }
        intercept = transform.Intercept;
        slope = transform.Slope;
        return true;
    }

    private sealed class AggregateAccumulator
    {
        private int _parsed;
        private int _projectsWithCalibration;
        private int _eligibleYTickRegions;
        private int _rejectedYTickRegions;
        private int _numericYTicks;
        private int _projectsWithTwoNumericTicks;
        private int _projectsWithTruthPoints;
        private long _anchors;
        private long _curves;
        private long _truthPoints;
        private long _calibratedTruthPoints;
        private long _pointsWithinFiveUnits;
        private long _pixelAnchorCount;
        private double _pixelAnchorErrorSum;
        private double _pixelAnchorErrorMaximum;
        private double _graphYAbsoluteErrorSum;
        private double _graphYAbsoluteErrorMaximum;
        private readonly Dictionary<string, int> _sourceFailures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _candidateFailures = new(StringComparer.Ordinal);

        internal void RecordTruth(EngaugeDigWholeWorkflowTruth truth)
        {
            _parsed++;
            _anchors += truth.Anchors.Count;
            _curves += truth.Curves.Count;
            long points = truth.Curves.Sum(static curve => (long)curve.Points.Count);
            _truthPoints += points;
            if (points > 0) _projectsWithTruthPoints++;
        }

        internal void RecordSourceFailure(string kind) => Increment(_sourceFailures, kind);

        internal void RecordCandidateFailure(string kind) => Increment(_candidateFailures, kind);

        internal void RecordCandidate(EngaugeDigWholeWorkflowTruth truth, OcrResult result)
        {
            if (!result.Succeeded)
            {
                Increment(_candidateFailures, "ocr_result_failure");
                return;
            }
            OcrRegion[] yTickRegions = result.Regions.Where(static region => region.Role == OcrTextRole.YTick).ToArray();
            OcrRegion[] eligibleYTickRegions = yTickRegions
                .Where(static region => region.ReviewStatus != OcrReviewStatus.Rejected)
                .ToArray();
            _eligibleYTickRegions += eligibleYTickRegions.Length;
            _rejectedYTickRegions += yTickRegions.Length - eligibleYTickRegions.Length;
            var numeric = new List<(double ScreenY, double Value, double Confidence)>();
            foreach (OcrRegion region in eligibleYTickRegions)
            {
                NumericTickEvidence? evidence = CreateYTickEvidence(
                    $"detected-y-{numeric.Count.ToString(CultureInfo.InvariantCulture)}",
                    region.Role,
                    region.ReviewStatus,
                    region.Text,
                    region.Confidence,
                    region.Polygon.Bounds.Center.Y);
                if (evidence is not null)
                    numeric.Add((evidence.PixelPosition, evidence.Value, evidence.Confidence));
            }
            _numericYTicks += numeric.Count;
            if (numeric.Count >= 2) _projectsWithTwoNumericTicks++;
            if (!TryFitTickCalibration(numeric.ToArray(), out double intercept, out double slope, out string failure))
            {
                Increment(_candidateFailures, failure);
                return;
            }

            _projectsWithCalibration++;
            foreach (EngaugeDigAxisAnchor anchor in truth.Anchors)
            {
                double predictedY = (anchor.GraphY - intercept) / slope;
                double error = Math.Abs(predictedY - anchor.ScreenY);
                _pixelAnchorErrorSum += error;
                _pixelAnchorErrorMaximum = Math.Max(_pixelAnchorErrorMaximum, error);
                _pixelAnchorCount++;
            }
            foreach (EngaugeDigCurve curve in truth.Curves)
            {
                foreach (EngaugeDigCurvePoint point in curve.Points)
                {
                    double error = Math.Abs((intercept + (slope * point.ScreenY)) - point.GraphY);
                    _graphYAbsoluteErrorSum += error;
                    _graphYAbsoluteErrorMaximum = Math.Max(_graphYAbsoluteErrorMaximum, error);
                    _calibratedTruthPoints++;
                    if (error <= 5) _pointsWithinFiveUnits++;
                }
            }
        }

        internal EvaluationReport Finish(int denominator) => new(
            denominator,
            _parsed,
            _sourceFailures.Values.Sum(),
            _projectsWithCalibration,
            _candidateFailures.Values.Sum(),
            _anchors,
            _curves,
            _truthPoints,
            _projectsWithTruthPoints,
            _eligibleYTickRegions,
            _rejectedYTickRegions,
            _numericYTicks,
            _projectsWithTwoNumericTicks,
            _calibratedTruthPoints,
            _pointsWithinFiveUnits,
            _projectsWithCalibration / (double)Math.Max(1, denominator),
            _pixelAnchorCount,
            _pixelAnchorErrorCountMean(),
            AvailableMaximum(_pixelAnchorErrorMaximum, _pixelAnchorCount),
            _calibratedTruthPoints == 0 ? null : _graphYAbsoluteErrorSum / _calibratedTruthPoints,
            AvailableMaximum(_graphYAbsoluteErrorMaximum, _calibratedTruthPoints),
            _sourceFailures,
            _candidateFailures);

        private double? _pixelAnchorErrorCountMean() =>
            _pixelAnchorCount == 0 ? null : _pixelAnchorErrorSum / _pixelAnchorCount;

        private static void Increment(Dictionary<string, int> values, string key) =>
            values[key] = values.GetValueOrDefault(key) + 1;
    }

    private sealed record SplitSelection(
        int ProjectCount,
        int StudyCount,
        int TrainProjectCount,
        int DevelopmentProjectCount,
        string AssignmentSha256,
        string SelectedInventorySha256,
        IReadOnlyList<FrozenRealCorpusProject> DevelopmentProjects);

    private sealed record ExecutionReceipt(
        string SourceSha256,
        string ProjectSha256,
        IReadOnlyDictionary<string, string> LoadedAssemblySha256);

    private sealed class CandidateRuntime(InferenceRuntime runtime, OcrV8ProductionCompositionPipeline pipeline)
        : IAsyncDisposable
    {
        internal OcrV8ProductionCompositionPipeline Pipeline { get; } = pipeline;

        public ValueTask DisposeAsync() => runtime.DisposeAsync();
    }

    private sealed record PreflightReport(
        string Schema,
        string Status,
        int SelectedProjectCount,
        int StudyCount,
        int TrainProjectCount,
        int DevelopmentProjectCount,
        string AssignmentSha256,
        string SelectedInventorySha256,
        string PartitionRule,
        bool FrozenAssignmentVerified,
        bool CandidatePayloadsVerified,
        bool PrivatePathsOutput,
        bool CaseLevelOutput,
        bool ProductionApproved,
        string Scope,
        string SourceSha256,
        string ProjectSha256,
        IReadOnlyDictionary<string, string> LoadedAssemblySha256);

    private sealed record EvaluationReport(
        int DevelopmentProjectDenominator,
        int ProjectsParsed,
        int SourceFailures,
        int CalibrationSuccesses,
        int CandidateFailures,
        long GroundTruthAxisAnchorCount,
        long GroundTruthCurveCount,
        long GroundTruthSampledPointCount,
        int ProjectsWithGroundTruthPoints,
        int EligibleYTickRegionCount,
        int RejectedYTickRegionCount,
        int NumericYTickCount,
        int ProjectsWithAtLeastTwoNumericYTicks,
        long CalibratedGroundTruthPointCount,
        long PointsWithinFiveGraphUnits,
        double CalibrationCoverage,
        long PixelAnchorMatchCount,
        double? MeanAxisAnchorErrorPixels,
        double? MaximumAxisAnchorErrorPixels,
        double? MeanAbsoluteYErrorGraphUnits,
        double? MaximumAbsoluteYErrorGraphUnits,
        IReadOnlyDictionary<string, int> SourceFailureKinds,
        IReadOnlyDictionary<string, int> CandidateFailureKinds);

    private sealed record CandidateReport(
        string Schema,
        string EvaluationMode,
        string CandidateMode,
        string CompositionId,
        string ConfigurationFingerprint,
        IReadOnlyDictionary<string, string> PayloadSha256,
        IReadOnlyDictionary<string, string> PayloadVersions,
        string InferenceProvider,
        int SelectedProjectCount,
        int StudyCount,
        int TrainProjectCount,
        int DevelopmentProjectCount,
        string AssignmentSha256,
        string SelectedInventorySha256,
        string DevelopmentContentSha256,
        string PartitionRule,
        bool FrozenAssignmentVerified,
        bool PreviouslyExposedDevelopmentData,
        bool SealedPayloadsRead,
        bool CaseLevelOutput,
        bool ProductionApproved,
        bool SealedAcceptance,
        string GeometryOracle,
        string CalibrationMethod,
        string LabelLimitations,
        string SourceSha256,
        string ProjectSha256,
        IReadOnlyDictionary<string, string> LoadedAssemblySha256,
        EvaluationReport Aggregate);

    private sealed record FailureReport(
        string Schema,
        string Status,
        string FailureKind,
        bool PreviouslyExposedDevelopmentData,
        bool SealedPayloadsRead,
        bool ProductionApproved);
}
