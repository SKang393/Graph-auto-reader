// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Inference;
using GraphReader.Markers.Detection;

namespace GraphReader.App.Integration.Workflow;

public interface IProductionMarkerCenterAdapter
{
    string AdapterId { get; }

    bool IsApproved { get; }

    ModelIdentity Model { get; }

    Task<ProductionMarkerCenterEvidence> DetectAsync(
        ProductionWorkflowDetectionRequest request,
        MarkerImageFrame originalImage,
        MarkerPolygon plotPolygon,
        MarkerImageFrame? enhancedImage,
        IReadOnlyList<WorkflowTransformProvenance>? enhancedTransforms,
        CancellationToken cancellationToken);
}

public sealed record ProductionMarkerCenterEvidence(
    WorkflowVisionEnvelope Envelope,
    IReadOnlyList<MarkerCenter> Markers,
    IReadOnlyList<MarkerFrameReport> Frames)
{
    internal ProposalMarkerCandidateDiagnosticResult? CandidateDiagnostics { get; init; }
}

/// <summary>
/// Binds a checksum-resolved marker-center manifest to the shared lazy
/// production ONNX runtime. OCR and artifact masks are supplied by the future
/// composite detection adapter and are validated before provider execution.
/// </summary>
public sealed class ProductionMarkerCenterAdapter : IProductionMarkerCenterAdapter
{
    private const string ProviderShapeBenchmark = "graphreader-inference-cpu-directml-parity";
    private static readonly string[] RequiredInputChannels =
        ["ink_probability", "text_mask", "artifact_mask"];
    private static readonly string[] RequiredOutputChannels =
        ["center_probability", "radius_pixels", "artifact_probability"];

    private readonly Lazy<IMarkerDetectionService> detector;
    private readonly MarkerDetectionOptions options;

    public ProductionMarkerCenterAdapter(
        ModelIdentity model,
        MarkerDetectionOptions options,
        bool isApproved,
        IMarkerDetectionService detector)
        : this(
            model,
            options,
            isApproved,
            () => detector ?? throw new ArgumentNullException(nameof(detector)))
    {
    }

    private ProductionMarkerCenterAdapter(
        ModelIdentity model,
        MarkerDetectionOptions options,
        bool isApproved,
        Func<IMarkerDetectionService> detectorFactory)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Model.Validate();
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(detectorFactory);
        IsApproved = isApproved;
        detector = new Lazy<IMarkerDetectionService>(
            detectorFactory,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string AdapterId => $"graphreader-marker-center:{Model.Sha256[..12].ToLowerInvariant()}";

    public bool IsApproved { get; }

    public ModelIdentity Model { get; }

    public static ProductionMarkerCenterAdapter Create(
        ResolvedProductionModel resolvedModel,
        ProductionInferenceRuntimeHost runtimeHost)
    {
        ArgumentNullException.ThrowIfNull(resolvedModel);
        ArgumentNullException.ThrowIfNull(runtimeHost);
        if (!string.Equals(resolvedModel.Task, "marker_center", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Resolved model task '{resolvedModel.Task}' is not marker_center.");
        }

        if (!resolvedModel.AvailableProviders.Contains(InferenceProvider.Cpu))
        {
            throw new InvalidDataException("The marker-center model lacks mandatory CPU provider approval.");
        }

        VerifyChecksum(
            resolvedModel.ManifestPath,
            resolvedModel.ManifestSha256,
            "marker-center manifest");
        MarkerDetectionOptions parsedOptions = ReadOptions(
            resolvedModel.ManifestPath,
            resolvedModel.Identity.Version);
        return new ProductionMarkerCenterAdapter(
            resolvedModel.Identity,
            parsedOptions,
            isApproved: true,
            () => new MarkerCenterDetector(runtimeHost.Runtime));
    }

    public async Task<ProductionMarkerCenterEvidence> DetectAsync(
        ProductionWorkflowDetectionRequest request,
        MarkerImageFrame originalImage,
        MarkerPolygon plotPolygon,
        MarkerImageFrame? enhancedImage,
        IReadOnlyList<WorkflowTransformProvenance>? enhancedTransforms,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(originalImage);
        ArgumentNullException.ThrowIfNull(plotPolygon);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsApproved)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
                "Errors.ModelNotFound",
                $"Marker-center adapter '{AdapterId}' is not production-approved.",
                "Install the exact approved marker-center model or continue in manual mode.");
        }

        if (request.ImageVariant != WorkflowImageVariant.Original)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Marker-center composition must begin from the retained immutable original image.",
                "Run marker-center detection from the original panel evidence.");
        }

        WorkflowTransformProvenance[] transforms = enhancedTransforms?.ToArray() ?? [];
        ValidateFrames(request, originalImage, enhancedImage, transforms);

        MarkerDetectionResult result;
        try
        {
            result = await detector.Value.DetectAsync(
                    new MarkerDetectionRequest(
                        request.ProjectId.ToString("D"),
                        request.Panel.ImportedPanel.PanelId.ToString("D"),
                        request.Image.Sha256,
                        Model,
                        originalImage,
                        plotPolygon,
                        options,
                        enhancedImage,
                        MarkerContract.Version,
                        TransformChain(transforms)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                $"Marker-center detection failed: {exception.Message}",
                "Retry on CPU or continue with manual marker editing.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ValidateResult(request, enhancedImage is not null, result);
        string? provider = ProviderName(result.Model.Provider);
        var envelope = new WorkflowVisionEnvelope(
            MarkerContract.Version,
            request.RunId,
            request.ProjectId,
            request.Panel.ImportedPanel.PanelId,
            MarkerContract.Stage,
            result.StageVersion,
            request.Image.Sha256,
            new WorkflowVisionModel(
                result.Model.ModelId,
                result.Model.Version,
                result.Model.Sha256,
                provider),
            new WorkflowVisionTiming(
                result.Timing.PreprocessMilliseconds,
                result.Timing.InferenceMilliseconds,
                result.Timing.PostprocessMilliseconds,
                result.Timing.TotalMilliseconds),
            result.Confidence,
            result.Warnings,
            transforms);
        return new ProductionMarkerCenterEvidence(envelope, result.Markers, result.Frames);
    }

    private static MarkerDetectionOptions ReadOptions(string manifestPath, string stageVersion)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = document.RootElement;
        JsonElement input = SingleObject(root, "inputs");
        JsonElement output = SingleObject(root, "outputs");
        RequireString(input, "element_type", "float32");
        RequireString(input, "layout", "NCHW");
        RequireString(output, "element_type", "float32");
        RequireString(output, "layout", "NCHW");
        RequireDynamicShape(input, "shape");
        RequireDynamicShape(output, "shape");
        RequireStringArray(input, "channels", RequiredInputChannels);
        RequireStringArray(output, "channels", RequiredOutputChannels);
        if (RequiredInt32(output, "output_stride") != 1)
        {
            throw new InvalidDataException("Marker-center output stride must be one.");
        }

        (int tensorWidth, int tensorHeight) = ReadProviderValidatedShape(root);
        JsonElement preprocessing = RequiredObject(root, "preprocessing");
        float normalizeMean = RequiredSingle(preprocessing, "normalization_mean");
        float normalizeScale = RequiredSingle(preprocessing, "normalization_scale");
        if (normalizeMean != 0 || normalizeScale != 1)
        {
            throw new InvalidDataException("Marker-center input must retain unnormalized [0,1] planes.");
        }

        JsonElement postprocessing = RequiredObject(root, "postprocessing");
        float centerThreshold = RequiredSingle(postprocessing, "center_threshold");
        float artifactThreshold = RequiredSingle(postprocessing, "artifact_threshold");
        int localMaximumWindow = RequiredInt32(postprocessing, "local_maximum_window");
        double minimumRadius = RequiredDouble(postprocessing, "minimum_radius_tensor_pixels");
        double minimumSuppression = RequiredDouble(
            postprocessing,
            "minimum_nms_distance_tensor_pixels");
        double radiusSuppressionScale = RequiredDouble(postprocessing, "radius_nms_scale");
        double consensusTolerance = ReadConsensusTolerance(postprocessing);
        double unmatchedScale = RequiredDouble(
            postprocessing,
            "unmatched_source_confidence_scale");
        if (centerThreshold is < 0 or > 1 || artifactThreshold is < 0 or > 1 ||
            localMaximumWindow != 9 || minimumRadius != 2.5 || minimumSuppression != 5 ||
            radiusSuppressionScale != 1.25 || consensusTolerance != 5 ||
            unmatchedScale is < 0 or > 1)
        {
            throw new InvalidDataException(
                "Marker-center postprocessing does not match the frozen runtime-v2 contract.");
        }

        var tensor = new MarkerModelTensorContract(
            RequiredString(input, "name"),
            RequiredString(output, "name"),
            tensorWidth,
            tensorHeight,
            3,
            MarkerTensorLayout.ChannelsFirst,
            tensorWidth,
            tensorHeight,
            3,
            MarkerTensorLayout.ChannelsFirst,
            CenterChannelIndex: 0,
            RadiusChannelIndex: 1,
            ArtifactChannelIndex: 2,
            MarkerHeadActivation.Identity,
            MarkerHeadActivation.Identity,
            RadiusScale: 1,
            normalizeMean,
            normalizeScale);
        return new MarkerDetectionOptions(tensor)
        {
            CenterThreshold = centerThreshold,
            ArtifactThreshold = artifactThreshold,
            MaskThreshold = artifactThreshold,
            LocalMaximumWindow = localMaximumWindow,
            MinimumRadiusGridPixels = minimumRadius,
            MinimumSuppressionDistanceGridPixels = minimumSuppression,
            RadiusSuppressionScale = radiusSuppressionScale,
            ConsensusToleranceOriginalPixels = consensusTolerance,
            UnmatchedSourceConfidenceScale = unmatchedScale,
            StageVersion = stageVersion,
        };
    }

    private static (int Width, int Height) ReadProviderValidatedShape(JsonElement root)
    {
        JsonElement benchmarks = RequiredArray(root, "benchmarks");
        JsonElement[] matches = benchmarks.EnumerateArray()
            .Where(element =>
                element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("profile", out JsonElement profile) &&
                profile.ValueKind == JsonValueKind.String &&
                string.Equals(profile.GetString(), ProviderShapeBenchmark, StringComparison.Ordinal) &&
                element.TryGetProperty("status", out JsonElement status) &&
                status.ValueKind == JsonValueKind.String &&
                string.Equals(status.GetString(), "pass", StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Marker-center manifest must contain one passing '{ProviderShapeBenchmark}' benchmark.");
        }

        int[] input = ReadFixedShape(matches[0], "input_shape");
        int[] output = ReadFixedShape(matches[0], "output_shape");
        if (input.Length != 4 || output.Length != 4 ||
            input[0] != 1 || input[1] != 3 || output[0] != 1 || output[1] != 3 ||
            input[2] != output[2] || input[3] != output[3] ||
            input[2] is < 32 or > 2048 || input[3] is < 32 or > 2048)
        {
            throw new InvalidDataException(
                "Marker-center provider evidence must bind equal bounded NCHW [1,3,H,W] input and output shapes.");
        }

        return (input[3], input[2]);
    }

    private static int[] ReadFixedShape(JsonElement parent, string propertyName)
    {
        JsonElement values = RequiredArray(parent, propertyName);
        try
        {
            return values.EnumerateArray().Select(static value => value.GetInt32()).ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException(
                $"Marker-center benchmark '{propertyName}' must contain integers.",
                exception);
        }
    }

    private static double ReadConsensusTolerance(JsonElement postprocessing)
    {
        if (!postprocessing.TryGetProperty("consensus", out JsonElement consensus) ||
            consensus.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Marker-center postprocessing field 'consensus' is required.");
        }

        string description = consensus.GetString()!;
        const string expected = "minimum-cost maximum one-to-one matching within 5 original pixels for original and enhanced detections";
        if (!string.Equals(description, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Marker-center consensus contract does not match runtime v2.");
        }

        return 5;
    }

    private static void ValidateFrames(
        ProductionWorkflowDetectionRequest request,
        MarkerImageFrame originalImage,
        MarkerImageFrame? enhancedImage,
        WorkflowTransformProvenance[] enhancedTransforms)
    {
        if (originalImage.SourceImage != MarkerSourceImage.Original ||
            originalImage.Width != request.Image.Width ||
            originalImage.Height != request.Image.Height ||
            originalImage.OriginalToFrame != MarkerAffineTransform.Identity ||
            !IsValidFrame(originalImage))
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Marker-center original frame does not match immutable original panel evidence.",
                "Regenerate the original marker frame without modifying or transforming source pixels.");
        }

        if (enhancedImage is null && enhancedTransforms.Length != 0)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Enhanced transform provenance was supplied without an enhanced marker frame.",
                "Remove stale transforms or regenerate the enhanced derivative.");
        }

        if (enhancedImage is not null &&
            (enhancedImage.SourceImage != MarkerSourceImage.Enhanced ||
                enhancedTransforms.Length == 0 ||
                !IsValidFrame(enhancedImage) ||
                enhancedTransforms.Select(static transform => transform.TransformId)
                    .Distinct(StringComparer.Ordinal).Count() != enhancedTransforms.Length))
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Enhanced marker-center detection requires an enhanced frame and reversible transform provenance.",
                "Regenerate the enhanced derivative with retained original-pixel transforms.");
        }
    }

    private static bool IsValidFrame(MarkerImageFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.ChannelCount != 1 ||
            !frame.OriginalToFrame.IsInvertible)
        {
            return false;
        }

        int pixelCount;
        try
        {
            pixelCount = checked(frame.Width * frame.Height);
        }
        catch (OverflowException)
        {
            return false;
        }

        return frame.ChannelsFirstPixels.Length == pixelCount &&
            frame.OcrMask.Width == frame.Width && frame.OcrMask.Height == frame.Height &&
            frame.OcrMask.Values.Length == pixelCount &&
            frame.ArtifactMask.Width == frame.Width && frame.ArtifactMask.Height == frame.Height &&
            frame.ArtifactMask.Values.Length == pixelCount &&
            AreNormalized(frame.ChannelsFirstPixels.Span) &&
            AreNormalized(frame.OcrMask.Values.Span) &&
            AreNormalized(frame.ArtifactMask.Values.Span);
    }

    private static bool AreNormalized(ReadOnlySpan<float> values)
    {
        foreach (float value in values)
        {
            if (!float.IsFinite(value) || value < 0 || value > 1)
            {
                return false;
            }
        }

        return true;
    }

    private void ValidateResult(
        ProductionWorkflowDetectionRequest request,
        bool usedEnhancedImage,
        MarkerDetectionResult result)
    {
        if (result is null)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Marker-center detector returned no result.",
                "Retry marker detection or continue with manual marker editing.");
        }

        if (!result.Succeeded)
        {
            MarkerDetectionFailure? failure = result.Failure;
            throw Failure(
                failure?.Code ?? ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                failure?.UserMessageKey ?? "Errors.DetectionEvidenceRejected",
                failure?.TechnicalMessage ?? "Marker-center detection failed without a diagnostic.",
                failure?.SuggestedAction ?? "Retry marker detection or continue manually.",
                failure?.Recoverable ?? true);
        }

        bool invalidSource = result.Markers.Any(marker =>
            marker.SourceImage is not MarkerSourceImage.Original &&
            (!usedEnhancedImage || marker.SourceImage is not (
                MarkerSourceImage.Enhanced or MarkerSourceImage.Consensus)));
        if (result.ContractVersion != MarkerContract.Version ||
            !string.Equals(result.ProjectId, request.ProjectId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(result.PanelId, request.Panel.ImportedPanel.PanelId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(result.Stage, MarkerContract.Stage, StringComparison.Ordinal) ||
            !string.Equals(result.InputSha256, request.Image.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.CoordinateSpace, MarkerContract.CoordinateSpace, StringComparison.Ordinal) ||
            !string.Equals(result.Model.ModelId, Model.ModelId, StringComparison.Ordinal) ||
            !string.Equals(result.Model.Version, Model.Version, StringComparison.Ordinal) ||
            !string.Equals(result.Model.Sha256, Model.Sha256, StringComparison.OrdinalIgnoreCase) ||
            result.Markers.Select(static marker => marker.MarkerId)
                .Distinct(StringComparer.Ordinal).Count() != result.Markers.Count ||
            result.Markers.Any(static marker =>
                string.IsNullOrWhiteSpace(marker.MarkerId) ||
                !marker.Center.IsFinite || !double.IsFinite(marker.Radius) || marker.Radius <= 0 ||
                !string.Equals(marker.CoordinateSpace, MarkerContract.CoordinateSpace, StringComparison.Ordinal)) ||
            invalidSource ||
            result.Model.Provider is null ||
            result.Model.Provider == InferenceProvider.Fake)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Marker-center detector returned mismatched identity, provider, coordinate, or source evidence.",
                "Reject the detector result and verify the exact production model composition.");
        }
    }

    private static string TransformChain(WorkflowTransformProvenance[] transforms) =>
        transforms.Length == 0
            ? "identity"
            : "identity>enhanced:" + string.Join('>', transforms.Select(static transform => transform.TransformId));

    private static string? ProviderName(InferenceProvider? provider) => provider switch
    {
        InferenceProvider.Cpu => "cpu",
        InferenceProvider.DirectMl => "directml",
        null => null,
        _ => throw Failure(
            ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
            "Errors.DetectionEvidenceRejected",
            "Marker-center detector used a non-production execution provider.",
            "Rerun with DirectML or mandatory CPU fallback."),
    };

    private static JsonElement SingleObject(JsonElement root, string propertyName)
    {
        JsonElement values = RequiredArray(root, propertyName);
        if (values.GetArrayLength() != 1 || values[0].ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Marker-center manifest '{propertyName}' must contain one object.");
        }

        return values[0];
    }

    private static JsonElement RequiredArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Marker-center manifest field '{propertyName}' must be an array.");
        }

        return value;
    }

    private static JsonElement RequiredObject(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Marker-center manifest field '{propertyName}' must be an object.");
        }

        return value;
    }

    private static string RequiredString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Marker-center manifest field '{propertyName}' must be a string.");
        }

        return value.GetString()!;
    }

    private static void RequireString(JsonElement parent, string propertyName, string expected)
    {
        string actual = RequiredString(parent, propertyName);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Marker-center manifest field '{propertyName}' must equal '{expected}'.");
        }
    }

    private static float RequiredSingle(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetSingle(out float result) || !float.IsFinite(result))
        {
            throw new InvalidDataException($"Marker-center manifest field '{propertyName}' must be finite float32.");
        }

        return result;
    }

    private static double RequiredDouble(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) || !double.IsFinite(result))
        {
            throw new InvalidDataException($"Marker-center manifest field '{propertyName}' must be finite.");
        }

        return result;
    }

    private static int RequiredInt32(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
        {
            throw new InvalidDataException($"Marker-center manifest field '{propertyName}' must be an integer.");
        }

        return result;
    }

    private static void RequireDynamicShape(JsonElement parent, string propertyName)
    {
        JsonElement values = RequiredArray(parent, propertyName);
        if (values.GetArrayLength() != 4 ||
            !string.Equals(values[0].GetString(), "N", StringComparison.Ordinal) ||
            values[1].GetInt32() != 3 ||
            !string.Equals(values[2].GetString(), "H", StringComparison.Ordinal) ||
            !string.Equals(values[3].GetString(), "W", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Marker-center tensor shape must be dynamic NCHW [N,3,H,W].");
        }
    }

    private static void RequireStringArray(
        JsonElement parent,
        string propertyName,
        string[] expected)
    {
        JsonElement values = RequiredArray(parent, propertyName);
        string?[] actual = values.EnumerateArray()
            .Select(static value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .ToArray();
        if (actual.Length != expected.Length ||
            actual.Where(static value => value is not null).Cast<string>()
                .SequenceEqual(expected, StringComparer.Ordinal) is false)
        {
            throw new InvalidDataException(
                $"Marker-center manifest field '{propertyName}' does not match the frozen order.");
        }
    }

    private static void VerifyChecksum(string path, string expectedSha256, string label)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"The checksum-resolved {label} is missing: {path}");
        }

        string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The checksum-resolved {label} changed after model-store validation.");
        }
    }

    private static ProductionWorkflowStageException Failure(
        string code,
        string userMessageKey,
        string technicalMessage,
        string suggestedAction,
        bool recoverable = true) =>
        new(new ProductionWorkflowFailure(
            code,
            userMessageKey,
            technicalMessage,
            recoverable,
            suggestedAction));
}
