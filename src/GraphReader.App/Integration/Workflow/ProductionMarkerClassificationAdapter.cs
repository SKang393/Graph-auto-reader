// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Inference;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;

namespace GraphReader.App.Integration.Workflow;

public interface IProductionMarkerClassificationAdapter
{
    string AdapterId { get; }

    bool IsApproved { get; }

    ModelIdentity Model { get; }

    Task<ProductionMarkerClassificationEvidence> ClassifyAsync(
        ProductionWorkflowDetectionRequest request,
        MarkerImageFrame image,
        IReadOnlyList<MarkerCenter> markers,
        CancellationToken cancellationToken);

    Task<ProductionMarkerClassificationEvidence> ClassifyAsync(
        ProductionWorkflowDetectionRequest request,
        MarkerImageFrame image,
        IReadOnlyList<MarkerCenter> markers,
        IReadOnlyDictionary<string, MarkerRectangle> originalPixelContentBounds,
        CancellationToken cancellationToken) => originalPixelContentBounds.Count == 0
            ? ClassifyAsync(request, image, markers, cancellationToken)
            : throw new NotSupportedException("This classifier does not support measured symbol content isolation.");
}

public sealed record ProductionMarkerClassificationEvidence(
    WorkflowVisionEnvelope Envelope,
    IReadOnlyList<ClassifiedMarker> Markers);

/// <summary>
/// Binds a checksum-resolved marker-classifier manifest to the shared lazy
/// production ONNX runtime. Marker centers remain a separate required stage.
/// </summary>
public sealed class ProductionMarkerClassificationAdapter : IProductionMarkerClassificationAdapter
{
    private const int FixedBatchSize = 64;
    private static readonly string[] RequiredShapeOrder =
    [
        "circle", "square", "triangle_up", "triangle_down", "diamond",
        "star", "asterisk", "cross", "other",
    ];
    private static readonly string[] RequiredFillOrder = ["filled", "open", "unknown"];
    private static readonly string[] RequiredOutputOrder =
    [
        "shape_probabilities[9]",
        "fill_probabilities[3]",
        "artifact_probability[1]",
        "l2_normalized_embedding[12]",
    ];

    private readonly Lazy<IMarkerClassificationService> classifier;
    private readonly MarkerClassificationOptions options;

    public ProductionMarkerClassificationAdapter(
        ModelIdentity model,
        MarkerClassificationOptions options,
        bool isApproved,
        IMarkerClassificationService classifier)
        : this(
            model,
            options,
            isApproved,
            () => classifier ?? throw new ArgumentNullException(nameof(classifier)))
    {
    }

    private ProductionMarkerClassificationAdapter(
        ModelIdentity model,
        MarkerClassificationOptions options,
        bool isApproved,
        Func<IMarkerClassificationService> classifierFactory)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Model.Validate();
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(classifierFactory);
        IsApproved = isApproved;
        classifier = new Lazy<IMarkerClassificationService>(
            classifierFactory,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string AdapterId => $"graphreader-marker-classifier:{Model.Sha256[..12].ToLowerInvariant()}";

    public bool IsApproved { get; }

    public ModelIdentity Model { get; }

    public static ProductionMarkerClassificationAdapter Create(
        ResolvedProductionModel resolvedModel,
        ProductionInferenceRuntimeHost runtimeHost)
    {
        ArgumentNullException.ThrowIfNull(resolvedModel);
        ArgumentNullException.ThrowIfNull(runtimeHost);
        if (!string.Equals(resolvedModel.Task, "marker_classifier", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Resolved model task '{resolvedModel.Task}' is not marker_classifier.");
        }

        if (!resolvedModel.AvailableProviders.Contains(InferenceProvider.Cpu))
        {
            throw new InvalidDataException("The marker classifier lacks mandatory CPU provider approval.");
        }

        VerifyChecksum(
            resolvedModel.ManifestPath,
            resolvedModel.ManifestSha256,
            "marker-classifier manifest");
        MarkerClassificationOptions parsedOptions = ReadOptions(
            resolvedModel.ManifestPath,
            resolvedModel.Identity.Version);
        return new ProductionMarkerClassificationAdapter(
            resolvedModel.Identity,
            parsedOptions,
            isApproved: true,
            () => new MarkerClassificationService(runtimeHost.Runtime));
    }

    public Task<ProductionMarkerClassificationEvidence> ClassifyAsync(
        ProductionWorkflowDetectionRequest request,
        MarkerImageFrame image,
        IReadOnlyList<MarkerCenter> markers,
        CancellationToken cancellationToken) => ClassifyCoreAsync(request, image, markers, options, cancellationToken);

    public Task<ProductionMarkerClassificationEvidence> ClassifyAsync(
        ProductionWorkflowDetectionRequest request,
        MarkerImageFrame image,
        IReadOnlyList<MarkerCenter> markers,
        IReadOnlyDictionary<string, MarkerRectangle> originalPixelContentBounds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(originalPixelContentBounds);
        return ClassifyCoreAsync(request, image, markers, originalPixelContentBounds.Count == 0 ? options : options with
        {
            OriginalPixelContentBounds = originalPixelContentBounds,
            StageVersion = options.StageVersion + ":isolated-symbol-v1",
        }, cancellationToken);
    }

    private async Task<ProductionMarkerClassificationEvidence> ClassifyCoreAsync(
        ProductionWorkflowDetectionRequest request,
        MarkerImageFrame image,
        IReadOnlyList<MarkerCenter> markers,
        MarkerClassificationOptions executionOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(markers);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsApproved)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
                "Errors.ModelNotFound",
                $"Marker classifier adapter '{AdapterId}' is not production-approved.",
                "Install the exact approved classifier or continue in manual mode.");
        }

        MarkerSourceImage expectedSource = request.ImageVariant switch
        {
            WorkflowImageVariant.Original => MarkerSourceImage.Original,
            WorkflowImageVariant.Enhanced => MarkerSourceImage.Enhanced,
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Unsupported workflow image variant."),
        };
        ValidateFrame(request, image, expectedSource);
        ValidateMarkers(markers, expectedSource);

        MarkerClassificationResult result;
        try
        {
            result = await classifier.Value.ClassifyAsync(
                    new MarkerClassificationRequest(
                        request.ProjectId.ToString("D"),
                        request.Panel.ImportedPanel.PanelId.ToString("D"),
                        request.Image.Sha256,
                        Model,
                        image,
                        markers,
                        executionOptions,
                        transformChain: TransformChain(request.Transforms)),
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
                $"Marker classification failed: {exception.Message}",
                "Retry on CPU or continue with manual marker labels.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ValidateResult(request, markers, result);
        string? provider = ProviderName(result.Model.Provider);
        var envelope = new WorkflowVisionEnvelope(
            MarkerClassificationContract.Version,
            request.RunId,
            request.ProjectId,
            request.Panel.ImportedPanel.PanelId,
            MarkerClassificationContract.Stage,
            result.StageVersion,
            request.Image.Sha256,
            new WorkflowVisionModel(
                result.Model.ModelId,
                result.Model.Version,
                result.Model.Sha256,
                provider),
            new WorkflowVisionTiming(
                result.Timing.PatchExtractionMilliseconds,
                result.Timing.InferenceMilliseconds,
                result.Timing.PostprocessMilliseconds,
                result.Timing.TotalMilliseconds),
            result.Confidence,
            result.Warnings,
            request.Transforms);
        return new ProductionMarkerClassificationEvidence(envelope, result.Markers);
    }

    private static MarkerClassificationOptions ReadOptions(string manifestPath, string stageVersion)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = document.RootElement;
        JsonElement input = SingleObject(root, "inputs");
        JsonElement output = SingleObject(root, "outputs");
        string inputName = RequiredString(input, "name");
        string outputName = RequiredString(output, "name");
        JsonElement inputShape = RequiredArray(input, "shape");
        JsonElement outputShape = RequiredArray(output, "shape");
        if (inputShape.GetArrayLength() != 4 ||
            !string.Equals(inputShape[0].GetString(), "N", StringComparison.Ordinal) ||
            inputShape[1].GetInt32() != 1 ||
            inputShape[2].GetInt32() != 32 ||
            inputShape[3].GetInt32() != 32)
        {
            throw new InvalidDataException("Marker classifier input must be dynamic NCHW [N,1,32,32].");
        }

        int valueCount;
        if (outputShape.GetArrayLength() != 2 ||
            !string.Equals(outputShape[0].GetString(), "N", StringComparison.Ordinal) ||
            (valueCount = outputShape[1].GetInt32()) != 25)
        {
            throw new InvalidDataException("Marker classifier output must be dynamic NC [N,25].");
        }

        RequireStringArray(output, "order", RequiredOutputOrder);
        JsonElement preprocessing = RequiredObject(root, "preprocessing");
        JsonElement postprocessing = RequiredObject(root, "postprocessing");
        RequireStringArray(postprocessing, "shape_order", RequiredShapeOrder);
        RequireStringArray(postprocessing, "fill_order", RequiredFillOrder);
        if (!postprocessing.TryGetProperty("shape_and_fill_separate", out JsonElement separate) ||
            separate.ValueKind is not JsonValueKind.True)
        {
            throw new InvalidDataException("Marker classifier must retain separate shape and fill outputs.");
        }

        int embeddingLength = checked(valueCount - MarkerClassifierTensorContract.EmbeddingOffset);
        var tensor = new MarkerClassifierTensorContract(
            inputName,
            outputName,
            patchWidth: 32,
            patchHeight: 32,
            inputChannelCount: 1,
            embeddingLength)
        {
            NormalizeMean = RequiredSingle(preprocessing, "normalization_mean"),
            NormalizeScale = RequiredSingle(preprocessing, "normalization_scale"),
            OutputEncoding = MarkerClassifierOutputEncoding.Probabilities,
        };
        return new MarkerClassificationOptions(tensor)
        {
            BatchSize = FixedBatchSize,
            StageVersion = stageVersion,
        };
    }

    private static void ValidateFrame(
        ProductionWorkflowDetectionRequest request,
        MarkerImageFrame image,
        MarkerSourceImage expectedSource)
    {
        if (image.Width != request.Image.Width || image.Height != request.Image.Height ||
            image.ChannelCount != 1 || image.SourceImage != expectedSource ||
            !image.OriginalToFrame.IsInvertible ||
            image.ChannelsFirstPixels.Length != checked(image.Width * image.Height) ||
            image.OcrMask.Width != image.Width || image.OcrMask.Height != image.Height ||
            image.ArtifactMask.Width != image.Width || image.ArtifactMask.Height != image.Height ||
            image.OcrMask.Values.Length != checked(image.Width * image.Height) ||
            image.ArtifactMask.Values.Length != checked(image.Width * image.Height) ||
            !AreNormalized(image.OcrMask.Values.Span) ||
            !AreNormalized(image.ArtifactMask.Values.Span))
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Marker classifier frame does not match the retained panel image or tensor contract.",
                "Regenerate marker frame evidence from the immutable selected image.");
        }

        if (expectedSource == MarkerSourceImage.Enhanced && request.Transforms.Count == 0)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Enhanced marker classification requires reversible original-pixel transform provenance.",
                "Regenerate the enhanced derivative with a retained reversible transform.");
        }
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

    private static void ValidateMarkers(
        IReadOnlyList<MarkerCenter> markers,
        MarkerSourceImage expectedSource)
    {
        if (markers.Select(static marker => marker.MarkerId).Distinct(StringComparer.Ordinal).Count() != markers.Count ||
            markers.Any(marker =>
                string.IsNullOrWhiteSpace(marker.MarkerId) ||
                !marker.Center.IsFinite ||
                !double.IsFinite(marker.Radius) || marker.Radius <= 0 ||
                (marker.SourceImage != expectedSource &&
                    marker.SourceImage != MarkerSourceImage.Consensus) ||
                !string.Equals(marker.CoordinateSpace, MarkerContract.CoordinateSpace, StringComparison.Ordinal)))
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Marker centers must be unique, finite, source-matched original-pixel evidence.",
                "Reject invalid centers and rerun marker detection.");
        }
    }

    private void ValidateResult(
        ProductionWorkflowDetectionRequest request,
        IReadOnlyList<MarkerCenter> inputMarkers,
        MarkerClassificationResult result)
    {
        if (result is null)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Marker classifier returned no result.",
                "Retry classification or continue with manual marker labels.");
        }

        if (!result.Succeeded)
        {
            MarkerClassificationFailure? failure = result.Failure;
            throw Failure(
                failure?.Code ?? ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                failure?.UserMessageKey ?? "Errors.DetectionEvidenceRejected",
                failure?.TechnicalMessage ?? "Marker classification failed without a diagnostic.",
                failure?.SuggestedAction ?? "Retry classification or continue with manual marker labels.",
                failure?.Recoverable ?? true);
        }

        string[] expectedIds = inputMarkers.Select(static marker => marker.MarkerId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actualIds = result.Markers.Select(static marker => marker.Marker.MarkerId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (result.ContractVersion != MarkerClassificationContract.Version ||
            !string.Equals(result.ProjectId, request.ProjectId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(result.PanelId, request.Panel.ImportedPanel.PanelId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(result.Stage, MarkerClassificationContract.Stage, StringComparison.Ordinal) ||
            !string.Equals(result.InputSha256, request.Image.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.CoordinateSpace, MarkerClassificationContract.CoordinateSpace, StringComparison.Ordinal) ||
            !string.Equals(result.Model.ModelId, Model.ModelId, StringComparison.Ordinal) ||
            !string.Equals(result.Model.Version, Model.Version, StringComparison.Ordinal) ||
            !string.Equals(result.Model.Sha256, Model.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !expectedIds.SequenceEqual(actualIds, StringComparer.Ordinal) ||
            (inputMarkers.Count > 0 && result.Model.Provider is null) ||
            result.Model.Provider == InferenceProvider.Fake)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Marker classifier returned mismatched identity, provider, coordinate, or marker evidence.",
                "Reject the classifier result and verify the exact production model composition.");
        }
    }

    private static string TransformChain(IReadOnlyList<WorkflowTransformProvenance> transforms) =>
        transforms.Count == 0
            ? "identity"
            : string.Join('>', transforms.Select(static transform => transform.TransformId));

    private static string? ProviderName(InferenceProvider? provider) => provider switch
    {
        InferenceProvider.Cpu => "cpu",
        InferenceProvider.DirectMl => "directml",
        null => null,
        _ => throw Failure(
            ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
            "Errors.DetectionEvidenceRejected",
            "Marker classifier used a non-production execution provider.",
            "Rerun with DirectML or mandatory CPU fallback."),
    };

    private static JsonElement SingleObject(JsonElement root, string propertyName)
    {
        JsonElement values = RequiredArray(root, propertyName);
        if (values.GetArrayLength() != 1 || values[0].ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Marker classifier manifest '{propertyName}' must contain one object.");
        }

        return values[0];
    }

    private static JsonElement RequiredArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Marker classifier manifest field '{propertyName}' must be an array.");
        }

        return value;
    }

    private static JsonElement RequiredObject(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Marker classifier manifest field '{propertyName}' must be an object.");
        }

        return value;
    }

    private static string RequiredString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Marker classifier manifest field '{propertyName}' must be a string.");
        }

        return value.GetString()!;
    }

    private static float RequiredSingle(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetSingle(out float result) || !float.IsFinite(result))
        {
            throw new InvalidDataException($"Marker classifier manifest field '{propertyName}' must be finite float32.");
        }

        return result;
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
                $"Marker classifier manifest field '{propertyName}' does not match the frozen order.");
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
