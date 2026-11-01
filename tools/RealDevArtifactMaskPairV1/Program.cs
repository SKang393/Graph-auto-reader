// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
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

namespace GraphReader.RealDevArtifactMaskPairV1;

internal static class Program
{
    private const int ExpectedSelectedProjects = 120;
    private const int ExpectedTrainProjects = 104;
    private const int ExpectedDevelopmentProjects = 16;
    private const int ExpectedStudies = 28;
    private const string PartitionRule = "sha256:graphreader-engauge-real-dev-study-v1:first-byte-lt-204";
    private const string ProtocolRelativePath = "tools/RealDevArtifactMaskPairV1/protocol.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static async Task<int> Main(string[] args)
    {
        bool explicitOptIn = args.Contains("--explicit-opt-in", StringComparer.Ordinal);
        bool preflight = args.Contains("--preflight", StringComparer.Ordinal);
        int candidateIndex = Array.IndexOf(args, "--candidate-mode");
        bool candidateMode = candidateIndex >= 0 && candidateIndex + 1 < args.Length &&
            args[candidateIndex + 1] == "v8-cpu";
        if (!explicitOptIn || (!preflight && !candidateMode) || (preflight && candidateMode) ||
            args.Any(argument => argument is not ("--explicit-opt-in" or "--preflight" or "--candidate-mode" or "v8-cpu")))
        {
            Console.Error.WriteLine("REAL_DEV_ARTIFACT_MASK_EXPLICIT_MODE_REQUIRED");
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
            Console.Error.WriteLine("REAL_DEV_ARTIFACT_MASK_PREFLIGHT_FAILED " + exception.GetType().Name);
            return 2;
        }

        try
        {
            JsonElement protocol = LoadAndValidateProtocol(repositoryRoot);
            FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(
                Path.Combine(repositoryRoot, "data", "manual data"), CancellationToken.None);
            FrozenRealCorpusSelection selection = FrozenRealCorpusInventory.Load(
                Path.Combine(repositoryRoot, "data", "manual data"),
                FrozenRealCorpusInventory.RealDev,
                CancellationToken.None);
            IReadOnlyList<FrozenRealCorpusProject> selected = selector.EnumerateSelectedProjects(CancellationToken.None);
            SplitSelection split = SelectDevelopment(selector, selection, selected);
            OcrV8ProductionPayloadSet payloads = LoadAndValidatePayloads(repositoryRoot);
            string openCvRuntimePath = ResolveOpenCvRuntimePath();
            string openCvRuntimeSha256 = HashFile(openCvRuntimePath);

            if (preflight)
            {
                WriteReport(outputDirectory, new
                {
                    schema = "graphreader.real-dev-artifact-mask-pair-preflight.v1",
                    status = "pass",
                    candidate_mode = "v8-cpu",
                    selected_projects = split.ProjectCount,
                    studies = split.StudyCount,
                    train_projects_metadata_only = split.TrainProjectCount,
                    development_projects = split.DevelopmentProjectCount,
                    sealed_count_metadata_only = selection.RealSealedCount,
                    development_assignment_sha256 = split.AssignmentSha256,
                    selected_inventory_sha256 = split.SelectedInventorySha256,
                    partition_rule = PartitionRule,
                    candidate_ocr_payloads_verified = true,
                    provider_policy = "cpu_only",
                    source_payloads_opened = false,
                    training_performed = false,
                    sealed_payloads_opened = false,
                    open_cv_runtime_sha256 = openCvRuntimeSha256,
                    protocol_sha256 = HashFile(Path.Combine(repositoryRoot, ProtocolRelativePath)),
                    model_payload_sha256 = PayloadHashes(payloads),
                });
                Console.WriteLine("REAL_DEV_ARTIFACT_MASK_PREFLIGHT_PASS");
                return 0;
            }

            await using CandidateRuntime candidateRuntime = CreateCandidate(payloads);
            var axisAdapter = new ProductionAxisGeometryAdapter(openCvRuntimeSha256, isApproved: false);
            var baselineMaskProvider = new RasterResidualArtifactMaskProvider();
            var candidateMaskProvider = new RasterResidualArtifactMaskProvider(CreateFrozenCandidateStructuralProvider());
            var aggregate = new AggregateAccumulator();
            using var corpusDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            corpusDigest.AppendData(Encoding.UTF8.GetBytes("graphreader.real-dev-artifact-mask-pair.v1\n"));
            corpusDigest.AppendData(Encoding.UTF8.GetBytes(split.AssignmentSha256 + "\n"));
            var uniqueImages = new HashSet<string>(StringComparer.Ordinal);

            foreach (FrozenRealCorpusProject project in split.DevelopmentProjects)
            {
                EngaugeDigWholeWorkflowTruth truth;
                try
                {
                    truth = ReadSelectedProject(selector, project);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
                {
                    aggregate.RecordSourceFailure(exception.GetType().Name);
                    continue;
                }

                aggregate.RecordSource(truth.TruthCase.Points.Count);
                uniqueImages.Add(truth.SourceImageSha256);
                corpusDigest.AppendData(Encoding.UTF8.GetBytes(truth.ProjectSha256 + "\n" + truth.SourceImageSha256 + "\n"));

                try
                {
                    CaseObservation observation = await EvaluateAsync(
                        truth,
                        axisAdapter,
                        candidateRuntime.Pipeline,
                        baselineMaskProvider,
                        candidateMaskProvider).ConfigureAwait(false);
                    aggregate.Record(observation);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
                {
                    aggregate.RecordEvaluationFailure(exception.GetType().Name);
                }
            }

            _ = selector.EnumerateSelectedProjects(CancellationToken.None);
            string[] nativeModules = ReadLoadedOpenCvModulePaths();
            string? loadedOpenCvSha = nativeModules.Length == 1 && File.Exists(nativeModules[0])
                ? HashFile(nativeModules[0])
                : null;
            if (loadedOpenCvSha is not null && !string.Equals(loadedOpenCvSha, openCvRuntimeSha256, StringComparison.Ordinal))
                throw new InvalidDataException("AXIS_NATIVE_RUNTIME_HASH_MISMATCH");

            var report = new CandidateReport(
                Schema: "graphreader.real-dev-artifact-mask-pair-aggregate.v1",
                Scope: "development_diagnostic_component_only",
                RuntimeComposition: "v8-cpu-plus-production-axis-candidate",
                OcrCompositionId: candidateRuntime.Pipeline.CompositionId,
                OcrConfigurationFingerprint: candidateRuntime.Pipeline.ConfigurationFingerprint,
                OcrPayloadSha256: PayloadHashes(payloads),
                InferenceProvider: InferenceProvider.Cpu.ToString(),
                SelectedProjects: split.ProjectCount,
                SelectedStudies: split.StudyCount,
                TrainProjectMetadataCount: split.TrainProjectCount,
                DevelopmentProjectCount: split.DevelopmentProjectCount,
                AssignmentSha256: split.AssignmentSha256,
                SelectedInventorySha256: split.SelectedInventorySha256,
                DevelopmentContentSha256: Convert.ToHexStringLower(corpusDigest.GetHashAndReset()),
                UniqueDevelopmentImageCount: uniqueImages.Count,
                PartitionRule: PartitionRule,
                TrainPayloadsOpened: false,
                SealedPayloadsOpened: false,
                TrainingPerformed: false,
                ProductionApproval: false,
                AxisAndOcrInputContract: "production OpenCV axis candidate from original raster; V8 detector receives the original unmasked raster and axis-derived plot bounds; Engauge points are evaluation-only",
                MaskSeedProvenance: "production DetectionMaskComposer.BuildSeedMasks method invoked by exact source-bound reflection because V8 does not expose ProductionOcrEvidence receipts; OCR/axis outputs and mask seed bytes remain identical for each baseline/candidate pair",
                MarkerPointMetricLimitations: "nearest-pixel sampling of Engauge point centers; no labeled negatives or complete marker masks, so no precision, recall, or acceptance claim",
                OpenCvRuntimeSha256: HashFile(openCvRuntimePath),
                LoadedOpenCvNativeModuleCount: ReadLoadedOpenCvModulePaths().Length,
                SourceSha256: HashSourceSet(repositoryRoot),
                RunnerAssemblySha256: HashFile(Assembly.GetExecutingAssembly().Location),
                Metrics: aggregate.Finish());
            WriteReport(outputDirectory, report);
            Console.WriteLine("REAL_DEV_ARTIFACT_MASK_PAIRED_REPORT_WRITTEN");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            WriteReport(outputDirectory, new
            {
                schema = "graphreader.real-dev-artifact-mask-pair-failure.v1",
                status = "failed",
                failure_kind = exception.GetType().Name,
                training_performed = false,
                sealed_payloads_opened = false,
            });
            Console.Error.WriteLine("REAL_DEV_ARTIFACT_MASK_FAILED " + exception.GetType().Name);
            return 1;
        }
    }

    private static async Task<CaseObservation> EvaluateAsync(
        EngaugeDigWholeWorkflowTruth truth,
        ProductionAxisGeometryAdapter axisAdapter,
        OcrV8ProductionCompositionPipeline ocrPipeline,
        RasterResidualArtifactMaskProvider baselineProvider,
        RasterResidualArtifactMaskProvider candidateProvider)
    {
        byte[] imageBytes = truth.CopyImageBytes();
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
            imageBytes);
        ProductionDecodedRaster raster = new ProductionRasterFrameDecoder().Decode(request, CancellationToken.None);
        if (!string.Equals(raster.InputSha256, truth.SourceImageSha256, StringComparison.Ordinal) ||
            raster.Width != truth.SourceWidth || raster.Height != truth.SourceHeight)
            throw new InvalidDataException("DEVELOPMENT_IMAGE_DECODE_INVALID");

        ProductionAxisGeometryEvidence axis = await axisAdapter.DetectForCandidateEvaluationAsync(
            request, CancellationToken.None).ConfigureAwait(false);
        OcrRectangle plot = AxisPlotBounds(axis.Geometry.PlotPolygon);
        OcrResult ocr = await ocrPipeline.RecognizeAsync(new OcrRequest(
            request.ProjectId.ToString("D"),
            imported.PanelId.ToString("D"),
            truth.SourceImageSha256,
            raster.CreateOcrImage(),
            plot), CancellationToken.None).ConfigureAwait(false);
        if (!ocr.Succeeded)
            throw new InvalidDataException("V8_CANDIDATE_OCR_FAILED");

        ProductionDetectionMaskSeed seed = BuildProductionMaskSeed(raster, axis, ocr);
        RasterResidualArtifactMaskResult baseline = await baselineProvider.AnalyzeAsync(
            raster, axis, ocr, seed, CancellationToken.None).ConfigureAwait(false);
        RasterResidualArtifactMaskResult candidate = await candidateProvider.AnalyzeAsync(
            raster, axis, ocr, seed, CancellationToken.None).ConfigureAwait(false);

        long baselineCentersMasked = 0;
        long candidateCentersMasked = 0;
        long baselineMaskedToCandidateClear = 0;
        long baselineClearToCandidateMasked = 0;
        long points = 0;
        foreach (WholeWorkflowTruthPoint point in truth.TruthCase.Points)
        {
            int x = checked((int)Math.Round(point.SourcePixelX, MidpointRounding.AwayFromZero));
            int y = checked((int)Math.Round(point.SourcePixelY, MidpointRounding.AwayFromZero));
            if ((uint)x >= (uint)raster.Width || (uint)y >= (uint)raster.Height)
                throw new InvalidDataException("ENGAUGE_POINT_OUTSIDE_DECODED_IMAGE");
            bool before = baseline.Mask.Span[(y * raster.Width) + x] >= 0.5f;
            bool after = candidate.Mask.Span[(y * raster.Width) + x] >= 0.5f;
            points++;
            if (before) baselineCentersMasked++;
            if (after) candidateCentersMasked++;
            if (before && !after) baselineMaskedToCandidateClear++;
            if (!before && after) baselineClearToCandidateMasked++;
        }

        var categories = Enum.GetValues<RasterResidualArtifactCategory>();
        Dictionary<string, int> baselineRegions = categories.ToDictionary(
            static category => category.ToString(),
            category => baseline.Regions.Count(region => region.Category == category),
            StringComparer.Ordinal);
        Dictionary<string, int> candidateRegions = categories.ToDictionary(
            static category => category.ToString(),
            category => candidate.Regions.Count(region => region.Category == category),
            StringComparer.Ordinal);
        long baselineCoverage = CountMaskedPixels(baseline.Mask.Span);
        long candidateCoverage = CountMaskedPixels(candidate.Mask.Span);
        return new CaseObservation(
            raster.Width,
            raster.Height,
            points,
            baselineCentersMasked,
            candidateCentersMasked,
            baselineMaskedToCandidateClear,
            baselineClearToCandidateMasked,
            checked((long)raster.Width * raster.Height),
            baselineCoverage,
            candidateCoverage,
            baselineRegions,
            candidateRegions);
    }

    private static ProductionDetectionMaskSeed BuildProductionMaskSeed(
        ProductionDecodedRaster raster,
        ProductionAxisGeometryEvidence axis,
        OcrResult ocr)
    {
        MethodInfo method = typeof(ProductionDetectionMaskComposer).GetMethod(
            "BuildSeedMasks", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("ProductionDetectionMaskComposer.BuildSeedMasks");
        try
        {
            return (ProductionDetectionMaskSeed)(method.Invoke(null, [raster, axis, ocr, CancellationToken.None])
                ?? throw new InvalidOperationException("PRODUCTION_MASK_SEED_EMPTY"));
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static IPreOcrStructuralProbabilityProvider CreateFrozenCandidateStructuralProvider()
    {
        Assembly testAssembly = Assembly.Load("GraphReader.App.Tests");
        Type testType = testAssembly.GetType("GraphReader.App.Tests.ArtifactMaskOutlineCoreCandidateTests", throwOnError: true)!;
        Type candidateType = testType.GetNestedType(
            "OutlineCoreCandidateProbabilityProvider", BindingFlags.NonPublic)
            ?? throw new MissingMemberException("Frozen synthetic artifact-mask candidate provider is unavailable.");
        return (IPreOcrStructuralProbabilityProvider)(Activator.CreateInstance(candidateType, nonPublic: true)
            ?? throw new InvalidOperationException("Frozen synthetic artifact-mask candidate could not be constructed."));
    }

    private static SplitSelection SelectDevelopment(
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
            throw new InvalidDataException("LOCKED_REAL_DEV_PARTITION_COUNTS_MISMATCH");

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

    private static OcrV8ProductionPayloadSet LoadAndValidatePayloads(string root)
    {
        ModelIdentity Model(string id, string version, string sha256, string relativePath) =>
            new(id, version, sha256, Path.GetFullPath(Path.Combine(root, relativePath)));
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

    private static string ReadAlphabet(string path)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        int start = Array.FindIndex(lines, static line => line.Trim() == "character_dict:");
        if (start < 0) throw new InvalidDataException("OCR_ALPHABET_SECTION_MISSING");
        var result = new StringBuilder();
        for (int index = start + 1; index < lines.Length && lines[index].StartsWith("  - ", StringComparison.Ordinal); index++)
        {
            string value = lines[index][4..].Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            result.Append(value);
        }
        if (!result.ToString().Contains(' ', StringComparison.Ordinal)) result.Append(' ');
        if (!string.Equals(HashBytes(Encoding.UTF8.GetBytes(result.ToString())),
                OcrV8ProductionCompositionFactory.OfficialAlphabetSha256, StringComparison.Ordinal))
            throw new InvalidDataException("OCR_ALPHABET_HASH_MISMATCH");
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

    private static OcrRectangle AxisPlotBounds(PlotPolygon polygon)
    {
        double left = polygon.Points.Min(static point => point.X);
        double top = polygon.Points.Min(static point => point.Y);
        double right = polygon.Points.Max(static point => point.X);
        double bottom = polygon.Points.Max(static point => point.Y);
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom) ||
            right <= left || bottom <= top)
            throw new InvalidDataException("AXIS_PLOT_BOUNDS_INVALID");
        return new OcrRectangle(left, top, right - left, bottom - top);
    }

    private static long CountMaskedPixels(ReadOnlySpan<float> mask)
    {
        long count = 0;
        foreach (float value in mask)
            if (value >= 0.5f) count++;
        return count;
    }

    private static JsonElement LoadAndValidateProtocol(string root)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, ProtocolRelativePath)));
        JsonElement protocol = document.RootElement.Clone();
        if (protocol.GetProperty("schema").GetString() != "graphreader.real-dev-artifact-mask-pair.v1" ||
            protocol.GetProperty("status").GetString() != "preregistered-before-run" ||
            protocol.GetProperty("split").GetProperty("development_projects").GetInt32() != ExpectedDevelopmentProjects ||
            protocol.GetProperty("candidate_ocr").GetProperty("provider").GetString() != "cpu_only" ||
            protocol.GetProperty("scope").GetProperty("sealed_payload_reads").GetBoolean() ||
            protocol.GetProperty("scope").GetProperty("training").GetBoolean())
            throw new InvalidDataException("REAL_DEV_ARTIFACT_MASK_PROTOCOL_INVALID");

        JsonElement identity = protocol.GetProperty("identity");
        string own = Path.Combine(root, "tools", "RealDevArtifactMaskPairV1", "Program.cs");
        string project = Path.Combine(root, "tools", "RealDevArtifactMaskPairV1", "RealDevArtifactMaskPairV1.csproj");
        string candidate = Path.Combine(root, "tests", "GraphReader.App.Tests", "ArtifactMaskOutlineCoreCandidateTests.cs");
        if (identity.GetProperty("program_sha256").GetString() != HashFile(own) ||
            identity.GetProperty("project_sha256").GetString() != HashFile(project) ||
            identity.GetProperty("candidate_source_sha256").GetString() != HashFile(candidate) ||
            identity.GetProperty("app_assembly_sha256").GetString() != HashFile(typeof(RasterResidualArtifactMaskProvider).Assembly.Location) ||
            identity.GetProperty("ocr_assembly_sha256").GetString() != HashFile(typeof(OcrV8ProductionCompositionFactory).Assembly.Location) ||
            identity.GetProperty("axis_assembly_sha256").GetString() != HashFile(typeof(AxisGeometryDetector).Assembly.Location) ||
            identity.GetProperty("app_test_assembly_sha256").GetString() != HashFile(
                Path.Combine(AppContext.BaseDirectory, "GraphReader.App.Tests.dll")) ||
            identity.GetProperty("runner_assembly_sha256").GetString() != HashFile(Assembly.GetExecutingAssembly().Location))
            throw new InvalidDataException("REAL_DEV_ARTIFACT_MASK_IDENTITY_MISMATCH");
        return protocol;
    }

    private static Dictionary<string, string> PayloadHashes(OcrV8ProductionPayloadSet payloads) => new(StringComparer.Ordinal)
    {
        ["detector"] = HashFile(payloads.Detector.FilePath),
        ["official_recognizer"] = HashFile(payloads.OfficialRecognizer.FilePath),
        ["numeric_recognizer"] = HashFile(payloads.NumericRecognizer.FilePath),
        ["ambiguity_recognizer"] = HashFile(payloads.AmbiguityRecognizer.FilePath),
        ["official_alphabet"] = HashBytes(Encoding.UTF8.GetBytes(payloads.OfficialAlphabet)),
    };

    private static Dictionary<string, string> HashSourceSet(string root)
    {
        string[] relative =
        [
            "tools/RealDevArtifactMaskPairV1/Program.cs",
            "tools/RealDevArtifactMaskPairV1/RealDevArtifactMaskPairV1.csproj",
            "tests/GraphReader.App.Tests/ArtifactMaskOutlineCoreCandidateTests.cs",
            "tests/GraphReader.App.Tests/ArtifactMaskOutlineCoreBroadV2CandidateTests.cs",
            "src/GraphReader.App/Integration/Workflow/ProductionDetectionMaskComposer.cs",
            "src/GraphReader.App/Integration/Workflow/RasterResidualArtifactMaskProvider.cs",
            "src/GraphReader.Ocr/RasterPreOcrStructuralProbabilityProvider.cs",
            "src/GraphReader.Ocr/OcrV8ProductionCompositionFactory.cs",
            "src/GraphReader.App/Integration/Workflow/ProductionAxisGeometryAdapter.cs",
            "tools/GraphReader.RealAcceptance.Ocr/FrozenRealCorpusInventory.cs",
            "tools/GraphReader.RealAcceptance.Ocr/FrozenDevTrainingSelector.cs",
            "tools/GraphReader.RealAcceptance.Ocr/EngaugeDevSourceMaterializer.cs",
            "tools/GraphReader.RealAcceptance.Ocr/EngaugeDigWholeWorkflowTruthAdapter.cs",
        ];
        return relative.ToDictionary(static path => Path.GetFileName(path), path => HashFile(Path.Combine(root, path)), StringComparer.Ordinal);
    }

    private static string[] ReadLoadedOpenCvModulePaths() =>
        Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Where(static module => string.Equals(module.ModuleName, "OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase))
            .Select(static module => module.FileName ?? string.Empty)
            .Where(static path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string ResolveOpenCvRuntimePath()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "OpenCvSharpExtern.dll"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "OpenCvSharpExtern.dll"),
        ];
        string[] existing = candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (existing.Length != 1)
            throw new FileNotFoundException("OPEN_CV_NATIVE_RUNTIME_NOT_UNIQUE");
        return existing[0];
    }

    private static string FindRepositoryRoot(string start)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tools", "GraphReader.RealAcceptance.Ocr")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("REPOSITORY_ROOT_NOT_FOUND");
    }

    private static string CreateRunDirectory(string root)
    {
        string artifactRoot = Path.GetFullPath(Path.Combine(root, "artifacts"));
        string outputRoot = Path.GetFullPath(Path.Combine(artifactRoot, "real-dev-artifact-mask-pair-v1"));
        if (!IsUnder(outputRoot, artifactRoot)) throw new InvalidDataException("OUTPUT_ROOT_OUTSIDE_ARTIFACTS");
        Directory.CreateDirectory(outputRoot);
        if ((File.GetAttributes(artifactRoot) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("OUTPUT_ROOT_REPARSE_POINT_REJECTED");
        string run = Path.Combine(outputRoot,
            "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
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

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void WriteReport(string directory, object report) =>
        File.WriteAllText(Path.Combine(directory, "aggregate-report.json"),
            JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));

    private sealed class CandidateRuntime(
        InferenceRuntime runtime,
        OcrV8ProductionCompositionPipeline pipeline) : IAsyncDisposable
    {
        internal OcrV8ProductionCompositionPipeline Pipeline { get; } = pipeline;
        public ValueTask DisposeAsync() => runtime.DisposeAsync();
    }

    private sealed record SplitSelection(
        int ProjectCount,
        int StudyCount,
        int TrainProjectCount,
        int DevelopmentProjectCount,
        string AssignmentSha256,
        string SelectedInventorySha256,
        IReadOnlyList<FrozenRealCorpusProject> DevelopmentProjects);

    private sealed record CaseObservation(
        int Width,
        int Height,
        long PointCount,
        long BaselinePointCentersMasked,
        long CandidatePointCentersMasked,
        long BaselineMaskedToCandidateClear,
        long BaselineClearToCandidateMasked,
        long PixelCount,
        long BaselineMaskedPixels,
        long CandidateMaskedPixels,
        IReadOnlyDictionary<string, int> BaselineRegions,
        IReadOnlyDictionary<string, int> CandidateRegions);

    private sealed record CandidateReport(
        string Schema,
        string Scope,
        string RuntimeComposition,
        string OcrCompositionId,
        string OcrConfigurationFingerprint,
        IReadOnlyDictionary<string, string> OcrPayloadSha256,
        string InferenceProvider,
        int SelectedProjects,
        int SelectedStudies,
        int TrainProjectMetadataCount,
        int DevelopmentProjectCount,
        string AssignmentSha256,
        string SelectedInventorySha256,
        string DevelopmentContentSha256,
        int UniqueDevelopmentImageCount,
        string PartitionRule,
        bool TrainPayloadsOpened,
        bool SealedPayloadsOpened,
        bool TrainingPerformed,
        bool ProductionApproval,
        string AxisAndOcrInputContract,
        string MaskSeedProvenance,
        string MarkerPointMetricLimitations,
        string OpenCvRuntimeSha256,
        int LoadedOpenCvNativeModuleCount,
        IReadOnlyDictionary<string, string> SourceSha256,
        string RunnerAssemblySha256,
        object Metrics);

    private sealed class AggregateAccumulator
    {
        private readonly Dictionary<string, long> sourceFailures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> evaluationFailures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> baselineRegionCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> candidateRegionCounts = new(StringComparer.Ordinal);
        private long sourceProjectsRead;
        private long sourceManualPoints;
        private long pairedProjects;
        private long pairedPoints;
        private long baselineMaskedCenters;
        private long candidateMaskedCenters;
        private long baselineMaskedToCandidateClear;
        private long baselineClearToCandidateMasked;
        private long pixels;
        private long baselineMaskedPixels;
        private long candidateMaskedPixels;

        internal void RecordSource(int points)
        {
            sourceProjectsRead++;
            sourceManualPoints += points;
        }

        internal void RecordSourceFailure(string kind) => Increment(sourceFailures, kind);

        internal void RecordEvaluationFailure(string kind) => Increment(evaluationFailures, kind);

        internal void Record(CaseObservation item)
        {
            pairedProjects++;
            pairedPoints += item.PointCount;
            baselineMaskedCenters += item.BaselinePointCentersMasked;
            candidateMaskedCenters += item.CandidatePointCentersMasked;
            baselineMaskedToCandidateClear += item.BaselineMaskedToCandidateClear;
            baselineClearToCandidateMasked += item.BaselineClearToCandidateMasked;
            pixels += item.PixelCount;
            baselineMaskedPixels += item.BaselineMaskedPixels;
            candidateMaskedPixels += item.CandidateMaskedPixels;
            foreach ((string category, int count) in item.BaselineRegions) Increment(baselineRegionCounts, category, count);
            foreach ((string category, int count) in item.CandidateRegions) Increment(candidateRegionCounts, category, count);
        }

        internal object Finish() => new
        {
            projects_read = sourceProjectsRead,
            source_read_failures = sourceFailures,
            projects_with_paired_results = pairedProjects,
            paired_evaluation_failures = evaluationFailures,
            manual_engauge_point_centers = sourceManualPoints,
            paired_point_centers = pairedPoints,
            baseline_masked_point_centers = baselineMaskedCenters,
            candidate_masked_point_centers = candidateMaskedCenters,
            candidate_unmasked_centers_masked_by_baseline = baselineMaskedToCandidateClear,
            candidate_newly_masked_centers = baselineClearToCandidateMasked,
            pixel_denominator = pixels,
            baseline_masked_pixel_count = baselineMaskedPixels,
            candidate_masked_pixel_count = candidateMaskedPixels,
            baseline_mask_coverage = pixels == 0 ? (double?)null : (double)baselineMaskedPixels / pixels,
            candidate_mask_coverage = pixels == 0 ? (double?)null : (double)candidateMaskedPixels / pixels,
            baseline_region_counts = Enum.GetValues<RasterResidualArtifactCategory>()
                .ToDictionary(static category => category.ToString(), category => baselineRegionCounts.GetValueOrDefault(category.ToString()), StringComparer.Ordinal),
            candidate_region_counts = Enum.GetValues<RasterResidualArtifactCategory>()
                .ToDictionary(static category => category.ToString(), category => candidateRegionCounts.GetValueOrDefault(category.ToString()), StringComparer.Ordinal),
            all_masked_center_transitions_are_aggregates_only = true,
        };

        private static void Increment(Dictionary<string, long> target, string key, long amount = 1) =>
            target[key] = target.GetValueOrDefault(key) + amount;
    }
}
