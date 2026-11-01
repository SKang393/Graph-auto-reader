// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Ocr;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.RealDevDetectorSubfloorCensusV1;

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
        if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
        if (args.Length != 3 || args[0] != "--candidate-mode" || args[1] != "v8-detector-cpu" ||
            args[2] != "--explicit-opt-in" || IsContinuousIntegration())
        {
            Console.Error.WriteLine("REAL_DEV_DETECTOR_SUBFLOOR_EXPLICIT_CANDIDATE_MODE_REQUIRED");
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
            Console.Error.WriteLine("REAL_DEV_DETECTOR_SUBFLOOR_PREFLIGHT_FAILED " + exception.GetType().Name);
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
            ModelIdentity detectorModel = LoadDetector(root);
            await using var runtime = CreateRuntime();
            LocalOnnxProposalTextRegionDetectorOptions detectorOptions = CreateDetectorOptions(detectorModel);
            var detector = new LocalOnnxProposalTextRegionDetector(runtime, detectorOptions);
            PrivateDetectorApi privateApi = CreatePrivateDetectorApi(detectorOptions);
            var accumulator = new SubfloorAccumulator();
            using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sourceHash.AppendData(Encoding.UTF8.GetBytes("graphreader.real-dev-detector-subfloor-census-v1\n"));
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
                    accumulator.RecordFailure(source: true, exception.GetType().Name);
                    continue;
                }

                sourceHash.AppendData(Encoding.UTF8.GetBytes(
                    project.AnonymizedCaseId + "\n" + truth.ProjectSha256 + "\n" + truth.SourceImageSha256 + "\n"));
                try
                {
                    OcrImage image = DecodeProductionImage(truth);
                    OcrRectangle plot = PlotBounds(truth.Anchors, image.Width, image.Height);
                    IReadOnlyList<OcrDetectedRegion> publicProposals = await detector
                        .DetectProposalsAsync(image, CancellationToken.None).ConfigureAwait(false);
                    ScoredPanel scored = await ScoreAllPrivateCandidatesAsync(
                        runtime, detector, detectorModel, privateApi, image, plot, publicProposals,
                        CancellationToken.None).ConfigureAwait(false);
                    accumulator.Record(scored);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
                {
                    accumulator.RecordFailure(source: false, exception.GetType().Name);
                    if (exception is ProposalParityException)
                    {
                        WriteParityFailure(output);
                        Console.Error.WriteLine("REAL_DEV_DETECTOR_SUBFLOOR_PARITY_CHECK_FAILED");
                        return 1;
                    }
                }
            }

            _ = selector.EnumerateSelectedProjects(CancellationToken.None);
            if (accumulator.ParityPanels != accumulator.CensusPanels || accumulator.ParityPanels != ExpectedDevelopmentProjects)
            {
                WriteParityFailure(output);
                Console.Error.WriteLine("REAL_DEV_DETECTOR_SUBFLOOR_PARITY_CHECK_FAILED");
                return 1;
            }

            ExecutionReceipt receipt = CaptureExecutionReceipt(root);
            AggregateReport report = BuildReport(
                detector.ConfigurationFingerprint,
                detectorModel.Sha256,
                development,
                Convert.ToHexStringLower(sourceHash.GetHashAndReset()),
                accumulator.Finish(ExpectedDevelopmentProjects),
                receipt);
            File.WriteAllText(Path.Combine(output, "aggregate-report.json"),
                JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
            Console.WriteLine("REAL_DEV_DETECTOR_SUBFLOOR_REPORT_WRITTEN");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            File.WriteAllText(Path.Combine(output, "aggregate-failure.json"), JsonSerializer.Serialize(
                new FailureReport("failed", exception.GetType().Name, true, false, false), JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false));
            Console.Error.WriteLine("REAL_DEV_DETECTOR_SUBFLOOR_FAILED " + exception.GetType().Name);
            return 1;
        }
    }

    private static async Task<ScoredPanel> ScoreAllPrivateCandidatesAsync(
        InferenceRuntime runtime,
        LocalOnnxProposalTextRegionDetector detector,
        ModelIdentity model,
        PrivateDetectorApi api,
        OcrImage image,
        OcrRectangle plot,
        IReadOnlyList<OcrDetectedRegion> publicProposals,
        CancellationToken cancellationToken)
    {
        LocalOnnxProposalTextRegionDetectorOptions options = api.Options;
        byte threshold = (byte)api.EstimateThreshold.Invoke(null, [image, options, cancellationToken])!;
        Array components = (Array)api.BuildProposals.Invoke(null, [image, threshold, options, cancellationToken])!;
        if (components.Length > options.MaximumProposals)
            throw new InvalidDataException("PRIVATE_COMPONENT_COUNT_EXCEEDS_DETECTOR_LIMIT");

        int encodedWidth = checked(options.CropWidth + options.GeometryFeatureCount);
        int valuesPerProposal = checked(options.InputChannels * options.CropHeight * encodedWidth);
        var tensor = new float[checked(components.Length * valuesPerProposal)];
        var regions = new List<ScoredProposal>(components.Length);
        for (int index = 0; index < components.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object component = components.GetValue(index)!;
            api.Encode(image, component, threshold, options,
                tensor.AsSpan(index * valuesPerProposal, valuesPerProposal));
            OcrRectangle bounds = (OcrRectangle)api.MapToOriginal.Invoke(null, [image, component])!;
            string id = (string)api.DeterministicRegionId.Invoke(null,
                [model.Sha256, bounds, LocalOnnxProposalTextRegionDetector.PostprocessingAlgorithm])!;
            regions.Add(new ScoredProposal(id, bounds, 0d, Classify(bounds, plot)));
        }

        if (components.Length > 0)
        {
            string imageSha256 = Convert.ToHexStringLower(SHA256.HashData(image.Pixels.Span));
            string transformFingerprint = (string)api.TransformFingerprint.Invoke(null, [image.OriginalToImage])!;
            string providerFingerprint = (string)api.ProviderFingerprint.Invoke(null, [options.AllowedProviders])!;
            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["configuration_sha256"] = detector.ConfigurationFingerprint,
                ["proposal_algorithm"] = LocalOnnxProposalTextRegionDetector.ProposalAlgorithm,
                ["encoding_algorithm"] = LocalOnnxProposalTextRegionDetector.EncodingAlgorithm,
                ["postprocessing_algorithm"] = LocalOnnxProposalTextRegionDetector.PostprocessingAlgorithm,
                ["proposal_count"] = components.Length,
                ["proposal_threshold"] = threshold,
                ["confidence_threshold"] = options.ConfidenceThreshold,
                ["proposal_confidence_floor"] = LocalOnnxProposalTextRegionDetector.ProposalConfidenceFloor,
                ["allowed_providers"] = providerFingerprint,
            };
            var request = new InferenceRequest(
                model,
                new InferenceInput(tensor, [components.Length, options.InputChannels, options.CropHeight, encodedWidth],
                    options.InputName, options.OutputName),
                new StageCacheMaterial(imageSha256,
                    FormattableString.Invariant($"0,0,{image.Width},{image.Height}"),
                    transformFingerprint, "ocr_proposal_detection", options.StageVersion,
                    parameters, OcrContract.Version),
                options.Timeout,
                options.AllowedProviders,
                options.BypassCache);
            InferenceResponse response = await runtime.RunAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.Succeeded || response.Execution is null || response.Execution.Output.Count != components.Length * 2 ||
                !options.AllowedProviders!.Contains(response.Execution.Provider))
                throw new InvalidDataException("PRIVATE_SCORER_INFERENCE_EVIDENCE_INVALID");

            IReadOnlyList<float> logits = response.Execution.Output;
            for (int index = 0; index < regions.Count; index++)
            {
                float score = (float)api.SoftmaxClassOne.Invoke(null,
                    [logits[index * 2], logits[(index * 2) + 1]])!;
                regions[index] = regions[index] with { Confidence = score };
            }
        }

        ScoredProposal[] aboveFloor = regions
            .Where(static proposal => proposal.Confidence >= LocalOnnxProposalTextRegionDetector.ProposalConfidenceFloor)
            .ToArray();
        EnsureExactPublicParity(aboveFloor, publicProposals);
        return new ScoredPanel(regions, aboveFloor.Length, publicProposals.Count);
    }

    private static void EnsureExactPublicParity(
        IReadOnlyList<ScoredProposal> scored,
        IReadOnlyList<OcrDetectedRegion> publicProposals)
    {
        var scoredById = scored.ToDictionary(static item => item.RegionId, StringComparer.Ordinal);
        var publicById = publicProposals.ToDictionary(static item => item.RegionId, StringComparer.Ordinal);
        if (scoredById.Count != scored.Count || publicById.Count != publicProposals.Count ||
            !scoredById.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(publicById.Keys))
            throw new ProposalParityException();
        foreach ((string id, ScoredProposal scoredProposal) in scoredById)
        {
            OcrDetectedRegion publicProposal = publicById[id];
            if (scoredProposal.Bounds != publicProposal.Polygon.Bounds ||
                scoredProposal.Confidence != publicProposal.DetectionConfidence)
                throw new ProposalParityException();
        }
    }

    private static AggregateReport BuildReport(
        string detectorConfigurationFingerprint,
        string detectorSha256,
        DevelopmentSelection development,
        string developmentContentSha256,
        Evaluation evaluation,
        ExecutionReceipt receipt) => new(
            Schema: "graphreader.real-dev-detector-subfloor-census-v1",
            EvaluationMode: "development_diagnostic",
            CandidateMode: "v8-detector-cpu",
            DetectorId: "graph-text-spaced-component-recall-v10-p2",
            DetectorVersion: "0.0.21-p2",
            DetectorSha256: detectorSha256,
            DetectorConfigurationFingerprint: detectorConfigurationFingerprint,
            DetectorProposalAlgorithm: LocalOnnxProposalTextRegionDetector.ProposalAlgorithm,
            DetectorEncodingAlgorithm: LocalOnnxProposalTextRegionDetector.EncodingAlgorithm,
            DetectorPostprocessingAlgorithm: LocalOnnxProposalTextRegionDetector.PostprocessingAlgorithm,
            DetectorContract: OcrProposalClassifierContract.BinaryV8.ToString(),
            DetectorStageVersion: "0.0.21-v10-p2",
            DetectorProvider: InferenceProvider.Cpu.ToString(),
            ReturnedProposalFloor: LocalOnnxProposalTextRegionDetector.ProposalConfidenceFloor,
            CandidateScope: "Every source-bound Component value returned by the exact private BuildProposals method before the detector confidence-floor filter",
            ReflectionMethods: ["EstimateThreshold", "BuildProposals", "EncodeProposal", "MapToOriginal", "DeterministicRegionId", "SoftmaxClassOne", "TransformFingerprint", "ProviderFingerprint"],
            ParityRule: "For each panel, the private-reflection re-score set with score >= 0.82 must match public DetectProposalsAsync by UUID, original-pixel bounds, and exact float confidence before any score/geometry aggregate is recorded",
            GeometryDefinition: "Oracle bounds are the clamped extrema of source Engauge axis anchors; within-Y uses component center Y; left-of-axis requires component right edge <= oracle left; near-left means gap <= listed fraction of oracle plot width",
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
            NearAxis2_5PercentPlotWidth: 0.025,
            NearAxis5PercentPlotWidth: 0.05,
            NearAxis10PercentPlotWidth: 0.10,
            OnlyAggregateOutput: true,
            NoGroundTruthProposalTextOrBoxes: true,
            Aggregate: evaluation,
            SourceSha256: receipt.SourceSha256,
            ProjectSha256: receipt.ProjectSha256,
            LoadedAssemblySha256: receipt.LoadedAssemblySha256);

    private static PrivateDetectorApi CreatePrivateDetectorApi(LocalOnnxProposalTextRegionDetectorOptions options)
    {
        Type type = typeof(LocalOnnxProposalTextRegionDetector);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo Required(string name) => type.GetMethod(name, flags) ??
            throw new MissingMethodException("EXACT_PRIVATE_DETECTOR_METHOD_UNAVAILABLE:" + name);
        MethodInfo encode = Required("EncodeProposal");
        Type componentType = encode.GetParameters()[1].ParameterType;
        var dynamic = new DynamicMethod("ExactDetectorEncodeProposal", typeof(void),
            [typeof(OcrImage), typeof(object), typeof(byte), typeof(LocalOnnxProposalTextRegionDetectorOptions), typeof(Span<float>)],
            type.Module, skipVisibility: true);
        ILGenerator il = dynamic.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Unbox_Any, componentType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Call, encode);
        il.Emit(OpCodes.Ret);
        EncodeProposalDelegate encoder = (EncodeProposalDelegate)dynamic.CreateDelegate(typeof(EncodeProposalDelegate));
        if (encoder is null) throw new InvalidOperationException("EXACT_PRIVATE_ENCODER_BIND_FAILED");
        return new PrivateDetectorApi(
            options,
            Required("EstimateThreshold"),
            Required("BuildProposals"),
            encoder,
            Required("MapToOriginal"),
            Required("DeterministicRegionId"),
            Required("SoftmaxClassOne"),
            Required("TransformFingerprint"),
            Required("ProviderFingerprint"));
    }

    private static int SelfTest()
    {
        IReadOnlyDictionary<string, int> emptyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, long> emptyLongCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> emptyGeometry =
            new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> emptyHistograms =
            new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        var report = BuildReport(
            "self-test-configuration",
            OcrV8ProductionCompositionFactory.DetectorSha256,
            new DevelopmentSelection(120, 28, 104, new string('0', 64), new string('0', 64),
                Array.AsReadOnly(Enumerable.Range(0, ExpectedDevelopmentProjects)
                    .Select(index => new FrozenRealCorpusProject(string.Empty, "case" + index, "study" + index,
                        FrozenRealCorpusInventory.RealDev)).ToArray())),
            new string('0', 64),
            new Evaluation(16, 0, 0, 0, 0, emptyLongCounts, emptyGeometry, emptyHistograms,
                0, 0, emptyCounts, emptyCounts),
            new ExecutionReceipt(new string('0', 64), new string('0', 64), new Dictionary<string, string>()));
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(report, JsonOptions));
        JsonElement root = json.RootElement;
        if (!Flag("frozen_assignment_verified", true) || !Flag("previously_exposed_development_data", true) ||
            !Flag("sealed_payloads_read", false) || !Flag("case_level_output", false) ||
            !Flag("production_approved", false) || !Flag("sealed_acceptance", false) ||
            !Flag("only_aggregate_output", true) || !Flag("no_ground_truth_proposal_text_or_boxes", true) ||
            root.GetProperty("schema").GetString() != "graphreader.real-dev-detector-subfloor-census-v1" ||
            root.GetProperty("returned_proposal_floor").GetSingle() != 0.82f ||
            root.GetProperty("detector_contract").GetString() != "BinaryV8" ||
            root.GetProperty("reflection_methods").GetArrayLength() != 8 ||
            !root.GetProperty("candidate_scope").GetString()!.Contains("confidence-floor", StringComparison.OrdinalIgnoreCase) ||
            root.GetProperty("aggregate").GetProperty("panels_with_public_detector_parity").GetInt32() != 0)
            return 1;
        LocalOnnxProposalTextRegionDetectorOptions options = CreateDetectorOptions(
            new ModelIdentity("graph-text-spaced-component-recall-v10-p2", "0.0.21-p2",
                OcrV8ProductionCompositionFactory.DetectorSha256, "self-test.onnx"));
        _ = CreatePrivateDetectorApi(options);
        Console.WriteLine("REAL_DEV_DETECTOR_SUBFLOOR_SELF_TEST_PASS");
        return 0;

        bool Flag(string name, bool expected) => root.GetProperty(name).GetBoolean() == expected;
    }

    private static LocalOnnxProposalTextRegionDetectorOptions CreateDetectorOptions(ModelIdentity model) => new(model)
    {
        StageVersion = "0.0.21-v10-p2",
        AllowedProviders = [InferenceProvider.Cpu],
        BypassCache = true,
    };

    private static InferenceRuntime CreateRuntime()
    {
        var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(),
            new WindowsExecutionProviderPolicy(), new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance),
            CpuThreadConfiguration.Create(1));
        return new InferenceRuntime(registry, new BoundedInferenceScheduler(2, 1),
            new NoPersistenceStageCache(), allowedProviders: [InferenceProvider.Cpu]);
    }

    private static ModelIdentity LoadDetector(string root) => new(
        "graph-text-spaced-component-recall-v10-p2",
        "0.0.21-p2",
        OcrV8ProductionCompositionFactory.DetectorSha256,
        Path.GetFullPath(Path.Combine(root,
            "ml/ocr/component_spaced_recall_detector_v10/artifacts/P2-run/graph-text-spaced-component-recall-v10-p2.onnx")));

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

    private static Geometry Classify(OcrRectangle bounds, OcrRectangle plot)
    {
        double gap = bounds.Right <= plot.Left ? plot.Left - bounds.Right : double.PositiveInfinity;
        double relativeGap = gap / plot.Width;
        bool withinY = bounds.Center.Y >= plot.Top && bounds.Center.Y <= plot.Bottom;
        bool left = bounds.Right <= plot.Left;
        bool near2_5 = left && relativeGap <= 0.025;
        bool near5 = left && relativeGap <= 0.05;
        bool near10 = left && relativeGap <= 0.10;
        return new Geometry(withinY, left, near2_5, near5, near10,
            withinY && near2_5, withinY && near5, withinY && near10);
    }

    private static ExecutionReceipt CaptureExecutionReceipt(string root)
    {
        string directory = Path.Combine(root, "tools", "RealDevDetectorSubfloorCensusV1");
        Assembly[] assemblies =
        [
            Assembly.GetExecutingAssembly(),
            typeof(ProductionRasterFrameDecoder).Assembly,
            typeof(GraphReader.Axis.RobustCalibration).Assembly,
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
            HashFile(Path.Combine(directory, "RealDevDetectorSubfloorCensusV1.csproj")), hashes);
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
        string outputRoot = Path.GetFullPath(Path.Combine(artifacts, "real-dev-detector-subfloor-census-v1"));
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

    private static void WriteParityFailure(string output) =>
        File.WriteAllText(Path.Combine(output, "aggregate-failure.json"),
            JsonSerializer.Serialize(new FailureReport("failed", "public_detector_parity_mismatch", true, false, false), JsonOptions) + Environment.NewLine,
            new UTF8Encoding(false));

    private sealed class SubfloorAccumulator
    {
        private readonly List<ScoredPanel> _panels = [];
        private readonly Dictionary<string, int> _sourceFailures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _candidateFailures = new(StringComparer.Ordinal);
        internal int ParityPanels { get; private set; }
        internal int CensusPanels => _panels.Count;
        internal void RecordFailure(bool source, string kind)
        {
            Dictionary<string, int> target = source ? _sourceFailures : _candidateFailures;
            target[kind] = target.GetValueOrDefault(kind) + 1;
        }
        internal void Record(ScoredPanel panel)
        {
            if (panel.AboveFloorCount != panel.PublicProposalCount)
                throw new ProposalParityException();
            ParityPanels++;
            _panels.Add(panel);
        }

        internal Evaluation Finish(int denominator)
        {
            var scoreCounts = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["below_0_82"] = 0,
                ["at_or_above_0_82"] = 0,
            };
            var geometryCounts = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
            string[] bands = ["below_0_82", "at_or_above_0_82"];
            string[] geometryNames =
            [
                "within_oracle_y_extent", "left_of_oracle_axis", "near_left_2_5_percent",
                "near_left_5_percent", "near_left_10_percent", "near_left_2_5_percent_and_y",
                "near_left_5_percent_and_y", "near_left_10_percent_and_y",
            ];
            foreach (string band in bands)
                geometryCounts.Add(band, geometryNames.ToDictionary(static name => name, static _ => 0L, StringComparer.Ordinal));

            var panelHistograms = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (string band in bands)
            {
                foreach (string key in new[] { band }.Concat(geometryNames.Select(name => band + "." + name)))
                {
                    panelHistograms.Add(key, new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["zero"] = 0, ["one"] = 0, ["two_to_five"] = 0,
                        ["six_to_ten"] = 0, ["more_than_ten"] = 0,
                    });
                }
            }
            foreach (ScoredPanel panel in _panels)
            {
                foreach (string band in bands)
                {
                    ScoredProposal[] proposals = panel.Proposals.Where(item =>
                        band == "below_0_82"
                            ? item.Confidence < LocalOnnxProposalTextRegionDetector.ProposalConfidenceFloor
                            : item.Confidence >= LocalOnnxProposalTextRegionDetector.ProposalConfidenceFloor).ToArray();
                    scoreCounts[band] += proposals.Length;
                    Increment(panelHistograms[band], CountBin(proposals.Length));
                    foreach (ScoredProposal proposal in proposals)
                    {
                        foreach (string geometry in GeometryNames(proposal.Geometry))
                            geometryCounts[band][geometry]++;
                    }
                    foreach (string geometry in geometryNames)
                    {
                        int count = proposals.Count(proposal => GeometryNames(proposal.Geometry).Contains(geometry, StringComparer.Ordinal));
                        Increment(panelHistograms[band + "." + geometry], CountBin(count));
                    }
                }
            }
            return new Evaluation(
                denominator,
                _panels.Count,
                ParityPanels,
                _panels.Sum(static panel => panel.Proposals.Count),
                _panels.Sum(static panel => panel.PublicProposalCount),
                scoreCounts,
                geometryCounts.ToDictionary(static pair => pair.Key,
                    static pair => (IReadOnlyDictionary<string, long>)pair.Value, StringComparer.Ordinal),
                panelHistograms.ToDictionary(static pair => pair.Key,
                    static pair => (IReadOnlyDictionary<string, int>)pair.Value, StringComparer.Ordinal),
                _sourceFailures.Values.Sum(),
                _candidateFailures.Values.Sum(),
                _sourceFailures,
                _candidateFailures);
        }
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

    private static string CountBin(int count) => count switch
    {
        0 => "zero",
        1 => "one",
        >= 2 and <= 5 => "two_to_five",
        >= 6 and <= 10 => "six_to_ten",
        > 10 => "more_than_ten",
        _ => throw new ArgumentOutOfRangeException(nameof(count)),
    };

    private static void Increment(Dictionary<string, int> values, string key) =>
        values[key] = values.GetValueOrDefault(key) + 1;

    private sealed class PrivateDetectorApi
    {
        internal PrivateDetectorApi(
            LocalOnnxProposalTextRegionDetectorOptions options,
            MethodInfo estimateThreshold,
            MethodInfo buildProposals,
            EncodeProposalDelegate encode,
            MethodInfo mapToOriginal,
            MethodInfo deterministicRegionId,
            MethodInfo softmaxClassOne,
            MethodInfo transformFingerprint,
            MethodInfo providerFingerprint)
        {
            Options = options;
            EstimateThreshold = estimateThreshold;
            BuildProposals = buildProposals;
            Encode = encode;
            MapToOriginal = mapToOriginal;
            DeterministicRegionId = deterministicRegionId;
            SoftmaxClassOne = softmaxClassOne;
            TransformFingerprint = transformFingerprint;
            ProviderFingerprint = providerFingerprint;
        }
        internal LocalOnnxProposalTextRegionDetectorOptions Options { get; }
        internal MethodInfo EstimateThreshold { get; }
        internal MethodInfo BuildProposals { get; }
        internal EncodeProposalDelegate Encode { get; }
        internal MethodInfo MapToOriginal { get; }
        internal MethodInfo DeterministicRegionId { get; }
        internal MethodInfo SoftmaxClassOne { get; }
        internal MethodInfo TransformFingerprint { get; }
        internal MethodInfo ProviderFingerprint { get; }
    }

    private delegate void EncodeProposalDelegate(
        OcrImage image,
        object component,
        byte threshold,
        LocalOnnxProposalTextRegionDetectorOptions options,
        Span<float> destination);

    private sealed class ProposalParityException : InvalidOperationException { }
    private sealed record ScoredProposal(string RegionId, OcrRectangle Bounds, double Confidence, Geometry Geometry);
    private sealed record ScoredPanel(IReadOnlyList<ScoredProposal> Proposals, int AboveFloorCount, int PublicProposalCount);
    private sealed record Geometry(
        bool WithinY, bool LeftOfAxis, bool Near2_5, bool Near5, bool Near10,
        bool Near2_5AndY, bool Near5AndY, bool Near10AndY);
    private sealed record DevelopmentSelection(
        int SelectedProjectCount, int StudyCount, int TrainProjectCount,
        string AssignmentSha256, string SelectedInventorySha256,
        IReadOnlyList<FrozenRealCorpusProject> Projects);
    private sealed record ExecutionReceipt(
        string SourceSha256, string ProjectSha256, IReadOnlyDictionary<string, string> LoadedAssemblySha256);
    private sealed record Evaluation(
        int DevelopmentPanelDenominator,
        int PanelCensusCount,
        int PanelsWithPublicDetectorParity,
        int PrivateCandidateCount,
        int PublicDetectorProposalCount,
        IReadOnlyDictionary<string, long> CandidateCountByScoreBand,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> GeometryCountByScoreBand,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> PanelCountHistograms,
        int SourceFailures,
        int CandidateFailures,
        IReadOnlyDictionary<string, int> SourceFailureKinds,
        IReadOnlyDictionary<string, int> CandidateFailureKinds);
    private sealed record AggregateReport(
        string Schema, string EvaluationMode, string CandidateMode,
        string DetectorId, string DetectorVersion, string DetectorSha256,
        string DetectorConfigurationFingerprint, string DetectorProposalAlgorithm,
        string DetectorEncodingAlgorithm, string DetectorPostprocessingAlgorithm,
        string DetectorStageVersion, string DetectorProvider, string DetectorContract, float ReturnedProposalFloor,
        string CandidateScope, IReadOnlyList<string> ReflectionMethods, string ParityRule,
        string GeometryDefinition, int SelectedProjectCount, int StudyCount,
        int TrainProjectCount, int DevelopmentProjectCount, string AssignmentSha256,
        string SelectedInventorySha256, string DevelopmentContentSha256, string PartitionRule,
        bool FrozenAssignmentVerified, bool PreviouslyExposedDevelopmentData,
        bool SealedPayloadsRead, bool CaseLevelOutput, bool ProductionApproved,
        bool SealedAcceptance, double NearAxis2_5PercentPlotWidth,
        double NearAxis5PercentPlotWidth, double NearAxis10PercentPlotWidth,
        bool OnlyAggregateOutput, bool NoGroundTruthProposalTextOrBoxes,
        Evaluation Aggregate, string SourceSha256, string ProjectSha256,
        IReadOnlyDictionary<string, string> LoadedAssemblySha256);
    private sealed record FailureReport(
        string Status, string FailureKind, bool PreviouslyExposedDevelopmentData,
        bool SealedPayloadsRead, bool CaseLevelOutput);
}
