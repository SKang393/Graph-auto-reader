// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Inference;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

public interface IProductionOcrAdapter
{
    string AdapterId { get; }

    bool IsApproved { get; }

    Task<ProductionOcrEvidence> RecognizeAsync(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster originalRaster,
        OcrRectangle plotBounds,
        OcrDetectorImage detectorImage,
        CancellationToken cancellationToken);
}

internal enum ProductionOcrConfigurationScope
{
    ApprovedProduction,
    UnapprovedLocalSyntheticCandidate,
    UnapprovedFrozenCandidate,
}

public sealed class ProductionOcrConfiguredModel
{
    internal ProductionOcrConfiguredModel(
        string task,
        ModelIdentity identity,
        InferenceProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        Task = task;
        Identity = identity;
        Provider = provider is InferenceProvider.Cpu or InferenceProvider.DirectMl
            ? provider
            : throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Configured OCR evidence supports only CPU or DirectML providers.");
    }

    public string Task { get; }

    internal ModelIdentity Identity { get; }

    internal InferenceProvider Provider { get; }

    public string ModelId => Identity.ModelId;

    public string Version => Identity.Version;

    public string Sha256 => Identity.Sha256;

    public string ExecutionProvider => ProviderName(Provider);

    private static string ProviderName(InferenceProvider provider) => provider switch
    {
        InferenceProvider.Cpu => "cpu",
        InferenceProvider.DirectMl => "directml",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };
}

public sealed class ProductionOcrConfigurationEvidence
{
    internal ProductionOcrConfigurationEvidence(
        IEnumerable<ProductionOcrConfiguredModel> models,
        ProductionOcrConfigurationScope scope)
    {
        ArgumentNullException.ThrowIfNull(models);
        Models = Array.AsReadOnly(models.ToArray());
        ConfigurationScope = scope;
    }

    public IReadOnlyList<ProductionOcrConfiguredModel> Models { get; }

    internal ProductionOcrConfigurationScope ConfigurationScope { get; }

    public string Scope => ConfigurationScope switch
    {
        ProductionOcrConfigurationScope.ApprovedProduction => "approved_production",
        ProductionOcrConfigurationScope.UnapprovedLocalSyntheticCandidate =>
            "unapproved_local_synthetic_candidate",
        ProductionOcrConfigurationScope.UnapprovedFrozenCandidate =>
            "unapproved_frozen_candidate",
        _ => throw new ArgumentOutOfRangeException(nameof(ConfigurationScope)),
    };

    internal bool IsApproved =>
        ConfigurationScope == ProductionOcrConfigurationScope.ApprovedProduction;
}

public sealed class ProductionOcrEvidence
{
    internal ProductionOcrEvidence(
        OcrResult result,
        IEnumerable<ProductionOcrModelEvidence> modelEvidence,
        ProductionOcrConfigurationEvidence configuredModels)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        ModelEvidence = Array.AsReadOnly(modelEvidence.ToArray());
        ConfiguredModels = configuredModels ?? throw new ArgumentNullException(nameof(configuredModels));
    }

    public OcrResult Result { get; }

    public IReadOnlyList<ProductionOcrModelEvidence> ModelEvidence { get; }

    public ProductionOcrConfigurationEvidence ConfiguredModels { get; }
}

/// <summary>
/// Pins local synthetic evaluation to exact model and manifest bytes. The
/// caller remains responsible for reviewed license, notice, and provenance.
/// </summary>
internal sealed record LocalSyntheticOcrModelDescriptor(
    ModelIdentity Identity,
    string ManifestPath,
    string ManifestSha256);

/// <summary>
/// Pins an externally frozen candidate to exact model and manifest bytes.
/// Reviewed licenses and the complete executable binding are validated by the
/// acceptance harness. This descriptor never grants production approval.
/// </summary>
internal sealed record FrozenCandidateOcrModelDescriptor(
    ModelIdentity Identity,
    string ManifestPath,
    string ManifestSha256);

/// <summary>
/// Binds the existing OCR pipeline to exact detector and recognizer identities.
/// Composition remains disabled until both payloads have independently passed
/// the production model-store, benchmark, provider, notice, and checksum gates.
/// </summary>
public sealed class ProductionOcrAdapter :
    IProductionOcrAdapter,
    IProductionCandidateOcrAdapter
{
    public const string ApprovalBenchmarkProfile =
        "graphreader-ocr-structure-consensus-public-gate-v1";
    internal const string OfficialRecognitionSpacingV2Algorithm =
        "ctc_greedy_alternatives_with_source_spacing_v2_p2";
    private const string CombinedTimingWarning = "ocr_pipeline_timing_not_model_isolated";
    private readonly Lazy<OcrPipeline> pipeline;
    private readonly ModelIdentity detectionModel;
    private readonly ModelIdentity recognitionModel;
    private readonly InferenceProvider detectionProvider;
    private readonly InferenceProvider recognitionProvider;
    private readonly ProductionOcrConfigurationEvidence configuredModels;
    private readonly string compositionVersion;
    private readonly GraphStructureModelInput modelInput;

    public ProductionOcrAdapter(
        OcrPipeline pipeline,
        ModelIdentity detectionModel,
        InferenceProvider detectionProvider,
        ModelIdentity recognitionModel,
        InferenceProvider recognitionProvider,
        string openCvRuntimeSha256,
        bool isApproved)
        : this(
            () => pipeline ?? throw new ArgumentNullException(nameof(pipeline)),
            detectionModel,
            detectionProvider,
            recognitionModel,
            recognitionProvider,
            openCvRuntimeSha256,
            RequireUnapprovedDirectConstruction(isApproved))
    {
    }

    private ProductionOcrAdapter(
        Func<OcrPipeline> pipelineFactory,
        ModelIdentity detectionModel,
        InferenceProvider detectionProvider,
        ModelIdentity recognitionModel,
        InferenceProvider recognitionProvider,
        string openCvRuntimeSha256,
        ProductionOcrConfigurationScope configurationScope,
        GraphStructureConsensusGeometry outputGeometry = GraphStructureConsensusGeometry.ModelPolygon,
        GraphStructureModelInput modelInput = GraphStructureModelInput.AxisMasked,
        GraphStructureConsensusAdmission admission = GraphStructureConsensusAdmission.Required)
    {
        ArgumentNullException.ThrowIfNull(pipelineFactory);
        pipeline = new Lazy<OcrPipeline>(
            pipelineFactory,
            LazyThreadSafetyMode.ExecutionAndPublication);
        this.detectionModel = ValidateModel(detectionModel, nameof(detectionModel));
        this.recognitionModel = ValidateModel(recognitionModel, nameof(recognitionModel));
        this.detectionProvider = ValidateProvider(detectionProvider, nameof(detectionProvider));
        this.recognitionProvider = ValidateProvider(recognitionProvider, nameof(recognitionProvider));
        OpenCvRuntimeSha256 = ValidateSha256(openCvRuntimeSha256, nameof(openCvRuntimeSha256));
        this.modelInput = modelInput;
        compositionVersion = GraphStructureConsensusTextRegionDetector.GetCompositionVersion(outputGeometry, modelInput, admission);
        if (configurationScope == ProductionOcrConfigurationScope.ApprovedProduction &&
            (outputGeometry != GraphStructureConsensusGeometry.ModelPolygon ||
             modelInput != GraphStructureModelInput.AxisMasked ||
             admission != GraphStructureConsensusAdmission.Required))
        {
            throw new InvalidOperationException("Experimental OCR geometry or model input cannot acquire production approval.");
        }
        configuredModels = new ProductionOcrConfigurationEvidence(
        [
            new ProductionOcrConfiguredModel("ocr_detection", this.detectionModel, this.detectionProvider),
            new ProductionOcrConfiguredModel("ocr_recognition", this.recognitionModel, this.recognitionProvider),
        ],
        configurationScope);
        IsApproved = configuredModels.IsApproved;
    }

    public string AdapterId =>
        $"graphreader-ocr:{compositionVersion}:{detectionModel.Sha256[..12].ToLowerInvariant()}:{recognitionModel.Sha256[..12].ToLowerInvariant()}:{OpenCvRuntimeSha256[..12]}";

    public bool IsApproved { get; }

    internal string ConfigurationScope => configuredModels.Scope;

    public string OpenCvRuntimeSha256 { get; }

    public static async Task<ProductionOcrAdapter> CreateAsync(
        ResolvedProductionModel detectionModel,
        ResolvedProductionModel recognitionModel,
        ProductionInferenceRuntimeHost runtimeHost,
        string reviewedOpenCvRuntimeSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detectionModel);
        ArgumentNullException.ThrowIfNull(recognitionModel);
        ArgumentNullException.ThrowIfNull(runtimeHost);
        reviewedOpenCvRuntimeSha256 = ValidateSha256(
            reviewedOpenCvRuntimeSha256,
            nameof(reviewedOpenCvRuntimeSha256));
        RequireTask(detectionModel, "ocr_detection");
        RequireTask(recognitionModel, "ocr_recognition");
        RequireCpu(detectionModel);
        RequireCpu(recognitionModel);
        await VerifyChecksumAsync(
            detectionModel.ManifestPath,
            detectionModel.ManifestSha256,
            "OCR detection manifest",
            cancellationToken).ConfigureAwait(false);
        await VerifyChecksumAsync(
            recognitionModel.ManifestPath,
            recognitionModel.ManifestSha256,
            "OCR recognition manifest",
            cancellationToken).ConfigureAwait(false);
        ProductionOcrApprovalGate.Validate(detectionModel, recognitionModel);

        return await CreateFromPinnedModelsAsync(
                detectionModel.Identity,
                detectionModel.ManifestPath,
                recognitionModel.Identity,
                recognitionModel.ManifestPath,
                runtimeHost,
                reviewedOpenCvRuntimeSha256,
                ProductionOcrConfigurationScope.ApprovedProduction,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<ProductionOcrAdapter> CreateForLocalSyntheticCandidateEvaluationAsync(
        LocalSyntheticOcrModelDescriptor detectionModel,
        LocalSyntheticOcrModelDescriptor recognitionModel,
        ProductionInferenceRuntimeHost runtimeHost,
        string reviewedOpenCvRuntimeSha256,
        CancellationToken cancellationToken,
        GraphStructureConsensusGeometry outputGeometry = GraphStructureConsensusGeometry.ModelPolygon,
        GraphStructureModelInput modelInput = GraphStructureModelInput.AxisMasked,
        GraphStructureConsensusAdmission admission = GraphStructureConsensusAdmission.Required)
    {
        ArgumentNullException.ThrowIfNull(detectionModel);
        ArgumentNullException.ThrowIfNull(recognitionModel);
        ArgumentNullException.ThrowIfNull(runtimeHost);
        if (admission == GraphStructureConsensusAdmission.Advisory &&
            (outputGeometry != GraphStructureConsensusGeometry.InitialDbContour ||
             modelInput != GraphStructureModelInput.Original))
        {
            throw new InvalidOperationException("Advisory structure requires the preregistered original-input initial-contour mode.");
        }
        if (outputGeometry == GraphStructureConsensusGeometry.InitialDbContour &&
            modelInput != GraphStructureModelInput.Original)
        {
            throw new InvalidOperationException("Initial-contour experimentation requires the frozen original detector input.");
        }
        _ = GraphStructureConsensusTextRegionDetector.GetCompositionVersion(outputGeometry, modelInput, admission);
        if (modelInput == GraphStructureModelInput.Original &&
            outputGeometry is not (GraphStructureConsensusGeometry.ModelPolygon or GraphStructureConsensusGeometry.InitialDbContour))
        {
            throw new InvalidOperationException("The original-input candidate does not authorize matched-component output geometry.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        reviewedOpenCvRuntimeSha256 = ValidateSha256(
            reviewedOpenCvRuntimeSha256,
            nameof(reviewedOpenCvRuntimeSha256));
        await ValidateLocalSyntheticDescriptorAsync(
                detectionModel,
                "ocr_detection",
                cancellationToken)
            .ConfigureAwait(false);
        await ValidateLocalSyntheticDescriptorAsync(
                recognitionModel,
                "ocr_recognition",
                cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(
                detectionModel.Identity.Sha256,
                recognitionModel.Identity.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Path.GetFullPath(detectionModel.Identity.FilePath),
                Path.GetFullPath(recognitionModel.Identity.FilePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Local synthetic OCR evaluation requires distinct pinned detection and recognition payloads.");
        }

        return await CreateFromPinnedModelsAsync(
                detectionModel.Identity,
                detectionModel.ManifestPath,
                recognitionModel.Identity,
                recognitionModel.ManifestPath,
                runtimeHost,
                reviewedOpenCvRuntimeSha256,
                ProductionOcrConfigurationScope.UnapprovedLocalSyntheticCandidate,
                cancellationToken,
                outputGeometry,
                modelInput,
                admission)
            .ConfigureAwait(false);
    }

    internal static async Task<ProductionOcrAdapter> CreateForFrozenCandidateEvaluationAsync(
        FrozenCandidateOcrModelDescriptor detectionModel,
        FrozenCandidateOcrModelDescriptor recognitionModel,
        ProductionInferenceRuntimeHost runtimeHost,
        string reviewedOpenCvRuntimeSha256,
        CancellationToken cancellationToken,
        GraphStructureConsensusGeometry outputGeometry = GraphStructureConsensusGeometry.ModelPolygon)
    {
        ArgumentNullException.ThrowIfNull(detectionModel);
        ArgumentNullException.ThrowIfNull(recognitionModel);
        ArgumentNullException.ThrowIfNull(runtimeHost);
        if (outputGeometry == GraphStructureConsensusGeometry.InitialDbContour)
        {
            throw new InvalidOperationException("Initial-contour output is limited to the preregistered local synthetic factory.");
        }
        _ = GraphStructureConsensusTextRegionDetector.GetCompositionVersion(outputGeometry);
        cancellationToken.ThrowIfCancellationRequested();
        reviewedOpenCvRuntimeSha256 = ValidateSha256(
            reviewedOpenCvRuntimeSha256,
            nameof(reviewedOpenCvRuntimeSha256));
        await ValidateCandidateDescriptorAsync(
                detectionModel.Identity,
                detectionModel.ManifestPath,
                detectionModel.ManifestSha256,
                "ocr_detection",
                "Frozen candidate OCR",
                cancellationToken)
            .ConfigureAwait(false);
        await ValidateCandidateDescriptorAsync(
                recognitionModel.Identity,
                recognitionModel.ManifestPath,
                recognitionModel.ManifestSha256,
                "ocr_recognition",
                "Frozen candidate OCR",
                cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(
                detectionModel.Identity.Sha256,
                recognitionModel.Identity.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Path.GetFullPath(detectionModel.Identity.FilePath),
                Path.GetFullPath(recognitionModel.Identity.FilePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Frozen candidate OCR evaluation requires distinct pinned detection and recognition payloads.");
        }

        return await CreateFromPinnedModelsAsync(
                detectionModel.Identity,
                detectionModel.ManifestPath,
                recognitionModel.Identity,
                recognitionModel.ManifestPath,
                runtimeHost,
                reviewedOpenCvRuntimeSha256,
                ProductionOcrConfigurationScope.UnapprovedFrozenCandidate,
                cancellationToken,
                outputGeometry)
            .ConfigureAwait(false);
    }

    private static async Task<ProductionOcrAdapter> CreateFromPinnedModelsAsync(
        ModelIdentity detectionModel,
        string detectionManifestPath,
        ModelIdentity recognitionModel,
        string recognitionManifestPath,
        ProductionInferenceRuntimeHost runtimeHost,
        string reviewedOpenCvRuntimeSha256,
        ProductionOcrConfigurationScope configurationScope,
        CancellationToken cancellationToken,
        GraphStructureConsensusGeometry outputGeometry = GraphStructureConsensusGeometry.ModelPolygon,
        GraphStructureModelInput modelInput = GraphStructureModelInput.AxisMasked,
        GraphStructureConsensusAdmission admission = GraphStructureConsensusAdmission.Required)
    {
        LocalOnnxTextRegionDetectorOptions detectorOptions = ReadDetectionOptions(
            detectionModel,
            detectionManifestPath);
        (LocalOnnxTextRecognizerOptions Recognizer, OcrPipelineOptions Pipeline) recognition =
            ReadRecognitionOptions(recognitionModel, recognitionManifestPath);
        bool usesOfficialSpacingV2 = UsesOfficialRecognitionSpacingV2Manifest(
            recognitionManifestPath);
        InferenceRuntime runtime = runtimeHost.Runtime;
        await ValidateExecutablePairAsync(
                detectorOptions,
                recognition.Recognizer,
                runtime,
                outputGeometry,
                modelInput,
                admission,
                cancellationToken)
            .ConfigureAwait(false);
        return new ProductionOcrAdapter(
            () =>
            {
                var modelDetector = new LocalOnnxTextRegionDetector(runtime, detectorOptions);
                var detector = new GraphStructureConsensusTextRegionDetector(
                    modelDetector,
                    new ConnectedComponentTextRegionDetector(),
                    new GraphStructureConsensusTextRegionDetectorOptions
                    {
                        OutputGeometry = outputGeometry,
                        ModelInput = modelInput,
                        Admission = admission,
                    });
                ITextRecognizer recognizer = new LocalOnnxTextRecognizer(
                    runtime,
                    recognition.Recognizer);
                if (usesOfficialSpacingV2)
                {
                    recognizer = new OfficialRecognitionSpacingV2TextRecognizer(recognizer);
                }
                return new OcrPipeline(
                    detector,
                    recognizer,
                    new MemoryOcrResultCache(),
                    recognition.Pipeline);
            },
            detectionModel,
            InferenceProvider.Cpu,
            recognitionModel,
            InferenceProvider.Cpu,
            reviewedOpenCvRuntimeSha256,
            configurationScope,
            outputGeometry,
            modelInput,
            admission);
    }

    private static Task ValidateExecutablePairAsync(
        LocalOnnxTextRegionDetectorOptions detectorOptions,
        LocalOnnxTextRecognizerOptions recognizerOptions,
        InferenceRuntime runtime,
        GraphStructureConsensusGeometry outputGeometry,
        GraphStructureModelInput modelInput,
        GraphStructureConsensusAdmission admission,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modelDetector = new LocalOnnxTextRegionDetector(
                runtime,
                detectorOptions with { BypassCache = true });
            var detector = new GraphStructureConsensusTextRegionDetector(
                modelDetector,
                new ConnectedComponentTextRegionDetector(),
                new GraphStructureConsensusTextRegionDetectorOptions
                {
                    OutputGeometry = outputGeometry,
                    ModelInput = modelInput,
                    Admission = admission,
                });
            const int detectorProbeSize = 32;
            var detectorImage = new OcrImage(
                detectorProbeSize,
                detectorProbeSize,
                detectorProbeSize,
                new byte[detectorProbeSize * detectorProbeSize],
                OcrSourceImage.Original,
                OcrFrameTransform.Identity,
                CanonicalOriginalWidth: detectorProbeSize,
                CanonicalOriginalHeight: detectorProbeSize,
                BgrPixels: detectorOptions.InputColorMode == OcrTensorColorMode.Bgr
                    ? new OcrBgrBytePixels(
                        detectorProbeSize * 3,
                        new byte[detectorProbeSize * detectorProbeSize * 3])
                    : null);
            _ = modelInput == GraphStructureModelInput.Original
                ? await detector.DetectAsync(detectorImage, detectorImage, cancellationToken).ConfigureAwait(false)
                : await detector.DetectAsync(detectorImage, cancellationToken).ConfigureAwait(false);

            var recognizer = new LocalOnnxTextRecognizer(
                runtime,
                recognizerOptions with { BypassCache = true });
            var cropPixels = new float[checked(recognizerOptions.InputWidth * recognizerOptions.InputHeight)];
            OcrBgrFloatPixels? bgrCropPixels = recognizerOptions.InputColorMode == OcrTensorColorMode.Bgr
                ? new OcrBgrFloatPixels(
                    recognizerOptions.InputWidth * 3,
                    new float[checked(cropPixels.Length * 3)])
                : null;
            string cropSha256 = Convert.ToHexStringLower(
                SHA256.HashData(new byte[checked(cropPixels.Length * sizeof(float))]));
            OcrPolygon polygon = OcrPolygon.FromRectangle(new OcrRectangle(
                0,
                0,
                recognizerOptions.InputWidth,
                recognizerOptions.InputHeight));
            OcrCrop[] crops =
            [
                new("probe-1", OcrSourceImage.Original, recognizerOptions.InputWidth,
                    recognizerOptions.InputHeight, cropPixels, cropSha256, polygon, bgrCropPixels),
                new("probe-2", OcrSourceImage.Original, recognizerOptions.InputWidth,
                    recognizerOptions.InputHeight, cropPixels, cropSha256, polygon, bgrCropPixels),
            ];
            IReadOnlyList<OcrRecognition> results = await recognizer
                .RecognizeBatchAsync(crops, cancellationToken)
                .ConfigureAwait(false);
            if (results.Count != crops.Length || results.Any(static result => result.Failure is not null))
            {
                string failures = string.Join(
                    ", ",
                    results.Where(static result => result.Failure is not null)
                        .Select(static result =>
                            $"{result.Failure!.Code}: {result.Failure.TechnicalMessage}"));
                throw new InvalidDataException(
                    $"OCR recognition payload failed the two-item CPU executable probe: {failures}.");
            }
        }, cancellationToken);

    public async Task<ProductionOcrEvidence> RecognizeAsync(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster originalRaster,
        OcrRectangle plotBounds,
        OcrDetectorImage detectorImage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(originalRaster);
        ArgumentNullException.ThrowIfNull(detectorImage);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsApproved)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
                "Errors.ModelNotFound",
                $"OCR adapter '{AdapterId}' does not have two independently approved production payloads.",
                "Install checksum-verified approved OCR detection and recognition models or continue in manual mode.");
        }

        return await RecognizeCoreAsync(
                request,
                originalRaster,
                plotBounds,
                detectorImage,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal Task<ProductionOcrEvidence> RecognizeForLocalSyntheticCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster originalRaster,
        OcrRectangle plotBounds,
        OcrDetectorImage detectorImage,
        CancellationToken cancellationToken)
    {
        if (configuredModels.ConfigurationScope ==
            ProductionOcrConfigurationScope.UnapprovedFrozenCandidate)
        {
            throw new InvalidOperationException(
                "Local synthetic OCR execution requires the local synthetic candidate configuration scope.");
        }

        return RecognizeForCandidateEvaluationAsync(
            request,
            originalRaster,
            plotBounds,
            detectorImage,
            cancellationToken);
    }

    Task<ProductionOcrEvidence> IProductionCandidateOcrAdapter.RecognizeForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster originalRaster,
        OcrRectangle plotBounds,
        OcrDetectorImage detectorImage,
        CancellationToken cancellationToken) =>
        RecognizeForCandidateEvaluationAsync(
            request,
            originalRaster,
            plotBounds,
            detectorImage,
            cancellationToken);

    internal Task<ProductionOcrEvidence> RecognizeForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster originalRaster,
        OcrRectangle plotBounds,
        OcrDetectorImage detectorImage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(originalRaster);
        ArgumentNullException.ThrowIfNull(detectorImage);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsApproved)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Local candidate evaluation requires an explicitly unapproved OCR adapter.",
                "Use normal production execution for an approved adapter.");
        }
        return RecognizeCoreAsync(
            request,
            originalRaster,
            plotBounds,
            detectorImage,
            cancellationToken);
    }

    internal static IReadOnlyList<string> DetectorInputWarnings(
        GraphStructureModelInput modelInput,
        OcrImage originalImage,
        OcrDetectorImage detectorImage)
    {
        ArgumentNullException.ThrowIfNull(originalImage);
        ArgumentNullException.ThrowIfNull(detectorImage);
        if (modelInput == GraphStructureModelInput.AxisMasked)
        {
            return Array.AsReadOnly(new[]
            {
                "ocr_detector_axis_geometry_mask_applied",
                $"ocr_detector_input_sha256:{detectorImage.PixelSha256.ToLowerInvariant()}",
            });
        }
        if (modelInput != GraphStructureModelInput.Original)
        {
            throw new ArgumentOutOfRangeException(nameof(modelInput));
        }
        var warnings = new List<string>
        {
            "ocr_detector_model_input_original",
            $"ocr_detector_model_input_sha256:{Convert.ToHexStringLower(SHA256.HashData(originalImage.Pixels.Span))}",
            "ocr_detector_structure_input_axis_masked",
            $"ocr_detector_structure_input_sha256:{detectorImage.PixelSha256.ToLowerInvariant()}",
        };
        if (originalImage.BgrPixels is { } bgr)
        {
            warnings.Add($"ocr_detector_model_input_bgr_sha256:{Convert.ToHexStringLower(SHA256.HashData(bgr.Pixels.Span))}");
        }
        return warnings.AsReadOnly();
    }

    private async Task<ProductionOcrEvidence> RecognizeCoreAsync(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster originalRaster,
        OcrRectangle plotBounds,
        OcrDetectorImage detectorImage,
        CancellationToken cancellationToken)
    {
        ValidateInput(request, originalRaster, plotBounds);
        var ocrRequest = new OcrRequest(
            request.ProjectId.ToString("D"),
            request.Panel.ImportedPanel.PanelId.ToString("D"),
            request.Image.Sha256,
            originalRaster.CreateOcrImage(),
            plotBounds,
            EnhancedImage: null,
            DetectedRegions: null,
            OcrContract.Version,
            TransformChain: "identity",
            DetectorImage: detectorImage);

        OcrResult result = await pipeline.Value
            .RecognizeAsync(ocrRequest, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOutput(request, originalRaster, result);

        result = result with
        {
            Warnings = Array.AsReadOnly(result.Warnings
                .Concat(DetectorInputWarnings(modelInput, ocrRequest.OriginalImage, detectorImage))
                .Distinct(StringComparer.Ordinal)
                .ToArray()),
        };

        IReadOnlyList<string> envelopeWarnings = result.Warnings
            .Append(CombinedTimingWarning)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        WorkflowVisionTiming combinedTiming = new(
            result.Timing.PreprocessMilliseconds,
            result.Timing.InferenceMilliseconds,
            result.Timing.PostprocessMilliseconds,
            result.Timing.TotalMilliseconds);
        var models = new List<ProductionOcrModelEvidence>(capacity: 2);
        bool detectionCompleted = !string.Equals(
            result.Failure?.Code,
            "OCR_REGION_DETECTION_FAILED",
            StringComparison.Ordinal);
        if (detectionCompleted)
        {
            models.Add(new(
                "ocr_detection",
                CreateEnvelope(
                    request,
                    result,
                    detectionModel,
                    detectionProvider,
                    combinedTiming,
                    envelopeWarnings)));
        }

        if (result.Succeeded && result.Cache.CropCount > 0)
        {
            models.Add(new(
                "ocr_recognition",
                CreateEnvelope(
                    request,
                    result,
                    recognitionModel,
                    recognitionProvider,
                    combinedTiming,
                    envelopeWarnings)));
        }

        if (!result.Succeeded)
        {
            OcrFailure failure = result.Failure!;
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                failure.UserMessageKey,
                $"OCR pipeline failed with '{failure.Code}': {failure.TechnicalMessage}",
                failure.SuggestedAction,
                models.Select(static model => model.Envelope));
        }

        return new ProductionOcrEvidence(result, models, configuredModels);
    }

    internal static ProductionOcrAdapter CreateFromValidatedApprovedPipeline(
        OcrPipeline pipeline,
        ModelIdentity detectionModel,
        InferenceProvider detectionProvider,
        ModelIdentity recognitionModel,
        InferenceProvider recognitionProvider,
        string openCvRuntimeSha256) =>
        new(
            () => pipeline ?? throw new ArgumentNullException(nameof(pipeline)),
            detectionModel,
            detectionProvider,
            recognitionModel,
            recognitionProvider,
            openCvRuntimeSha256,
            ProductionOcrConfigurationScope.ApprovedProduction);

    private static WorkflowVisionEnvelope CreateEnvelope(
        ProductionWorkflowDetectionRequest request,
        OcrResult result,
        ModelIdentity model,
        InferenceProvider provider,
        WorkflowVisionTiming timing,
        IReadOnlyList<string> warnings) =>
        new(
            contractVersion: 1,
            request.RunId,
            request.ProjectId,
            request.Panel.ImportedPanel.PanelId,
            OcrContract.Stage,
            result.StageVersion,
            request.Image.Sha256,
            new WorkflowVisionModel(
                model.ModelId,
                model.Version,
                model.Sha256.ToLowerInvariant(),
                ProviderName(provider)),
            timing,
            result.Confidence,
            warnings,
            request.Transforms,
            OcrContract.CoordinateSpace);

    private static void ValidateInput(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster originalRaster,
        OcrRectangle plotBounds)
    {
        bool invalidPlot = !plotBounds.IsValid ||
            plotBounds.Left < 0 ||
            plotBounds.Top < 0 ||
            plotBounds.Right > originalRaster.Width ||
            plotBounds.Bottom > originalRaster.Height;
        if (request.ImageVariant != WorkflowImageVariant.Original ||
            request.Image.Variant != WorkflowImageVariant.Original ||
            originalRaster.Variant != WorkflowImageVariant.Original ||
            originalRaster.OriginalToFrame != GraphReader.Markers.Detection.MarkerAffineTransform.Identity ||
            originalRaster.Width != request.Image.Width ||
            originalRaster.Height != request.Image.Height ||
            !string.Equals(
                originalRaster.InputSha256,
                request.Image.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.Panel.Original.Sha256,
                request.Image.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            invalidPlot)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Production OCR requires the checksum-matched immutable original raster and plot bounds inside that raster.",
                "Decode the current original image and recompute plot geometry before OCR.");
        }
    }

    private static void ValidateOutput(
        ProductionWorkflowDetectionRequest request,
        ProductionDecodedRaster raster,
        OcrResult result)
    {
        bool identityMismatch = result.ContractVersion != OcrContract.Version ||
            !Guid.TryParse(result.ProjectId, out Guid resultProjectId) ||
            resultProjectId != request.ProjectId ||
            !Guid.TryParse(result.PanelId, out Guid resultPanelId) ||
            resultPanelId != request.Panel.ImportedPanel.PanelId ||
            !string.Equals(result.Stage, OcrContract.Stage, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(result.StageVersion) ||
            !string.Equals(result.InputSha256, request.Image.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.CoordinateSpace, OcrContract.CoordinateSpace, StringComparison.Ordinal) ||
            result.Regions.Any(region =>
                !string.Equals(region.CoordinateSpace, OcrContract.CoordinateSpace, StringComparison.Ordinal) ||
                !PolygonIsBounded(region.Polygon, raster.Width, raster.Height)) ||
            result.Masks.Any(mask =>
                !string.Equals(mask.CoordinateSpace, OcrContract.CoordinateSpace, StringComparison.Ordinal) ||
                !PolygonIsBounded(mask.Polygon, raster.Width, raster.Height));
        if (identityMismatch)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "The OCR pipeline returned evidence for a different project, panel, image, contract, or coordinate space.",
                "Reject the result and rerun the checksum-bound OCR pipeline.");
        }
    }

    private static bool PolygonIsBounded(OcrPolygon polygon, int width, int height) =>
        polygon.Points.All(point =>
            point.IsFinite &&
            point.X >= 0 && point.X <= width &&
            point.Y >= 0 && point.Y <= height);

    private static ModelIdentity ValidateModel(ModelIdentity model, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(model, parameterName);
        model.Validate();
        return model;
    }

    private static ProductionOcrConfigurationScope RequireUnapprovedDirectConstruction(bool isApproved) =>
        !isApproved
            ? ProductionOcrConfigurationScope.UnapprovedLocalSyntheticCandidate
            : throw new ArgumentException(
                "Approved OCR adapters must be created by a checksum-gated production factory.",
                nameof(isApproved));

    private static string ValidateSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != 64 ||
            value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A canonical SHA-256 value is required.", parameterName);
        }

        return value.ToLowerInvariant();
    }

    private static InferenceProvider ValidateProvider(InferenceProvider provider, string parameterName) =>
        provider is InferenceProvider.Cpu or InferenceProvider.DirectMl
            ? provider
            : throw new ArgumentOutOfRangeException(
                parameterName,
                provider,
                "Production OCR supports only CPU or DirectML execution evidence.");

    private static string ProviderName(InferenceProvider provider) => provider switch
    {
        InferenceProvider.Cpu => "cpu",
        InferenceProvider.DirectMl => "directml",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    internal static LocalOnnxTextRegionDetectorOptions ReadDetectionOptions(
        ModelIdentity identity,
        string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = document.RootElement;
        JsonElement input = SingleObject(root, "inputs", "OCR detection");
        JsonElement output = SingleObject(root, "outputs", "OCR detection");
        RequireString(input, "element_type", "float32", "OCR detection input");
        RequireString(input, "layout", "NCHW", "OCR detection input");
        RequireShape(input, "shape", ["1", "3", "H", "W"], "OCR detection input");
        OcrTensorColorMode inputColorMode = ReadInputColorMode(input, "OCR detection input");
        RequireString(output, "element_type", "float32", "OCR detection output");
        RequireString(output, "layout", "NCHW", "OCR detection output");
        RequireShape(output, "shape", ["1", "1", "H", "W"], "OCR detection output");
        RequireStringArray(output, "channels", ["text_probability"], "OCR detection output");
        string activation = RequiredString(output, "activation", "OCR detection output");
        OcrDetectionOutputActivation outputActivation = activation switch
        {
            "probability" => OcrDetectionOutputActivation.Probability,
            "probability_with_1e-5_clamp" => OcrDetectionOutputActivation.ProbabilityWithParityTolerance,
            "sigmoid_logit" => OcrDetectionOutputActivation.SigmoidLogit,
            _ => throw new InvalidDataException(
                "OCR detection output activation must be probability, probability_with_1e-5_clamp, or sigmoid_logit."),
        };

        JsonElement preprocessing = RequiredObject(root, "preprocessing", "OCR detection manifest");
        RequireBgrChannelOrder(preprocessing, inputColorMode, "OCR detection preprocessing");
        float[] means = RequiredSingles(preprocessing, "channel_means", 3, "OCR detection preprocessing");
        float[] scales = RequiredSingles(preprocessing, "channel_scales", 3, "OCR detection preprocessing");
        JsonElement postprocessing = RequiredObject(root, "postprocessing", "OCR detection manifest");
        RequireString(
            postprocessing,
            "algorithm",
            "db_postprocess_v1",
            "OCR detection postprocessing");
        int maximumSideLength = RequiredReviewedInt32(
            preprocessing,
            "maximum_side_length",
            960,
            "OCR detection preprocessing");
        int dimensionMultiple = RequiredReviewedInt32(
            preprocessing,
            "dimension_multiple",
            128,
            "OCR detection preprocessing");
        RequireString(
            postprocessing,
            "score_mode",
            "fast",
            "OCR detection postprocessing");
        float probabilityThreshold = RequiredReviewedSingle(
            postprocessing,
            "probability_threshold",
            0.30f,
            "OCR detection postprocessing");
        float boxConfidenceThreshold = RequiredReviewedSingle(
            postprocessing,
            "box_confidence_threshold",
            0.60f,
            "OCR detection postprocessing");
        double unclipRatio = RequiredReviewedDouble(
            postprocessing,
            "unclip_ratio",
            1.5,
            "OCR detection postprocessing");
        int minimumSideLength = RequiredReviewedInt32(
            postprocessing,
            "minimum_side_length",
            3,
            "OCR detection postprocessing");
        int maximumRegions = RequiredReviewedInt32(
            postprocessing,
            "maximum_regions",
            1000,
            "OCR detection postprocessing");
        var options = new LocalOnnxTextRegionDetectorOptions(identity)
        {
            MaximumSideLength = maximumSideLength,
            DimensionMultiple = dimensionMultiple,
            InputChannels = 3,
            InputLayout = OcrTensorLayout.ChannelsFirst,
            InputColorMode = inputColorMode,
            ChannelMeans = means,
            ChannelScales = scales,
            InputName = RequiredString(input, "name", "OCR detection input"),
            OutputName = RequiredString(output, "name", "OCR detection output"),
            StageVersion = identity.Version,
            OutputActivation = outputActivation,
            PostprocessAlgorithm = OcrDetectionPostprocessAlgorithm.DbPostprocessV1,
            DbScoreMode = OcrDbScoreMode.FastMiniBox,
            ProbabilityThreshold = probabilityThreshold,
            BoxConfidenceThreshold = boxConfidenceThreshold,
            UnclipRatio = unclipRatio,
            MinimumSideLength = minimumSideLength,
            MaximumRegions = maximumRegions,
            AllowedProviders = [InferenceProvider.Cpu],
        };
        LocalOnnxTextRegionDetector.ValidateOptions(options);
        return options;
    }

    internal static (LocalOnnxTextRecognizerOptions Recognizer, OcrPipelineOptions Pipeline)
        ReadRecognitionOptions(ModelIdentity identity, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = document.RootElement;
        JsonElement input = SingleObject(root, "inputs", "OCR recognition");
        JsonElement output = SingleObject(root, "outputs", "OCR recognition");
        RequireString(input, "element_type", "float32", "OCR recognition input");
        RequireString(input, "layout", "NCHW", "OCR recognition input");
        JsonElement inputShape = RequiredArray(input, "shape", "OCR recognition input");
        bool dynamicInputWidth = inputShape.GetArrayLength() == 4 &&
            StringValueEquals(inputShape[3], "W");
        bool fixedInputWidth = inputShape.GetArrayLength() == 4 &&
            TryReadInt32(inputShape[3], out int declaredInputWidth) && declaredInputWidth == 320;
        if (inputShape.GetArrayLength() != 4 ||
            !StringValueEquals(inputShape[0], "N") ||
            !TryReadInt32(inputShape[1], out int channels) || channels != 3 ||
            !TryReadInt32(inputShape[2], out int inputHeight) || inputHeight != 48 ||
            (!dynamicInputWidth && !fixedInputWidth))
        {
            throw new InvalidDataException(
                "OCR recognition input shape must be reviewed [N,3,48,320] or dynamic [N,3,48,W].");
        }

        const int inputWidth = 320;

        OcrTensorColorMode inputColorMode = ReadInputColorMode(input, "OCR recognition input");
        RequireString(output, "element_type", "float32", "OCR recognition output");
        string outputLayout = RequiredString(output, "layout", "OCR recognition output");
        OcrOutputLayout runtimeOutputLayout = outputLayout switch
        {
            "NTC" => OcrOutputLayout.BatchTimeClass,
            "TNC" => OcrOutputLayout.TimeBatchClass,
            _ => throw new InvalidDataException("OCR recognition output layout must be NTC or TNC."),
        };
        RequireShape(
            output,
            "shape",
            runtimeOutputLayout == OcrOutputLayout.BatchTimeClass
                ? ["N", "T", "C"]
                : ["T", "N", "C"],
            "OCR recognition output");
        string alphabet = RequiredString(output, "alphabet", "OCR recognition output");
        int? expectedTimeSteps = dynamicInputWidth
            ? null
            : RequiredInt32(output, "time_steps", "OCR recognition output");
        if (dynamicInputWidth && output.TryGetProperty("time_steps", out _))
        {
            throw new InvalidDataException(
                "Dynamic OCR recognition output must not declare one fixed time_steps value.");
        }
        int blankClassIndex = RequiredInt32(output, "blank_class_index", "OCR recognition output");
        JsonElement preprocessing = RequiredObject(root, "preprocessing", "OCR recognition manifest");
        RequireBgrChannelOrder(preprocessing, inputColorMode, "OCR recognition preprocessing");
        float[] means = RequiredSingles(preprocessing, "channel_means", 3, "OCR recognition preprocessing");
        float[] scales = RequiredSingles(preprocessing, "channel_scales", 3, "OCR recognition preprocessing");
        int maximumInputWidth = inputWidth;
        if (dynamicInputWidth)
        {
            RequireString(
                preprocessing,
                "width_policy",
                "paddle_batch_max_wh_ratio_v1",
                "OCR recognition preprocessing");
            _ = RequiredReviewedInt32(
                preprocessing,
                "minimum_width",
                inputWidth,
                "OCR recognition preprocessing");
            maximumInputWidth = RequiredReviewedInt32(
                preprocessing,
                "maximum_width",
                4096,
                "OCR recognition preprocessing");
        }
        JsonElement postprocessing = RequiredObject(root, "postprocessing", "OCR recognition manifest");
        string postprocessingAlgorithm = RequiredString(
            postprocessing,
            "algorithm",
            "OCR recognition postprocessing");
        if (!string.Equals(
                postprocessingAlgorithm,
                "ctc_greedy_alternatives_v1",
                StringComparison.Ordinal) &&
            !string.Equals(
                postprocessingAlgorithm,
                OfficialRecognitionSpacingV2Algorithm,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "OCR recognition postprocessing algorithm is not reviewed for production.");
        }
        if (string.Equals(
                postprocessingAlgorithm,
                OfficialRecognitionSpacingV2Algorithm,
                StringComparison.Ordinal))
        {
            ValidateOfficialRecognitionSpacingV2(postprocessing);
        }
        int maximumAlternatives = RequiredInt32(
            postprocessing,
            "maximum_alternatives",
            "OCR recognition postprocessing");
        var recognizer = new LocalOnnxTextRecognizerOptions(identity, alphabet)
        {
            InputWidth = inputWidth,
            InputHeight = inputHeight,
            DynamicInputWidth = dynamicInputWidth,
            MaximumInputWidth = maximumInputWidth,
            InputChannels = channels,
            InputLayout = OcrTensorLayout.ChannelsFirst,
            InputColorMode = inputColorMode,
            OutputLayout = runtimeOutputLayout,
            ExpectedTimeSteps = expectedTimeSteps,
            BlankClassIndex = blankClassIndex,
            MaximumAlternatives = maximumAlternatives,
            InputName = RequiredString(input, "name", "OCR recognition input"),
            OutputName = RequiredString(output, "name", "OCR recognition output"),
            StageVersion = identity.Version,
            ChannelMeans = means,
            ChannelScales = scales,
            AllowedProviders = [InferenceProvider.Cpu],
        };
        var pipeline = new OcrPipelineOptions
        {
            StageVersion = identity.Version,
            CropWidth = inputWidth,
            CropHeight = inputHeight,
            CropWidthMode = dynamicInputWidth
                ? OcrCropWidthMode.PaddleBatchMaximumAspectRatio
                : OcrCropWidthMode.Fixed,
            MaximumCropWidth = maximumInputWidth,
        };
        LocalOnnxTextRecognizer.ValidateOptions(recognizer);
        return (recognizer, pipeline);
    }

    internal static bool UsesOfficialRecognitionSpacingV2Manifest(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement postprocessing = RequiredObject(
            document.RootElement,
            "postprocessing",
            "OCR recognition manifest");
        return string.Equals(
            RequiredString(postprocessing, "algorithm", "OCR recognition postprocessing"),
            OfficialRecognitionSpacingV2Algorithm,
            StringComparison.Ordinal);
    }

    private static void ValidateOfficialRecognitionSpacingV2(JsonElement postprocessing)
    {
        _ = RequiredReviewedInt32(
            postprocessing,
            "minimum_gap_pixels",
            OfficialRecognitionSpacingV2Postprocessor.MinimumGapPixels,
            "OCR recognition spacing V2 postprocessing");
        _ = RequiredReviewedDouble(
            postprocessing,
            "minimum_gap_to_ink_height_ratio",
            OfficialRecognitionSpacingV2Postprocessor.MinimumGapToInkHeightRatio,
            "OCR recognition spacing V2 postprocessing");
        _ = RequiredReviewedInt32(
            postprocessing,
            "minimum_source_groups",
            OfficialRecognitionSpacingV2Postprocessor.MinimumSourceGroups,
            "OCR recognition spacing V2 postprocessing");
        _ = RequiredReviewedDouble(
            postprocessing,
            "foreground_contrast_fraction",
            OfficialRecognitionSpacingV2Postprocessor.ForegroundContrastFraction,
            "OCR recognition spacing V2 postprocessing");
        _ = RequiredReviewedDouble(
            postprocessing,
            "minimum_foreground_contrast",
            OfficialRecognitionSpacingV2Postprocessor.MinimumForegroundContrast,
            "OCR recognition spacing V2 postprocessing");
        _ = RequiredReviewedDouble(
            postprocessing,
            "capital_i_minimum_width_height_ratio",
            OfficialRecognitionSpacingV2Postprocessor.CapitalIMinimumWidthHeightRatio,
            "OCR recognition spacing V2 postprocessing");
        _ = RequiredReviewedDouble(
            postprocessing,
            "capital_i_minimum_top_coverage",
            OfficialRecognitionSpacingV2Postprocessor.CapitalIMinimumTopCoverage,
            "OCR recognition spacing V2 postprocessing");
        _ = RequiredReviewedDouble(
            postprocessing,
            "capital_i_minimum_bottom_coverage",
            OfficialRecognitionSpacingV2Postprocessor.CapitalIMinimumBottomCoverage,
            "OCR recognition spacing V2 postprocessing");
        _ = RequiredReviewedInt32(
            postprocessing,
            "maximum_character_count",
            OfficialRecognitionSpacingV2Postprocessor.MaximumCharacterCount,
            "OCR recognition spacing V2 postprocessing");
        _ = RequiredReviewedInt32(
            postprocessing,
            "maximum_source_groups",
            OfficialRecognitionSpacingV2Postprocessor.MaximumSourceGroups,
            "OCR recognition spacing V2 postprocessing");
    }

    private static void RequireTask(ResolvedProductionModel model, string expected)
    {
        if (!string.Equals(model.Task, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Resolved model task '{model.Task}' is not {expected}.");
        }
    }

    private static void RequireCpu(ResolvedProductionModel model)
    {
        if (!model.AvailableProviders.Contains(InferenceProvider.Cpu))
        {
            throw new InvalidDataException(
                $"Resolved OCR model '{model.Identity.ModelId}' lacks mandatory CPU approval.");
        }
    }

    private static JsonElement SingleObject(JsonElement root, string propertyName, string label)
    {
        JsonElement values = RequiredArray(root, propertyName, label);
        if (values.GetArrayLength() != 1 || values[0].ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} '{propertyName}' must contain exactly one object.");
        }

        return values[0];
    }

    private static JsonElement RequiredObject(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be an object.");
        }

        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be an array.");
        }

        return value;
    }

    private static string RequiredString(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static void RequireString(
        JsonElement parent,
        string propertyName,
        string expected,
        string label)
    {
        string actual = RequiredString(parent, propertyName, label);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' must be '{expected}', found '{actual}'.");
        }
    }

    private static void RequireStringArray(
        JsonElement parent,
        string propertyName,
        string[] expected,
        string label)
    {
        JsonElement values = RequiredArray(parent, propertyName, label);
        string?[] actual = values.EnumerateArray()
            .Select(static value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' does not match the frozen order.");
        }
    }

    private static OcrTensorColorMode ReadInputColorMode(JsonElement input, string label)
    {
        JsonElement values = RequiredArray(input, "channels", label);
        string?[] actual = values.EnumerateArray()
            .Select(static value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .ToArray();
        if (actual.SequenceEqual(new[] { "b", "g", "r" }, StringComparer.Ordinal))
        {
            return OcrTensorColorMode.Bgr;
        }

        throw new InvalidDataException(
            $"{label} field 'channels' must use the frozen [b,g,r] production order.");
    }

    private static void RequireBgrChannelOrder(
        JsonElement preprocessing,
        OcrTensorColorMode colorMode,
        string label)
    {
        if (colorMode != OcrTensorColorMode.Bgr)
        {
            throw new InvalidDataException($"{label} must select the BGR production color mode.");
        }

        RequireString(preprocessing, "channel_order", "BGR", label);
    }

    private static void RequireShape(
        JsonElement parent,
        string propertyName,
        string[] expected,
        string label)
    {
        JsonElement values = RequiredArray(parent, propertyName, label);
        string?[] actual = values.EnumerateArray().Select(static value => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt32(out int integer) =>
                integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        }).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' does not match [{string.Join(',', expected)}].");
        }
    }

    private static float[] RequiredSingles(
        JsonElement parent,
        string propertyName,
        int count,
        string label)
    {
        JsonElement values = RequiredArray(parent, propertyName, label);
        float[] result;
        try
        {
            result = values.EnumerateArray().Select(static value => value.GetSingle()).ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must contain float32 values.", exception);
        }

        if (result.Length != count || result.Any(static value => !float.IsFinite(value)))
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' must contain {count} finite values.");
        }

        return result;
    }

    private static float RequiredSingle(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float result) ||
            !float.IsFinite(result))
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be finite float32.");
        }

        return result;
    }

    private static float RequiredReviewedSingle(
        JsonElement parent,
        string propertyName,
        float expected,
        string label)
    {
        float actual = RequiredSingle(parent, propertyName, label);
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' must match the reviewed value {expected:R}, found {actual:R}.");
        }

        return actual;
    }

    private static double RequiredDouble(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) ||
            !double.IsFinite(result))
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be finite.");
        }

        return result;
    }

    private static double RequiredReviewedDouble(
        JsonElement parent,
        string propertyName,
        double expected,
        string label)
    {
        double actual = RequiredDouble(parent, propertyName, label);
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' must match the reviewed value {expected:R}, found {actual:R}.");
        }

        return actual;
    }

    private static int RequiredInt32(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            !TryReadInt32(value, out int result))
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be int32.");
        }

        return result;
    }

    private static int RequiredReviewedInt32(
        JsonElement parent,
        string propertyName,
        int expected,
        string label)
    {
        int actual = RequiredInt32(parent, propertyName, label);
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' must match the reviewed value {expected}, found {actual}.");
        }

        return actual;
    }

    private static bool TryReadInt32(JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }

    private static bool StringValueEquals(JsonElement value, string expected) =>
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static async Task ValidateLocalSyntheticDescriptorAsync(
        LocalSyntheticOcrModelDescriptor descriptor,
        string expectedTask,
        CancellationToken cancellationToken) =>
        await ValidateCandidateDescriptorAsync(
                descriptor.Identity,
                descriptor.ManifestPath,
                descriptor.ManifestSha256,
                expectedTask,
                "Local synthetic OCR",
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task ValidateCandidateDescriptorAsync(
        ModelIdentity identity,
        string manifestPath,
        string expectedManifestSha256,
        string expectedTask,
        string label,
        CancellationToken cancellationToken)
    {
        identity.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string manifestSha256 = ValidateSha256(
            expectedManifestSha256,
            nameof(expectedManifestSha256));
        await VerifyChecksumAsync(
                identity.FilePath,
                identity.Sha256,
                $"{label} {expectedTask} model",
                cancellationToken)
            .ConfigureAwait(false);
        await VerifyChecksumAsync(
                manifestPath,
                manifestSha256,
                $"{label} {expectedTask} manifest",
                cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} manifest root must be an object.");
        }

        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!propertyNames.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"{label} manifest contains duplicate root field '{property.Name}'.");
            }
        }

        RequireString(root, "model_id", identity.ModelId, label);
        RequireString(root, "model_version", identity.Version, label);
        RequireString(root, "task", expectedTask, label);
        string declaredModelSha256 = RequiredString(
            root,
            "sha256",
            label);
        if (!string.Equals(
                declaredModelSha256,
                identity.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{label} manifest field 'sha256' does not match the pinned model bytes.");
        }

        JsonElement files = RequiredArray(root, "files", label);
        string expectedFileName = Path.GetFileName(identity.FilePath);
        var declaredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool modelFileDeclared = false;
        foreach (JsonElement item in files.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidDataException(
                    $"{label} manifest files must be non-empty relative paths.");
            }

            string declaredPath = item.GetString()!.Replace('\\', '/');
            string[] segments = declaredPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (Path.IsPathRooted(declaredPath) || segments.Length == 0 ||
                segments.Any(static segment => segment is "." or "..") ||
                !declaredFiles.Add(declaredPath))
            {
                throw new InvalidDataException(
                    $"{label} manifest contains invalid or duplicate payload path '{declaredPath}'.");
            }

            modelFileDeclared |= string.Equals(
                Path.GetFileName(declaredPath),
                expectedFileName,
                StringComparison.OrdinalIgnoreCase);
        }

        if (!modelFileDeclared)
        {
            throw new InvalidDataException(
                $"{label} manifest files do not identify pinned model '{expectedFileName}'.");
        }
    }

    private static async Task VerifyChecksumAsync(
        string path,
        string expectedSha256,
        string label,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"The checksum-resolved {label} is missing: {path}");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        string actual = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The checksum-resolved {label} does not match its pinned SHA-256.");
        }
    }

    private static ProductionWorkflowStageException Failure(
        string code,
        string userMessageKey,
        string technicalMessage,
        string suggestedAction,
        IEnumerable<WorkflowVisionEnvelope>? completedEvidence = null) =>
        new(
            new ProductionWorkflowFailure(
                code,
                userMessageKey,
                technicalMessage,
                Recoverable: true,
                suggestedAction),
            completedEvidence);
}
