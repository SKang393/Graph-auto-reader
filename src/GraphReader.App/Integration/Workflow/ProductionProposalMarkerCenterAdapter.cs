// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphReader.Inference;
using GraphReader.Markers.Detection;

namespace GraphReader.App.Integration.Workflow;

internal interface IProposalMarkerInferenceRunner
{
    ValueTask<InferenceResponse> RunAsync(InferenceRequest request, CancellationToken cancellationToken);
}

internal sealed record ProposalMarkerPrediction(
    MarkerPoint Center,
    double Radius,
    double Confidence);

internal sealed record FrozenCandidateMarkerCenterModelDescriptor(
    ModelIdentity Identity,
    string ManifestPath,
    string ManifestSha256);

public sealed record ProposalMarkerStageCounters(
    int ProposalGridPositionsConsidered,
    int LowInkRejects,
    int OcrMaskRejects,
    int ArtifactMaskRejects,
    int EmittedProposals,
    int InferenceOutputs,
    [property: JsonPropertyName("outputs_above_0_25")] int OutputsAbove025,
    int DecodedPointsMasked,
    [property: JsonPropertyName("geometry_consensus_rejects_after_1px_refinement_attempts")] int GeometryConsensusRejectsAfterRefinementAttempts,
    int DecodedPointsOutsidePlot,
    int CandidatesBeforeNms,
    int NmsSuppressions,
    int FinalCandidates);

public sealed record ProposalMarkerCandidateDiagnosticResult(
    IReadOnlyList<MarkerCenter> Candidates,
    ProposalMarkerStageCounters StageCounters,
    [property: JsonIgnore] IReadOnlyList<MarkerCenter> PreNmsCandidates,
    [property: JsonIgnore] IReadOnlyList<MarkerPoint> GridProposalCenters,
    [property: JsonIgnore] IReadOnlyList<MarkerPoint> InkSupportedProposalCenters,
    [property: JsonIgnore] IReadOnlyList<MarkerPoint> OcrUnmaskedProposalCenters,
    [property: JsonIgnore] IReadOnlyList<MarkerPoint> EmittedProposalCenters,
    [property: JsonIgnore] IReadOnlyList<MarkerPoint> AboveThresholdDecodedPoints);

public sealed record ProposalMarkerPatchFeatureSummary(
    MarkerPoint OriginalBaseCenter,
    double InkMean,
    double InkCenter5x5Mean,
    double InkMaximum,
    double OcrMaskMean,
    double OcrMaskMaximum,
    double ArtifactMaskMean,
    double ArtifactMaskMaximum);

public sealed record ProposalMarkerNegativePatchDiagnosticResult(
    ProposalMarkerStageCounters StageCounters,
    [property: JsonIgnore] IReadOnlyList<ProposalMarkerPatchFeatureSummary> EmittedProposalFeatures);

public sealed record ProposalMarkerMorphologyScoreSummary(
    MarkerPoint OriginalBaseCenter,
    double Probability,
    double DarkFractionAt012,
    double DarkFractionAt05,
    double InkCenter5x5Mean,
    double MaximumRowDarkFraction,
    double MaximumColumnDarkFraction,
    double ForegroundExtentBalance,
    double CovarianceEigenvalueRatio,
    double BorderDarkFraction,
    int MaximumRingSupportCount);

/// <summary>
/// Checksum-bound proposal-marker integration. Candidate factories remain
/// unapproved. The production factory can only be reached through a model that
/// the production store has already resolved as CPU-approved.
/// </summary>
public sealed class ProductionProposalMarkerCenterAdapter :
    IProductionMarkerCenterAdapter,
    IProductionCandidateMarkerCenterAdapter
{
    public const string CandidateRevision = "marker-center-runtime-consistency-v2";
    public const string CandidateId = "P2";
    public const string ExpectedModelSha256 = "924c555e2f27955c644143125d7abd3b05859ea9928ab9d1e741e0544fa19e8b";
    public const string MultiradiusCandidateRevision = "marker-center-multiradius-geometry-v23";
    public const string MultiradiusCandidateId = "P1";
    public const string ExpectedMultiradiusModelSha256 = "0b413db48f8e6707ee5ec99afff4cd8ec3d25c6b8a8d9f165bd416deb4578a38";
    public const string MaskPreservingCandidateRevision = "marker-center-mask-preserving-v24";
    public const string MaskPreservingCandidateId = "P1";
    public const string ExpectedMaskPreservingModelSha256 = "4dece2eeb87229d5d57e0d2d714c1915ebecf8e9475b0d466a03dd970993fdb4";
    public const float CenterThreshold = 0.25f;
    public const int PatchSize = 33;
    public const int ProposalStride = 4;
    public const int BatchSize = 256;
    internal const double PlotDomainBoundaryPixels = 16;
    internal const double MinimumCenterSeparationForTesting = 6.5;
    private const float InkSupportThreshold = 0.11f;
    private const float MaskRejectionThreshold = 0.35f;
    private const double MinimumCenterSeparation = 6.5;
    private const double RadiusSuppressionScale = 1.25;
    public const int MaximumDecodedCandidates = 100_000;

    private readonly IProposalMarkerInferenceRunner inference;
    private readonly int maximumDecodedCandidates;
    private readonly bool multiradiusGeometry;
    private readonly bool maskPreservingCandidate;
    private readonly bool plotDomainProposalFiltering;
    private readonly string maskPreservingRevision;
    private readonly string maskPreservingCandidateId;

    public static ProductionProposalMarkerCenterAdapter CreateCandidate(
        ModelIdentity model,
        InferenceRuntime runtime)
    {
        VerifyPayload(model);
        ArgumentNullException.ThrowIfNull(runtime);
        return new ProductionProposalMarkerCenterAdapter(
            model,
            new RuntimeProposalMarkerInferenceRunner(runtime));
    }

    public static ProductionProposalMarkerCenterAdapter CreateMultiradiusCandidate(
        ModelIdentity model,
        InferenceRuntime runtime)
    {
        VerifyMultiradiusPayload(model);
        ArgumentNullException.ThrowIfNull(runtime);
        return new ProductionProposalMarkerCenterAdapter(
            model,
            new RuntimeProposalMarkerInferenceRunner(runtime),
            multiradiusGeometry: true);
    }

    public static ProductionProposalMarkerCenterAdapter CreateMaskPreservingCandidate(
        ModelIdentity model,
        InferenceRuntime runtime)
    {
        VerifyMaskPreservingPayload(model);
        ArgumentNullException.ThrowIfNull(runtime);
        return new ProductionProposalMarkerCenterAdapter(
            model,
            new RuntimeProposalMarkerInferenceRunner(runtime),
            multiradiusGeometry: true,
            maskPreservingCandidate: true);
    }

    internal static ProductionProposalMarkerCenterAdapter CreateForFrozenCandidateEvaluation(
        FrozenCandidateMarkerCenterModelDescriptor descriptor,
        InferenceRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return CreateForFrozenCandidateEvaluation(
            descriptor,
            new RuntimeProposalMarkerInferenceRunner(runtime));
    }

    internal static ProductionProposalMarkerCenterAdapter CreateForFrozenCandidateEvaluation(
        FrozenCandidateMarkerCenterModelDescriptor descriptor,
        IProposalMarkerInferenceRunner inference)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(inference);
        ValidateFrozenCandidateDescriptor(descriptor);
        return new ProductionProposalMarkerCenterAdapter(
            descriptor.Identity,
            inference,
            multiradiusGeometry: true,
            maskPreservingCandidate: true,
            expectedMaskPreservingSha256: descriptor.Identity.Sha256,
            maskPreservingRevision: descriptor.Identity.ModelId,
            maskPreservingCandidateId: descriptor.Identity.Version);
    }

    internal static ProductionProposalMarkerCenterAdapter CreateForFrozenCandidatePlotDomainEvaluation(
        FrozenCandidateMarkerCenterModelDescriptor descriptor,
        InferenceRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return CreateForFrozenCandidatePlotDomainEvaluation(
            descriptor,
            new RuntimeProposalMarkerInferenceRunner(runtime));
    }

    internal static ProductionProposalMarkerCenterAdapter CreateForFrozenCandidatePlotDomainEvaluation(
        FrozenCandidateMarkerCenterModelDescriptor descriptor,
        IProposalMarkerInferenceRunner inference)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(inference);
        ValidateFrozenCandidateDescriptor(descriptor);
        return new ProductionProposalMarkerCenterAdapter(
            descriptor.Identity,
            inference,
            multiradiusGeometry: true,
            maskPreservingCandidate: true,
            expectedMaskPreservingSha256: descriptor.Identity.Sha256,
            maskPreservingRevision: descriptor.Identity.ModelId,
            maskPreservingCandidateId: descriptor.Identity.Version,
            plotDomainProposalFiltering: true);
    }

    public static ProductionProposalMarkerCenterAdapter Create(
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
            throw new InvalidDataException(
                "The proposal marker model lacks mandatory CPU provider approval.");
        }

        VerifyMaskPreservingPayload(resolvedModel.Identity);
        VerifyChecksum(
            resolvedModel.ManifestPath,
            resolvedModel.ManifestSha256,
            "proposal marker manifest");
        VerifyMaskPreservingManifest(resolvedModel.ManifestPath);
        return new ProductionProposalMarkerCenterAdapter(
            resolvedModel.Identity,
            new RuntimeProposalMarkerInferenceRunner(runtimeHost.Runtime),
            multiradiusGeometry: true,
            maskPreservingCandidate: true,
            isApproved: true);
    }

    internal ProductionProposalMarkerCenterAdapter(
        ModelIdentity model,
        IProposalMarkerInferenceRunner inference,
        bool multiradiusGeometry = false,
        int? maximumDecodedCandidates = null,
        bool maskPreservingCandidate = false,
        bool isApproved = false,
        string? expectedMaskPreservingSha256 = null,
        string? maskPreservingRevision = null,
        string? maskPreservingCandidateId = null,
        bool plotDomainProposalFiltering = false)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Model.Validate();
        this.multiradiusGeometry = multiradiusGeometry;
        this.maskPreservingCandidate = maskPreservingCandidate;
        if (plotDomainProposalFiltering &&
            (!maskPreservingCandidate || !multiradiusGeometry || isApproved))
        {
            throw new InvalidOperationException(
                "V25 plot-domain proposals require an explicitly unapproved mask-preserving multiradius candidate.");
        }

        this.plotDomainProposalFiltering = plotDomainProposalFiltering;
        string expectedModelSha256 = maskPreservingCandidate
            ? expectedMaskPreservingSha256 ?? ExpectedMaskPreservingModelSha256
            : multiradiusGeometry ? ExpectedMultiradiusModelSha256 : ExpectedModelSha256;
        if (!string.Equals(Model.Sha256, expectedModelSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(maskPreservingCandidate
                ? "The proposal marker payload is not the checksum-bound mask-preserving V24 P1 model."
                : multiradiusGeometry
                ? "The proposal marker payload is not the checksum-bound multiradius V23 P1 model."
                : "The proposal marker payload is not the checksum-bound runtime-consistency-v2 P2 model.");
        }

        this.inference = inference ?? throw new ArgumentNullException(nameof(inference));
        IsApproved = isApproved;
        this.maskPreservingRevision = maskPreservingRevision ?? MaskPreservingCandidateRevision;
        this.maskPreservingCandidateId = maskPreservingCandidateId ?? MaskPreservingCandidateId;
        this.maximumDecodedCandidates = maximumDecodedCandidates ?? MaximumDecodedCandidates;
        if (this.maximumDecodedCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDecodedCandidates));
        }
    }

    public string AdapterId => string.Concat(
        $"graphreader-marker-center-proposal:{Model.Sha256[..12].ToLowerInvariant()}",
        plotDomainProposalFiltering ? ":plot-domain-v25" : string.Empty);

    public bool IsApproved { get; }

    public ModelIdentity Model { get; }

    private sealed class RuntimeProposalMarkerInferenceRunner(InferenceRuntime runtime)
        : IProposalMarkerInferenceRunner
    {
        public ValueTask<InferenceResponse> RunAsync(
            InferenceRequest request,
            CancellationToken cancellationToken) =>
            runtime.RunAsync(request, cancellationToken);
    }

    private static void VerifyPayload(ModelIdentity model)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Validate();
        if (!File.Exists(model.FilePath))
        {
            throw new FileNotFoundException("The checksum-bound proposal marker model is missing.", model.FilePath);
        }

        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model.FilePath)));
        if (!string.Equals(actual, ExpectedModelSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(model.Sha256, ExpectedModelSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The proposal marker model bytes do not match the checksum-bound P2 payload.");
        }
    }

    private static void VerifyMultiradiusPayload(ModelIdentity model)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Validate();
        if (!string.Equals(model.ModelId, MultiradiusCandidateRevision, StringComparison.Ordinal) ||
            !string.Equals(model.Version, MultiradiusCandidateId, StringComparison.Ordinal) ||
            !string.Equals(model.Sha256, ExpectedMultiradiusModelSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The proposal marker payload identity is not the checksum-bound multiradius V23 P1 model.");
        }

        if (!File.Exists(model.FilePath))
        {
            throw new FileNotFoundException("The checksum-bound multiradius proposal marker model is missing.", model.FilePath);
        }

        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model.FilePath)));
        if (!string.Equals(actual, ExpectedMultiradiusModelSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The proposal marker model bytes do not match the checksum-bound multiradius V23 P1 payload.");
        }
    }

    private static void VerifyMaskPreservingPayload(ModelIdentity model)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Validate();
        if (!string.Equals(model.ModelId, MaskPreservingCandidateRevision, StringComparison.Ordinal) ||
            !string.Equals(model.Version, MaskPreservingCandidateId, StringComparison.Ordinal) ||
            !string.Equals(model.Sha256, ExpectedMaskPreservingModelSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The proposal marker payload identity is not the checksum-bound mask-preserving V24 P1 model.");
        }

        if (!File.Exists(model.FilePath))
        {
            throw new FileNotFoundException("The checksum-bound mask-preserving proposal marker model is missing.", model.FilePath);
        }

        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model.FilePath)));
        if (!string.Equals(actual, ExpectedMaskPreservingModelSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The proposal marker model bytes do not match the checksum-bound mask-preserving V24 P1 payload.");
        }
    }

    private static void VerifyChecksum(string path, string expectedSha256, string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The checksum-bound {description} is missing.", path);
        }

        string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The {description} bytes do not match the production model store.");
        }
    }

    private static void VerifyMaskPreservingManifest(string manifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement input = SingleTensor(document.RootElement, "inputs");
        JsonElement output = SingleTensor(document.RootElement, "outputs");
        RequireTensor(input, "candidate_patches", [-1, 3, PatchSize, PatchSize]);
        RequireTensor(output, "candidate_predictions", [-1, 4]);
    }

    private static void ValidateFrozenCandidateDescriptor(
        FrozenCandidateMarkerCenterModelDescriptor descriptor)
    {
        descriptor.Identity.Validate();
        VerifyChecksum(
            descriptor.Identity.FilePath,
            descriptor.Identity.Sha256,
            "frozen candidate proposal marker payload");
        VerifyChecksum(
            descriptor.ManifestPath,
            descriptor.ManifestSha256,
            "frozen candidate proposal marker manifest");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(descriptor.ManifestPath));
        JsonElement root = document.RootElement;
        RequireExactString(root, "task", "marker_center", "Frozen candidate proposal marker manifest");
        RequireExactString(root, "model_id", descriptor.Identity.ModelId, "Frozen candidate proposal marker manifest");
        RequireExactString(root, "model_version", descriptor.Identity.Version, "Frozen candidate proposal marker manifest");
        RequireExactString(root, "sha256", descriptor.Identity.Sha256, "Frozen candidate proposal marker manifest", ignoreCase: true);
        JsonElement files = RequiredArray(root, "files", "Frozen candidate proposal marker manifest");
        if (!files.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String &&
            string.Equals(Path.GetFileName(item.GetString()), Path.GetFileName(descriptor.Identity.FilePath), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Frozen candidate proposal marker manifest does not identify its payload.");
        }
        VerifyMaskPreservingManifest(descriptor.ManifestPath);
        JsonElement preprocessing = RequiredObject(root, "preprocessing", "Frozen candidate proposal marker manifest");
        RequireExactString(preprocessing, "architecture", "scale-separated-multiscale-patch-cnn-v16", "Frozen candidate preprocessing");
        RequireStringArray(preprocessing, "channels", ["ink_probability", "ocr_mask", "artifact_mask"], "Frozen candidate preprocessing");
        RequireExactNumber(preprocessing, "patch_size", PatchSize, "Frozen candidate preprocessing");
        RequireExactNumber(preprocessing, "proposal_stride", ProposalStride, "Frozen candidate preprocessing");
        RequireExactNumber(preprocessing, "ink_support_window_size", 17, "Frozen candidate preprocessing");
        RequireExactNumber(preprocessing, "ink_support_threshold", 0.11, "Frozen candidate preprocessing");
        JsonElement postprocessing = RequiredObject(root, "postprocessing", "Frozen candidate proposal marker manifest");
        RequireExactString(postprocessing, "algorithm", "mask_preserving_multiradius_v24", "Frozen candidate postprocessing");
        RequireExactNumber(postprocessing, "center_threshold", CenterThreshold, "Frozen candidate postprocessing");
        RequireExactNumber(postprocessing, "offset_scale", ProposalStride, "Frozen candidate postprocessing");
        RequireExactNumber(postprocessing, "minimum_radius_pixels", 2.5, "Frozen candidate postprocessing");
        RequireExactNumber(postprocessing, "maximum_radius_pixels", 8.0, "Frozen candidate postprocessing");
        RequireExactNumber(postprocessing, "mask_rejection_threshold", 0.35, "Frozen candidate postprocessing");
        RequireExactNumber(postprocessing, "minimum_center_separation_pixels", 5.0, "Frozen candidate postprocessing");
        RequireExactNumber(postprocessing, "radius_suppression_scale", RadiusSuppressionScale, "Frozen candidate postprocessing");
        RequireExactNumber(postprocessing, "maximum_decoded_candidates", MaximumDecodedCandidates, "Frozen candidate postprocessing");
    }

    private static JsonElement RequiredObject(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} field '{name}' must be an object.");
        }
        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{label} field '{name}' must be an array.");
        }
        return value;
    }

    private static void RequireExactString(
        JsonElement parent,
        string name,
        string expected,
        string label,
        bool ignoreCase = false)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            !string.Equals(value.GetString(), expected,
                ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{label} field '{name}' does not match the frozen V24 contract.");
        }
    }

    private static void RequireStringArray(
        JsonElement parent,
        string name,
        string[] expected,
        string label)
    {
        JsonElement value = RequiredArray(parent, name, label);
        string?[] actual = value.EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{label} field '{name}' does not match the frozen V24 contract.");
        }
    }

    private static void RequireExactNumber(
        JsonElement parent,
        string name,
        double expected,
        string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double actual) ||
            !double.IsFinite(actual) || actual != expected)
        {
            throw new InvalidDataException($"{label} field '{name}' does not match the frozen V24 contract.");
        }
    }

    private static JsonElement SingleTensor(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Proposal marker manifest field '{propertyName}' must be an array.");
        }

        JsonElement[] tensors = values.EnumerateArray().ToArray();
        if (tensors.Length != 1 || tensors[0].ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Proposal marker manifest field '{propertyName}' must contain one tensor.");
        }

        return tensors[0];
    }

    private static void RequireTensor(JsonElement tensor, string expectedName, int[] expectedShape)
    {
        if (!tensor.TryGetProperty("name", out JsonElement name) ||
            name.ValueKind != JsonValueKind.String ||
            !string.Equals(name.GetString(), expectedName, StringComparison.Ordinal) ||
            !tensor.TryGetProperty("element_type", out JsonElement elementType) ||
            elementType.ValueKind != JsonValueKind.String ||
            !string.Equals(elementType.GetString(), "float32", StringComparison.Ordinal) ||
            !tensor.TryGetProperty("shape", out JsonElement shape) ||
            shape.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Proposal marker tensor '{expectedName}' must declare its name, float32 type, and shape.");
        }

        int[] actualShape;
        try
        {
            actualShape = shape.EnumerateArray().Select(static value => value.GetInt32()).ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException(
                $"Proposal marker tensor '{expectedName}' shape must contain integers.",
                exception);
        }

        if (!actualShape.SequenceEqual(expectedShape))
        {
            throw new InvalidDataException(
                $"Proposal marker tensor '{expectedName}' does not match the frozen V24 shape.");
        }
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
                $"Marker-center adapter '{AdapterId}' is candidate-only and not production-approved.",
                "Use the candidate-only evaluation method or continue in manual mode.");
        }

        return await DetectValidatedAsync(
                request,
                originalImage,
                plotPolygon,
                enhancedImage,
                enhancedTransforms,
                cancellationToken)
            .ConfigureAwait(false);
    }

    Task<ProductionMarkerCenterEvidence> IProductionCandidateMarkerCenterAdapter.DetectForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        MarkerImageFrame originalImage,
        MarkerPolygon plotPolygon,
        CancellationToken cancellationToken) =>
        DetectForCandidateEvaluationAsync(request, originalImage, plotPolygon, cancellationToken);

    internal Task<ProductionMarkerCenterEvidence> DetectForCandidateEvaluationAsync(
        ProductionWorkflowDetectionRequest request,
        MarkerImageFrame originalImage,
        MarkerPolygon plotPolygon,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(originalImage);
        ArgumentNullException.ThrowIfNull(plotPolygon);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsApproved)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "Candidate evaluation requires an explicitly unapproved marker-center adapter.",
                "Use normal production execution for an approved adapter.");
        }

        return DetectValidatedAsync(
            request,
            originalImage,
            plotPolygon,
            enhancedImage: null,
            enhancedTransforms: null,
            cancellationToken);
    }

    private async Task<ProductionMarkerCenterEvidence> DetectValidatedAsync(
        ProductionWorkflowDetectionRequest request,
        MarkerImageFrame originalImage,
        MarkerPolygon plotPolygon,
        MarkerImageFrame? enhancedImage,
        IReadOnlyList<WorkflowTransformProvenance>? enhancedTransforms,
        CancellationToken cancellationToken)
    {
        if (!maskPreservingCandidate ||
            request.ImageVariant != WorkflowImageVariant.Original ||
            originalImage.SourceImage != MarkerSourceImage.Original ||
            originalImage.OriginalToFrame != MarkerAffineTransform.Identity ||
            originalImage.Width != request.Image.Width ||
            originalImage.Height != request.Image.Height ||
            enhancedImage is not null ||
            (enhancedTransforms?.Count ?? 0) != 0)
        {
            throw Failure(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                "Errors.DetectionEvidenceRejected",
                "The proposal marker path requires the immutable original frame with no enhanced derivative.",
                "Regenerate marker evidence from the retained original panel image.");
        }

        var total = Stopwatch.StartNew();
        ProposalMarkerCandidateDiagnosticResult diagnostic;
        try
        {
            diagnostic = await DetectCandidateWithDiagnosticsAsync(
                    originalImage,
                    plotPolygon,
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
                $"Proposal marker detection failed: {exception.Message}",
                "Retry on CPU or continue with manual marker editing.");
        }

        total.Stop();
        var timing = new MarkerDetectionTiming(0, 0, 0, total.Elapsed.TotalMilliseconds);
        double confidence = diagnostic.Candidates.Count == 0
            ? 0
            : diagnostic.Candidates.Average(static marker => marker.CenterConfidence);
        var envelope = new WorkflowVisionEnvelope(
            MarkerContract.Version,
            request.RunId,
            request.ProjectId,
            request.Panel.ImportedPanel.PanelId,
            MarkerContract.Stage,
            $"{(plotDomainProposalFiltering ? "proposal-marker-v25-plot-domain" : "proposal-marker-v24")}:{Model.Version}",
            request.Image.Sha256,
            new WorkflowVisionModel(Model.ModelId, Model.Version, Model.Sha256, "cpu"),
            new WorkflowVisionTiming(0, 0, 0, total.Elapsed.TotalMilliseconds),
            confidence,
            [$"proposal_marker_counts:{diagnostic.StageCounters.CandidatesBeforeNms}:{diagnostic.StageCounters.FinalCandidates}"],
            []);
        var report = new MarkerFrameReport(
            MarkerSourceImage.Original,
            $"{(plotDomainProposalFiltering ? "proposal-v25-plot-domain" : "proposal-v24")}:{request.Image.Sha256}:{Model.Sha256}",
            InferenceProvider.Cpu,
            [new ProviderAttempt(InferenceProvider.Cpu, true, null)],
            timing,
            diagnostic.StageCounters.CandidatesBeforeNms,
            diagnostic.StageCounters.FinalCandidates,
            CacheHit: false,
            Failure: null);
        return new ProductionMarkerCenterEvidence(envelope, diagnostic.Candidates, [report]);
    }

    /// <summary>
    /// Runs the P2 candidate for private real-dev diagnosis. This method never
    /// changes or implies the production approval state.
    /// </summary>
    public async Task<IReadOnlyList<MarkerCenter>> DetectCandidateAsync(
        MarkerImageFrame frame,
        MarkerPolygon plotPolygon,
        CancellationToken cancellationToken)
        => (await DetectCandidateWithDiagnosticsAsync(frame, plotPolygon, cancellationToken).ConfigureAwait(false)).Candidates;

    public async Task<ProposalMarkerCandidateDiagnosticResult> DetectCandidateWithDiagnosticsAsync(
        MarkerImageFrame frame,
        MarkerPolygon plotPolygon,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(plotPolygon);
        ValidateFrame(frame);
        cancellationToken.ThrowIfCancellationRequested();

        var counters = new ProposalMarkerStageCounterAccumulator();
        var predictions = new List<ProposalMarkerPrediction>();
        var gridProposalCenters = new List<MarkerPoint>();
        var inkSupportedProposalCenters = new List<MarkerPoint>();
        var ocrUnmaskedProposalCenters = new List<MarkerPoint>();
        var emittedProposalCenters = new List<MarkerPoint>();
        var aboveThresholdDecodedPoints = new List<MarkerPoint>();
        var batch = new List<Proposal>(BatchSize);
        int batchOffset = 0;

        async Task InferBatchAsync(List<Proposal> proposals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = proposals.Count;
            float[] values = new float[checked(count * 3 * PatchSize * PatchSize)];
            for (int index = 0; index < count; index++)
            {
                proposals[index].Patch.CopyTo(values.AsSpan(index * 3 * PatchSize * PatchSize));
            }

            var cacheParameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["candidate_id"] = maskPreservingCandidate ? maskPreservingCandidateId : multiradiusGeometry ? MultiradiusCandidateId : CandidateId,
                ["threshold"] = CenterThreshold,
                ["batch_offset"] = batchOffset,
                ["batch_count"] = count,
            };
            if (plotDomainProposalFiltering)
            {
                cacheParameters["proposal_domain"] = "axis-polygon-or-16px-boundary";
            }

            InferenceResponse response = await inference.RunAsync(
                    new InferenceRequest(
                        Model,
                        new InferenceInput(values, [count, 3, PatchSize, PatchSize], "candidate_patches", "candidate_predictions"),
                        new StageCacheMaterial(
                            "candidate-only",
                            "proposal-patches",
                            frame.SourceImage.ToString(),
                            plotDomainProposalFiltering ? "marker_center_candidate_v25_plot_domain" : maskPreservingCandidate ? "marker_center_candidate_v24" : multiradiusGeometry ? "marker_center_candidate_v23" : "marker_center_candidate_p2",
                            plotDomainProposalFiltering ? $"{maskPreservingRevision}:plot-domain-v25" : maskPreservingCandidate ? maskPreservingRevision : multiradiusGeometry ? MultiradiusCandidateRevision : CandidateRevision,
                            cacheParameters,
                            MarkerContract.Version),
                        TimeSpan.FromSeconds(30),
                        [InferenceProvider.Cpu],
                        BypassCache: true),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.Succeeded || response.Execution is null || response.Execution.Provider != InferenceProvider.Cpu)
            {
                throw new InvalidDataException("The proposal marker candidate requires successful CPU inference evidence.");
            }

            IReadOnlyList<float> output = response.Execution.Output;
            if (output.Count != checked(count * 4))
            {
                throw new InvalidDataException("The proposal marker candidate must return [N,4] output.");
            }

            counters.InferenceOutputs += count;

            for (int index = 0; index < count; index++)
            {
                int baseIndex = index * 4;
                float probability = output[baseIndex];
                float offsetX = output[baseIndex + 1];
                float offsetY = output[baseIndex + 2];
                float radius = output[baseIndex + 3];
                if (!float.IsFinite(probability) || probability is < 0 or > 1 ||
                    !float.IsFinite(offsetX) || !float.IsFinite(offsetY) ||
                    !float.IsFinite(radius) || radius < 0)
                {
                    throw new InvalidDataException("The proposal marker candidate returned invalid output values.");
                }

                if (probability < CenterThreshold)
                {
                    continue;
                }

                counters.OutputsAbove025++;

                Proposal proposal = proposals[index];
                double x = proposal.X + (offsetX * ProposalStride);
                double y = proposal.Y + (offsetY * ProposalStride);
                aboveThresholdDecodedPoints.Add(
                    frame.OriginalToFrame.MapToOriginal(new MarkerPoint(x, y)));
                double decodedRadius = Math.Clamp(radius, 2.5, 8.0);
                if (!TryRefine(frame, x, y, decodedRadius, multiradiusGeometry, maskPreservingCandidate, out MarkerPoint refined, out RefinementFailure failure))
                {
                    if (failure == RefinementFailure.Masked)
                    {
                        counters.DecodedPointsMasked++;
                    }
                    else
                    {
                        counters.GeometryConsensusRejectsAfterRefinementAttempts++;
                    }
                    continue;
                }

                if (plotPolygon.Contains(frame.OriginalToFrame.MapToOriginal(refined)))
                {
                    if (predictions.Count >= maximumDecodedCandidates)
                    {
                        throw new InvalidDataException("The proposal marker candidate exceeded its decoded-candidate limit.");
                    }

                    predictions.Add(new ProposalMarkerPrediction(refined, decodedRadius, probability));
                }
                else
                {
                    counters.DecodedPointsOutsidePlot++;
                }
            }

            batchOffset += count;
        }

        foreach (Proposal proposal in EnumerateProposals(
                     frame,
                     plotPolygon,
                     counters,
                     gridProposalCenters,
                     inkSupportedProposalCenters,
                     ocrUnmaskedProposalCenters,
                     emittedProposalCenters,
                     cancellationToken,
                     maskPreservingCandidate,
                     plotDomainProposalFiltering))
        {
            batch.Add(proposal);
            if (batch.Count == BatchSize)
            {
                await InferBatchAsync(batch).ConfigureAwait(false);
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            await InferBatchAsync(batch).ConfigureAwait(false);
        }

        List<ProposalMarkerPrediction> accepted = ApplyNms(predictions, out int nmsSuppressions);
        counters.CandidatesBeforeNms = predictions.Count;
        counters.NmsSuppressions = nmsSuppressions;
        counters.FinalCandidates = accepted.Count;
        IReadOnlyList<MarkerCenter> preNmsCandidates = predictions
            .Select((candidate, index) => ToMarkerCenter(frame, candidate, index, multiradiusGeometry, maskPreservingCandidate, plotDomainProposalFiltering, "pre-nms"))
            .ToArray();
        IReadOnlyList<MarkerCenter> candidates = accepted
            .OrderBy(candidate => candidate.Center.Y)
            .ThenBy(candidate => candidate.Center.X)
            .ThenByDescending(candidate => candidate.Confidence)
            .Select((candidate, index) => ToMarkerCenter(frame, candidate, index, multiradiusGeometry, maskPreservingCandidate, plotDomainProposalFiltering, suffix: null))
            .ToArray();
        return new ProposalMarkerCandidateDiagnosticResult(
            candidates,
            counters.ToRecord(),
            preNmsCandidates,
            gridProposalCenters,
            inkSupportedProposalCenters,
            ocrUnmaskedProposalCenters,
            emittedProposalCenters,
            aboveThresholdDecodedPoints);
    }

    /// <summary>Enumerates V24 proposals and summarizes their channels without model inference.</summary>
    public static ProposalMarkerNegativePatchDiagnosticResult DiagnoseMaskPreservingProposals(
        MarkerImageFrame frame,
        MarkerPolygon plotPolygon,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(plotPolygon);
        ValidateFrame(frame);
        cancellationToken.ThrowIfCancellationRequested();
        var counters = new ProposalMarkerStageCounterAccumulator();
        var grid = new List<MarkerPoint>();
        var ink = new List<MarkerPoint>();
        var unmasked = new List<MarkerPoint>();
        var emitted = new List<MarkerPoint>();
        var summaries = new List<ProposalMarkerPatchFeatureSummary>();
        foreach (Proposal proposal in EnumerateProposals(frame, plotPolygon, counters, grid, ink, unmasked, emitted, cancellationToken, maskPreservingCandidate: true))
        {
            summaries.Add(SummarizeProposal(frame, proposal));
        }
        return new ProposalMarkerNegativePatchDiagnosticResult(counters.ToRecord(), summaries);
    }

    public async Task<IReadOnlyList<ProposalMarkerMorphologyScoreSummary>> DetectMaskPreservingMorphologyScoresAsync(
        MarkerImageFrame frame,
        MarkerPolygon plotPolygon,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(plotPolygon);
        ValidateFrame(frame);
        cancellationToken.ThrowIfCancellationRequested();
        var counters = new ProposalMarkerStageCounterAccumulator();
        var scores = new List<ProposalMarkerMorphologyScoreSummary>();
        var batch = new List<Proposal>(BatchSize);
        int offset = 0;
        async Task InferBatchAsync(List<Proposal> proposals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int plane = PatchSize * PatchSize;
            float[] input = new float[checked(proposals.Count * 3 * plane)];
            for (int i = 0; i < proposals.Count; i++) proposals[i].Patch.CopyTo(input.AsSpan(i * 3 * plane));
            var cacheParameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["candidate_id"] = maskPreservingCandidateId,
                ["threshold"] = CenterThreshold,
                ["batch_offset"] = offset,
            };
            if (plotDomainProposalFiltering)
            {
                cacheParameters["proposal_domain"] = "axis-polygon-or-16px-boundary";
            }
            InferenceResponse response = await inference.RunAsync(new InferenceRequest(
                Model,
                new InferenceInput(input, [proposals.Count, 3, PatchSize, PatchSize], "candidate_patches", "candidate_predictions"),
                new StageCacheMaterial("candidate-only", "proposal-patches", frame.SourceImage.ToString(),
                    plotDomainProposalFiltering ? "marker_center_candidate_v25_plot_domain_morphology" : "marker_center_candidate_v24_morphology",
                    plotDomainProposalFiltering ? $"{maskPreservingRevision}:plot-domain-v25" : maskPreservingRevision,
                    cacheParameters, MarkerContract.Version),
                TimeSpan.FromSeconds(30), [InferenceProvider.Cpu], BypassCache: true), cancellationToken).ConfigureAwait(false);
            if (!response.Succeeded || response.Execution is null || response.Execution.Provider != InferenceProvider.Cpu || response.Execution.Output.Count != proposals.Count * 4)
                throw new InvalidDataException("The V24 morphology diagnostic requires successful CPU inference with [N,4] output.");
            for (int i = 0; i < proposals.Count; i++)
            {
                float probability = response.Execution.Output[i * 4];
                if (!float.IsFinite(probability) || probability is < 0 or > 1) throw new InvalidDataException("The V24 morphology diagnostic returned an invalid probability.");
                scores.Add(SummarizeMorphology(frame, proposals[i], probability));
            }
        }
        foreach (Proposal proposal in EnumerateProposals(
                     frame,
                     plotPolygon,
                     counters,
                     [],
                     [],
                     [],
                     [],
                     cancellationToken,
                     maskPreservingCandidate: true,
                     plotDomainProposalFiltering: plotDomainProposalFiltering))
        {
            batch.Add(proposal);
            if (batch.Count == BatchSize) { await InferBatchAsync(batch).ConfigureAwait(false); offset += batch.Count; batch.Clear(); }
        }
        if (batch.Count > 0) await InferBatchAsync(batch).ConfigureAwait(false);
        return scores;
    }

    private static ProposalMarkerMorphologyScoreSummary SummarizeMorphology(MarkerImageFrame frame, Proposal proposal, double probability)
    {
        int n = PatchSize, plane = n * n;
        ReadOnlySpan<float> ink = proposal.Patch.AsSpan(0, plane);
        int dark012 = 0, dark05 = 0, border = 0, borderCount = 0, maxRing = 0;
        double center = 0; int centerCount = 0; double sumX = 0, sumY = 0, sumXX = 0, sumYY = 0, sumXY = 0, foreground = 0;
        int minX = n, minY = n, maxX = -1, maxY = -1;
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            double value = ink[y * n + x]; bool is012 = value >= 0.12, is05 = value >= 0.5;
            if (is012) { dark012++; sumX += x; sumY += y; sumXX += x * x; sumYY += y * y; sumXY += x * y; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); foreground++; }
            if (is05) dark05++;
            if (Math.Abs(x - n / 2) <= 2 && Math.Abs(y - n / 2) <= 2) { center += value; centerCount++; }
            if (x == 0 || y == 0 || x == n - 1 || y == n - 1) { border += is012 ? 1 : 0; borderCount++; }
        }
        for (int radius = 3; radius <= 12; radius++)
        {
            int support = 0;
            foreach ((int x, int y) in RingPoints(n / 2, radius)) if ((uint)x < n && (uint)y < n && ink[y * n + x] >= 0.12) support++;
            maxRing = Math.Max(maxRing, support);
        }
        double ratio = 1;
        if (dark012 > 1)
        {
            double meanX = sumX / foreground, meanY = sumY / foreground;
            double a = sumXX / foreground - meanX * meanX, c = sumYY / foreground - meanY * meanY, b = sumXY / foreground - meanX * meanY;
            double root = Math.Sqrt(Math.Max(0, ((a - c) * (a - c)) + (4 * b * b))), high = Math.Max(0, (a + c + root) / 2), low = Math.Max(1e-12, (a + c - root) / 2);
            ratio = Math.Clamp(high / low, 1, 1e6);
        }
        double width = maxX < 0 ? 0 : maxX - minX + 1, height = maxY < 0 ? 0 : maxY - minY + 1;
        return new(frame.OriginalToFrame.MapToOriginal(new MarkerPoint(proposal.X, proposal.Y)), probability, dark012 / (double)plane, dark05 / (double)plane, center / centerCount,
            MaximumRowFraction(ink, n, 0.12), MaximumColumnFraction(ink, n, 0.12), Math.Min(width, height) / Math.Max(1, Math.Max(width, height)), ratio, border / (double)Math.Max(1, borderCount), maxRing);
    }

    private static IEnumerable<(int X, int Y)> RingPoints(int center, int radius)
    {
        for (int i = 0; i < 8; i++) { double angle = i * Math.PI / 4; yield return (center + (int)Math.Round(Math.Cos(angle) * radius), center + (int)Math.Round(Math.Sin(angle) * radius)); }
    }
    private static double MaximumRowFraction(ReadOnlySpan<float> values, int n, double threshold) { double max = 0; for (int y = 0; y < n; y++) { int count = 0; for (int x = 0; x < n; x++) if (values[y * n + x] >= threshold) count++; max = Math.Max(max, count / (double)n); } return max; }
    private static double MaximumColumnFraction(ReadOnlySpan<float> values, int n, double threshold) { double max = 0; for (int x = 0; x < n; x++) { int count = 0; for (int y = 0; y < n; y++) if (values[y * n + x] >= threshold) count++; max = Math.Max(max, count / (double)n); } return max; }

    private static ProposalMarkerPatchFeatureSummary SummarizeProposal(MarkerImageFrame frame, Proposal proposal)
    {
        int planeSize = PatchSize * PatchSize;
        ReadOnlySpan<float> patch = proposal.Patch;
        static (double Mean, double Maximum) Statistics(ReadOnlySpan<float> values)
        {
            double sum = 0, maximum = double.NegativeInfinity;
            foreach (float value in values)
            {
                sum += value;
                maximum = Math.Max(maximum, value);
            }
            return (sum / values.Length, maximum);
        }
        double centerSum = 0;
        int centerCount = 0;
        for (int y = 0; y < PatchSize; y++) for (int x = 0; x < PatchSize; x++)
            if (Math.Abs(x - PatchSize / 2) <= 2 && Math.Abs(y - PatchSize / 2) <= 2)
            {
                centerSum += patch[y * PatchSize + x];
                centerCount++;
            }
        (double inkMean, double inkMaximum) = Statistics(patch[..planeSize]);
        (double ocrMean, double ocrMaximum) = Statistics(patch.Slice(planeSize, planeSize));
        (double artifactMean, double artifactMaximum) = Statistics(patch.Slice(2 * planeSize, planeSize));
        return new ProposalMarkerPatchFeatureSummary(
            frame.OriginalToFrame.MapToOriginal(new MarkerPoint(proposal.X, proposal.Y)),
            inkMean, centerSum / centerCount, inkMaximum,
            ocrMean, ocrMaximum, artifactMean, artifactMaximum);
    }

    private static MarkerCenter ToMarkerCenter(
        MarkerImageFrame frame,
        ProposalMarkerPrediction candidate,
        int index,
        bool multiradiusGeometry,
        bool maskPreservingCandidate,
        bool plotDomainProposalFiltering,
        string? suffix)
    {
        string prefix = plotDomainProposalFiltering
            ? "candidate-v25-plot-domain"
            : maskPreservingCandidate ? "candidate-v24-p1" : multiradiusGeometry ? "candidate-v23-p1" : "candidate-p2";
        string markerId = $"{prefix}{(suffix is null ? string.Empty : $"-{suffix}")}-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        int centerX = (int)Math.Round(candidate.Center.X);
        int centerY = (int)Math.Round(candidate.Center.Y);
        double artifactProbability = Math.Max(
            WindowMax(frame.OcrMask.Values, frame.Width, frame.Height, centerX, centerY, 2),
            WindowMax(frame.ArtifactMask.Values, frame.Width, frame.Height, centerX, centerY, 2));
        return new MarkerCenter(
            markerId,
            frame.OriginalToFrame.MapToOriginal(candidate.Center),
            frame.OriginalToFrame.MapFrameRadiusToOriginal(candidate.Radius),
            artifactProbability,
            candidate.Confidence,
            frame.SourceImage,
            MarkerContract.CoordinateSpace);
    }

    private sealed record Proposal(int X, int Y, float[] Patch);

    private enum RefinementFailure
    {
        Masked,
        GeometryConsensus,
    }

    private sealed class ProposalMarkerStageCounterAccumulator
    {
        public int ProposalGridPositionsConsidered;
        public int LowInkRejects;
        public int OcrMaskRejects;
        public int ArtifactMaskRejects;
        public int EmittedProposals;
        public int InferenceOutputs;
        public int OutputsAbove025;
        public int DecodedPointsMasked;
        public int GeometryConsensusRejectsAfterRefinementAttempts;
        public int DecodedPointsOutsidePlot;
        public int CandidatesBeforeNms;
        public int NmsSuppressions;
        public int FinalCandidates;

        public ProposalMarkerStageCounters ToRecord() => new(
            ProposalGridPositionsConsidered,
            LowInkRejects,
            OcrMaskRejects,
            ArtifactMaskRejects,
            EmittedProposals,
            InferenceOutputs,
            OutputsAbove025,
            DecodedPointsMasked,
            GeometryConsensusRejectsAfterRefinementAttempts,
            DecodedPointsOutsidePlot,
            CandidatesBeforeNms,
            NmsSuppressions,
            FinalCandidates);
    }

    private static IEnumerable<Proposal> EnumerateProposals(
        MarkerImageFrame frame,
        MarkerPolygon plotPolygon,
        ProposalMarkerStageCounterAccumulator counters,
        List<MarkerPoint> gridProposalCenters,
        List<MarkerPoint> inkSupportedProposalCenters,
        List<MarkerPoint> ocrUnmaskedProposalCenters,
        List<MarkerPoint> emittedProposalCenters,
        CancellationToken cancellationToken,
        bool maskPreservingCandidate = false,
        bool plotDomainProposalFiltering = false)
    {
        int width = frame.Width;
        int height = frame.Height;
        int gridWidth = (width + ProposalStride - 1) / ProposalStride;
        int gridHeight = (height + ProposalStride - 1) / ProposalStride;
        var framePolygon = plotPolygon.Points.Select(frame.OriginalToFrame.MapFromOriginal).ToArray();
        MarkerPolygon? framePlotDomain = plotDomainProposalFiltering
            ? ValidatePlotDomain(framePolygon, width, height)
            : null;
        // V24 was trained and gated with torch.unfold across the complete frame.
        // Retain that exact proposal lattice, then apply the plot polygon to
        // decoded centers below. Older diagnostic candidates keep their scoped
        // lattice to preserve their historical behavior.
        int minimumX = maskPreservingCandidate
            ? 0
            : Math.Max(0, (int)Math.Floor(framePolygon.Min(static point => point.X)) - PatchSize / 2);
        int maximumX = maskPreservingCandidate
            ? width - 1
            : Math.Min(width - 1, (int)Math.Ceiling(framePolygon.Max(static point => point.X)) + PatchSize / 2);
        int minimumY = maskPreservingCandidate
            ? 0
            : Math.Max(0, (int)Math.Floor(framePolygon.Min(static point => point.Y)) - PatchSize / 2);
        int maximumY = maskPreservingCandidate
            ? height - 1
            : Math.Min(height - 1, (int)Math.Ceiling(framePolygon.Max(static point => point.Y)) + PatchSize / 2);
        int minimumGridX = Math.Max(0, (minimumX - ProposalStride + 1) / ProposalStride);
        int maximumGridX = Math.Min(gridWidth - 1, maximumX / ProposalStride);
        int minimumGridY = Math.Max(0, (minimumY - ProposalStride + 1) / ProposalStride);
        int maximumGridY = Math.Min(gridHeight - 1, maximumY / ProposalStride);
        ReadOnlyMemory<float> luminance = frame.ChannelsFirstPixels;
        ReadOnlyMemory<float> text = frame.OcrMask.Values;
        ReadOnlyMemory<float> artifact = frame.ArtifactMask.Values;
        for (int gy = minimumGridY; gy <= maximumGridY; gy++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int gx = minimumGridX; gx <= maximumGridX; gx++)
            {
                int x = gx * ProposalStride;
                int y = gy * ProposalStride;
                MarkerPoint originalCenter =
                    frame.OriginalToFrame.MapToOriginal(new MarkerPoint(x, y));
                counters.ProposalGridPositionsConsidered++;
                gridProposalCenters.Add(originalCenter);
                if (WindowMaxInk(luminance, width, height, x, y, 8) < InkSupportThreshold)
                {
                    counters.LowInkRejects++;
                    continue;
                }
                inkSupportedProposalCenters.Add(originalCenter);
                if (framePlotDomain is not null &&
                    !SupportsPlotDomain(framePlotDomain, new MarkerPoint(x, y)))
                {
                    continue;
                }

                if (!maskPreservingCandidate && WindowMax(text, width, height, x, y, 2) >= MaskRejectionThreshold)
                {
                    counters.OcrMaskRejects++;
                    continue;
                }
                ocrUnmaskedProposalCenters.Add(originalCenter);
                if (!maskPreservingCandidate && WindowMax(artifact, width, height, x, y, 2) >= MaskRejectionThreshold)
                {
                    counters.ArtifactMaskRejects++;
                    continue;
                }

                float[] patch = new float[checked(3 * PatchSize * PatchSize)];
                int planeSize = PatchSize * PatchSize;
                for (int py = 0; py < PatchSize; py++)
                {
                    for (int px = 0; px < PatchSize; px++)
                    {
                        int sourceX = x + px - (PatchSize / 2);
                        int sourceY = y + py - (PatchSize / 2);
                        int patchIndex = py * PatchSize + px;
                        if ((uint)sourceX < (uint)width && (uint)sourceY < (uint)height)
                        {
                            int sourceIndex = sourceY * width + sourceX;
                            patch[patchIndex] = 1 - luminance.Span[sourceIndex];
                            patch[planeSize + patchIndex] = text.Span[sourceIndex];
                            patch[(2 * planeSize) + patchIndex] = artifact.Span[sourceIndex];
                        }
                    }
                }
                counters.EmittedProposals++;
                emittedProposalCenters.Add(originalCenter);
                yield return new Proposal(x, y, patch);
            }
        }
    }

    private static MarkerPolygon ValidatePlotDomain(
        MarkerPoint[] points,
        int width,
        int height)
    {
        if (points.Length != 4 ||
            points.Distinct().Count() != 4 ||
            points.Any(point => !point.IsFinite ||
                point.X < 0 || point.X > width || point.Y < 0 || point.Y > height) ||
            Math.Abs(SignedArea(points)) <= 1e-9 ||
            SegmentsCross(points[0], points[1], points[2], points[3]) ||
            SegmentsCross(points[1], points[2], points[3], points[0]))
        {
            throw new ArgumentException(
                "V25 plot-domain proposals require a simple four-point polygon within the detector frame.",
                nameof(points));
        }

        return new MarkerPolygon(points);
    }

    private static bool SupportsPlotDomain(MarkerPolygon polygon, MarkerPoint point)
    {
        if (polygon.Contains(point))
        {
            return true;
        }

        for (int index = 0; index < polygon.Points.Count; index++)
        {
            MarkerPoint current = polygon.Points[index];
            MarkerPoint previous = polygon.Points[index == 0 ? polygon.Points.Count - 1 : index - 1];
            if (DistanceToSegment(point, current, previous) <= PlotDomainBoundaryPixels)
            {
                return true;
            }
        }

        return false;
    }

    private static double SignedArea(MarkerPoint[] points)
    {
        double sum = 0;
        for (int index = 0; index < points.Length; index++)
        {
            MarkerPoint current = points[index];
            MarkerPoint next = points[(index + 1) % points.Length];
            sum += (current.X * next.Y) - (next.X * current.Y);
        }

        return 0.5 * sum;
    }

    private static bool SegmentsCross(
        MarkerPoint a,
        MarkerPoint b,
        MarkerPoint c,
        MarkerPoint d) =>
        Orientation(a, b, c) * Orientation(a, b, d) < 0 &&
        Orientation(c, d, a) * Orientation(c, d, b) < 0;

    private static double Orientation(MarkerPoint a, MarkerPoint b, MarkerPoint c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static double DistanceToSegment(
        MarkerPoint point,
        MarkerPoint a,
        MarkerPoint b)
    {
        double deltaX = b.X - a.X;
        double deltaY = b.Y - a.Y;
        double denominator = (deltaX * deltaX) + (deltaY * deltaY);
        if (denominator == 0)
        {
            return Distance(point, a);
        }

        double position = Math.Clamp(
            (((point.X - a.X) * deltaX) + ((point.Y - a.Y) * deltaY)) / denominator,
            0,
            1);
        return Distance(
            point,
            new MarkerPoint(a.X + (position * deltaX), a.Y + (position * deltaY)));
    }

    private static float WindowMax(ReadOnlyMemory<float> values, int width, int height, int centerX, int centerY, int radius)
    {
        float maximum = 0;
        for (int y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); y++)
        {
            for (int x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); x++)
            {
                maximum = Math.Max(maximum, values.Span[(y * width) + x]);
            }
        }
        return maximum;
    }

    private static float WindowMaxInk(ReadOnlyMemory<float> luminance, int width, int height, int centerX, int centerY, int radius)
    {
        float maximum = 0;
        for (int y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); y++)
        {
            for (int x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); x++)
            {
                maximum = Math.Max(maximum, 1 - luminance.Span[(y * width) + x]);
            }
        }
        return maximum;
    }

    private static bool TryRefine(
        MarkerImageFrame frame,
        double x,
        double y,
        double radius,
        bool multiradiusGeometry,
        bool maskPreservingCandidate,
        out MarkerPoint refined,
        out RefinementFailure failure)
    {
        if (!maskPreservingCandidate && !CenterIsUnmasked(frame, x, y))
        {
            refined = default;
            failure = RefinementFailure.Masked;
            return false;
        }

        if (GeometryConsensus(frame, x, y, radius, multiradiusGeometry))
        {
            refined = new MarkerPoint(x, y);
            failure = default;
            return true;
        }

        // V24's sealed postprocessor evaluates consensus only at the decoded
        // point. Moving a failed point would make production behavior differ
        // from the Python gate that approved the model.
        if (maskPreservingCandidate)
        {
            refined = default;
            failure = RefinementFailure.GeometryConsensus;
            return false;
        }

        var candidates = new List<(double Distance, double AbsY, double AbsX, double Dy, double Dx, double X, double Y)>();
        foreach (double dy in new[] { -1d, 0d, 1d })
        {
            foreach (double dx in new[] { -1d, 0d, 1d })
            {
                double candidateX = x + dx;
                double candidateY = y + dy;
                if ((maskPreservingCandidate || CenterIsUnmasked(frame, candidateX, candidateY)) &&
                    GeometryConsensus(frame, candidateX, candidateY, radius, multiradiusGeometry))
                {
                    candidates.Add((dx * dx + dy * dy, Math.Abs(dy), Math.Abs(dx), dy, dx, candidateX, candidateY));
                }
            }
        }
        if (candidates.Count > 0)
        {
            var best = candidates.Min();
            refined = new MarkerPoint(best.X, best.Y);
            failure = default;
            return true;
        }
        refined = default;
        failure = RefinementFailure.GeometryConsensus;
        return false;
    }

    private static bool CenterIsUnmasked(MarkerImageFrame frame, double x, double y)
    {
        int ix = (int)Math.Round(x);
        int iy = (int)Math.Round(y);
        return ix >= 0 && iy >= 0 && ix < frame.Width && iy < frame.Height &&
            WindowMax(frame.OcrMask.Values, frame.Width, frame.Height, ix, iy, 2) < MaskRejectionThreshold &&
            WindowMax(frame.ArtifactMask.Values, frame.Width, frame.Height, ix, iy, 2) < MaskRejectionThreshold;
    }

    private static bool GeometryConsensus(
        MarkerImageFrame frame,
        double x,
        double y,
        double radius,
        bool multiradiusGeometry)
    {
        if (multiradiusGeometry)
        {
            for (int ring = 3; ring <= 12; ring++)
            {
                if (GeometryConsensusAtRadius(frame, x, y, ring))
                {
                    return true;
                }
            }

            return false;
        }

        return GeometryConsensusAtRadius(frame, x, y, radius);
    }

    private static bool GeometryConsensusAtRadius(MarkerImageFrame frame, double x, double y, double radius)
    {
        int ix = (int)Math.Round(x);
        int iy = (int)Math.Round(y);
        int ring = Math.Max(3, (int)Math.Round(radius));
        int[][] points =
        [
            [ix - ring, iy], [ix + ring, iy], [ix, iy - ring], [ix, iy + ring],
            [ix - ring, iy - ring], [ix + ring, iy - ring], [ix - ring, iy + ring], [ix + ring, iy + ring],
        ];
        ReadOnlySpan<float> luminance = frame.ChannelsFirstPixels.Span;
        int support = 0;
        foreach (int[] point in points)
        {
            if ((uint)point[0] < (uint)frame.Width && (uint)point[1] < (uint)frame.Height &&
                1 - luminance[(point[1] * frame.Width) + point[0]] >= 0.12f)
            {
                support++;
            }
        }
        int left = Math.Max(0, ix - 2);
        int top = Math.Max(0, iy - 2);
        int right = Math.Min(frame.Width, ix + 3);
        int bottom = Math.Min(frame.Height, iy + 3);
        double sum = 0;
        int count = 0;
        for (int py = top; py < bottom; py++)
        {
            for (int px = left; px < right; px++)
            {
                sum += 1 - luminance[(py * frame.Width) + px];
                count++;
            }
        }
        return support >= 3 || (count > 0 && sum / count >= 0.28);
    }

    private List<ProposalMarkerPrediction> ApplyNms(
        IEnumerable<ProposalMarkerPrediction> values,
        out int suppressions)
    {
        // V24's Python postprocessor uses a 5px floor; keep the older
        // candidates' 6.5px behavior unchanged.
        double minimumSeparation = maskPreservingCandidate ? 5.0 : MinimumCenterSeparation;
        var accepted = new List<ProposalMarkerPrediction>();
        suppressions = 0;
        var buckets = new Dictionary<(int X, int Y), List<ProposalMarkerPrediction>>();
        foreach (ProposalMarkerPrediction candidate in values
                     .OrderByDescending(static item => item.Confidence)
                     .ThenBy(static item => item.Center.Y)
                     .ThenBy(static item => item.Center.X))
        {
            int bucketX = (int)Math.Floor(candidate.Center.X / minimumSeparation);
            int bucketY = (int)Math.Floor(candidate.Center.Y / minimumSeparation);
            bool suppressed = false;
            for (int y = bucketY - 2; y <= bucketY + 2 && !suppressed; y++)
            {
                for (int x = bucketX - 2; x <= bucketX + 2 && !suppressed; x++)
                {
                    if (!buckets.TryGetValue((x, y), out List<ProposalMarkerPrediction>? neighbors))
                    {
                        continue;
                    }

                    suppressed = neighbors.Any(current => Distance(candidate.Center, current.Center) <
                        Math.Max(minimumSeparation, RadiusSuppressionScale * Math.Max(candidate.Radius, current.Radius)));
                }
            }
            if (suppressed)
            {
                suppressions++;
                continue;
            }

            accepted.Add(candidate);
            buckets.GetValueOrDefault((bucketX, bucketY))?.Add(candidate);
            if (!buckets.ContainsKey((bucketX, bucketY)))
            {
                buckets[(bucketX, bucketY)] = [candidate];
            }
        }

        return accepted;
    }

    private static double Distance(MarkerPoint left, MarkerPoint right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

    private static void ValidateFrame(MarkerImageFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.ChannelCount != 1 ||
            !frame.OriginalToFrame.IsInvertible ||
            frame.ChannelsFirstPixels.Length != checked(frame.Width * frame.Height) ||
            frame.OcrMask.Width != frame.Width || frame.OcrMask.Height != frame.Height ||
            frame.OcrMask.Values.Length != checked(frame.Width * frame.Height) ||
            frame.ArtifactMask.Width != frame.Width || frame.ArtifactMask.Height != frame.Height ||
            frame.ArtifactMask.Values.Length != checked(frame.Width * frame.Height) ||
            !AreNormalized(frame.ChannelsFirstPixels.Span) ||
            !AreNormalized(frame.OcrMask.Values.Span) ||
            !AreNormalized(frame.ArtifactMask.Values.Span))
        {
            throw new ArgumentException("Candidate marker frame must contain one finite luminance plane and matching masks.", nameof(frame));
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

    private static ProductionWorkflowStageException Failure(
        string code,
        string userMessageKey,
        string technicalMessage,
        string suggestedAction) => new(new ProductionWorkflowFailure(
            code, userMessageKey, technicalMessage, Recoverable: true, suggestedAction));
}
