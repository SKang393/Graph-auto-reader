// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

public enum GraphStructureConsensusGeometry
{
    ModelPolygon = 0,
    MatchedComponent = 1,
}

public enum GraphStructureModelInput
{
    AxisMasked = 0,
    Original = 1,
}

public sealed record GraphStructureConsensusTextRegionDetectorOptions
{
    public double MinimumOverlapCoefficient { get; init; } = 0.50;

    public double MinimumTextLikelihood { get; init; } = 0.45;

    public GraphStructureConsensusGeometry OutputGeometry { get; init; } =
        GraphStructureConsensusGeometry.ModelPolygon;

    public GraphStructureModelInput ModelInput { get; init; } =
        GraphStructureModelInput.AxisMasked;
}

/// <summary>
/// Keeps at most one model detection for each independently derived
/// connected-component text candidate. Candidate regions must carry explicit
/// non-structure evidence. This boundary rejects graph-shaped detections
/// without substituting heuristic regions for model detections.
/// </summary>
public sealed class GraphStructureConsensusTextRegionDetector : IDualInputTextRegionDetector
{
    public const string CompositionVersion = "graph-structure-consensus-v1";

    public const string MatchedComponentCompositionVersion =
        "graph-structure-consensus-component-geometry-v1";

    public const string OriginalModelInputCompositionVersion =
        "graph-structure-consensus-original-model-input-v1";

    public const string MatchedComponentOriginalModelInputCompositionVersion =
        "graph-structure-consensus-component-geometry-original-model-input-v1";

    private readonly ITextRegionDetector modelDetector;
    private readonly ITextRegionDetector structureCandidateDetector;
    private readonly GraphStructureConsensusTextRegionDetectorOptions options;

    public GraphStructureConsensusTextRegionDetector(
        ITextRegionDetector modelDetector,
        ITextRegionDetector structureCandidateDetector,
        GraphStructureConsensusTextRegionDetectorOptions? options = null)
    {
        this.modelDetector = modelDetector ?? throw new ArgumentNullException(nameof(modelDetector));
        this.structureCandidateDetector = structureCandidateDetector ??
            throw new ArgumentNullException(nameof(structureCandidateDetector));
        this.options = options ?? new GraphStructureConsensusTextRegionDetectorOptions();
        if (!double.IsFinite(this.options.MinimumOverlapCoefficient) ||
            this.options.MinimumOverlapCoefficient is <= 0 or > 1 ||
            !double.IsFinite(this.options.MinimumTextLikelihood) ||
            this.options.MinimumTextLikelihood is < 0 or > 1 ||
            !Enum.IsDefined(this.options.OutputGeometry) ||
            !Enum.IsDefined(this.options.ModelInput))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public string ConfigurationFingerprint => string.Create(
        CultureInfo.InvariantCulture,
        $"{GetCompositionVersion(options.OutputGeometry, options.ModelInput)}:{options.MinimumOverlapCoefficient:R}:{options.MinimumTextLikelihood:R}:model={modelDetector.ConfigurationFingerprint}:candidate={structureCandidateDetector.ConfigurationFingerprint}");

    public static string GetCompositionVersion(GraphStructureConsensusGeometry geometry) => geometry switch
    {
        GraphStructureConsensusGeometry.ModelPolygon => CompositionVersion,
        GraphStructureConsensusGeometry.MatchedComponent => MatchedComponentCompositionVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(geometry)),
    };

    public static string GetCompositionVersion(
        GraphStructureConsensusGeometry geometry,
        GraphStructureModelInput modelInput) => (geometry, modelInput) switch
    {
        (GraphStructureConsensusGeometry.ModelPolygon, GraphStructureModelInput.AxisMasked) =>
            CompositionVersion,
        (GraphStructureConsensusGeometry.MatchedComponent, GraphStructureModelInput.AxisMasked) =>
            MatchedComponentCompositionVersion,
        (GraphStructureConsensusGeometry.ModelPolygon, GraphStructureModelInput.Original) =>
            OriginalModelInputCompositionVersion,
        (GraphStructureConsensusGeometry.MatchedComponent, GraphStructureModelInput.Original) =>
            MatchedComponentOriginalModelInputCompositionVersion,
        (_, var invalidInput) when !Enum.IsDefined(invalidInput) =>
            throw new ArgumentOutOfRangeException(nameof(modelInput)),
        _ => throw new ArgumentOutOfRangeException(nameof(geometry)),
    };

    public async ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.ModelInput == GraphStructureModelInput.Original)
        {
            throw new InvalidOperationException(
                "Original model input requires both immutable original and detector images.");
        }

        return await DetectCoreAsync(image, image, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage originalImage,
        OcrImage detectorImage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(originalImage);
        ArgumentNullException.ThrowIfNull(detectorImage);
        cancellationToken.ThrowIfCancellationRequested();

        OcrImage modelImage = options.ModelInput switch
        {
            GraphStructureModelInput.AxisMasked => detectorImage,
            GraphStructureModelInput.Original => ValidateAndSelectOriginal(
                originalImage,
                detectorImage),
            _ => throw new InvalidOperationException("Unsupported consensus model input."),
        };
        return await DetectCoreAsync(modelImage, detectorImage, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectCoreAsync(
        OcrImage modelImage,
        OcrImage structureImage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<OcrDetectedRegion> modelRegions = await modelDetector
            .DetectAsync(modelImage, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (modelRegions.Count == 0)
        {
            return Array.Empty<OcrDetectedRegion>();
        }

        IReadOnlyList<OcrDetectedRegion> candidateRegions = await structureCandidateDetector
            .DetectAsync(structureImage, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRegions(modelRegions, requireEvidence: false, "model");
        ValidateRegions(candidateRegions, requireEvidence: true, "structure candidate");

        IndexedRegion[] eligibleCandidates = candidateRegions
            .Select(static (region, index) => new IndexedRegion(index, region))
            .Where(item =>
                item.Region.Evidence is { LikelyGraphStructure: false } evidence &&
                evidence.TextLikelihood >= options.MinimumTextLikelihood)
            .ToArray();
        Match[] matches = modelRegions
            .SelectMany((modelRegion, modelIndex) => eligibleCandidates.Select(candidate =>
                new Match(
                    modelIndex,
                    candidate.Index,
                    OverlapCoefficient(modelRegion.Polygon.Bounds, candidate.Region.Polygon.Bounds))))
            .Where(match => match.OverlapCoefficient >= options.MinimumOverlapCoefficient)
            .OrderByDescending(match => modelRegions[match.ModelIndex].DetectionConfidence)
            .ThenByDescending(static match => match.OverlapCoefficient)
            .ThenByDescending(match =>
                candidateRegions[match.CandidateIndex].Evidence!.TextLikelihood)
            .ThenBy(match => modelRegions[match.ModelIndex].RegionId, StringComparer.Ordinal)
            .ThenBy(match => candidateRegions[match.CandidateIndex].RegionId, StringComparer.Ordinal)
            .ToArray();

        var usedModels = new HashSet<int>();
        var usedCandidates = new HashSet<int>();
        var output = new List<OcrDetectedRegion>();
        foreach (Match match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (usedModels.Contains(match.ModelIndex) || usedCandidates.Contains(match.CandidateIndex))
            {
                continue;
            }

            usedModels.Add(match.ModelIndex);
            usedCandidates.Add(match.CandidateIndex);
            OcrDetectedRegion model = modelRegions[match.ModelIndex];
            OcrDetectedRegion candidate = candidateRegions[match.CandidateIndex];
            output.Add(options.OutputGeometry switch
            {
                GraphStructureConsensusGeometry.ModelPolygon => model with
                {
                    OrientationDegrees = Math.Abs(model.OrientationDegrees) <= double.Epsilon
                        ? candidate.OrientationDegrees
                        : model.OrientationDegrees,
                    Context = model.Context ?? candidate.Context,
                    Evidence = candidate.Evidence,
                },
                GraphStructureConsensusGeometry.MatchedComponent => model with
                {
                    RegionId = MatchedComponentRegionId(model, candidate),
                    Polygon = candidate.Polygon,
                    OrientationDegrees = candidate.OrientationDegrees,
                    Context = model.Context ?? candidate.Context,
                    Evidence = MatchedComponentEvidence(model, candidate),
                },
                _ => throw new InvalidOperationException("Unsupported consensus output geometry."),
            });
        }

        return Array.AsReadOnly(output
            .OrderBy(static region => region.Polygon.Bounds.Top)
            .ThenBy(static region => region.Polygon.Bounds.Left)
            .ThenBy(static region => region.RegionId, StringComparer.Ordinal)
            .ToArray());
    }

    private static OcrImage ValidateAndSelectOriginal(
        OcrImage originalImage,
        OcrImage detectorImage)
    {
        ValidateAlignedInputs(originalImage, detectorImage);
        return originalImage;
    }

    private static void ValidateAlignedInputs(OcrImage originalImage, OcrImage detectorImage)
    {
        if (!HasValidLayout(originalImage) ||
            !HasValidLayout(detectorImage) ||
            originalImage.SourceImage != OcrSourceImage.Original ||
            detectorImage.SourceImage != OcrSourceImage.Original ||
            !originalImage.OriginalToImage.IsInvertible ||
            !detectorImage.OriginalToImage.IsInvertible ||
            !string.Equals(
                originalImage.CoordinateSpace,
                OcrContract.CoordinateSpace,
                StringComparison.Ordinal) ||
            !string.Equals(
                detectorImage.CoordinateSpace,
                OcrContract.CoordinateSpace,
                StringComparison.Ordinal) ||
            !HasValidCanonicalDimensions(originalImage) ||
            !HasValidCanonicalDimensions(detectorImage) ||
            originalImage.Width != detectorImage.Width ||
            originalImage.Height != detectorImage.Height ||
            originalImage.Stride != detectorImage.Stride ||
            originalImage.OriginalToImage != detectorImage.OriginalToImage ||
            !string.Equals(
                originalImage.CoordinateSpace,
                detectorImage.CoordinateSpace,
                StringComparison.Ordinal) ||
            originalImage.CanonicalOriginalWidth != detectorImage.CanonicalOriginalWidth ||
            originalImage.CanonicalOriginalHeight != detectorImage.CanonicalOriginalHeight ||
            (originalImage.BgrPixels is null) != (detectorImage.BgrPixels is null))
        {
            throw new InvalidDataException(
                "Original and detector OCR images must have aligned dimensions, transforms, and provenance.");
        }
    }

    private static bool HasValidCanonicalDimensions(OcrImage image) =>
        (image.CanonicalOriginalWidth is null && image.CanonicalOriginalHeight is null) ||
        (image.CanonicalOriginalWidth > 0 && image.CanonicalOriginalHeight > 0);

    private static bool HasValidLayout(OcrImage image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Stride < image.Width ||
            image.Pixels.Length != checked(image.Stride * image.Height))
        {
            return false;
        }

        if (image.BgrPixels is not { } bgr)
        {
            return true;
        }

        return bgr.Stride >= checked(image.Width * 3) &&
            bgr.Pixels.Length == checked(bgr.Stride * image.Height);
    }

    private static void ValidateRegions(
        IReadOnlyList<OcrDetectedRegion> regions,
        bool requireEvidence,
        string label)
    {
        ArgumentNullException.ThrowIfNull(regions);
        foreach (OcrDetectedRegion region in regions)
        {
            OcrRegionEvidence? evidence = region.Evidence;
            bool invalidEvidence = evidence is not null &&
                (evidence.ComponentCount < 0 ||
                 !double.IsFinite(evidence.InkDensity) || evidence.InkDensity is < 0 or > 1 ||
                 !double.IsFinite(evidence.TextLikelihood) || evidence.TextLikelihood is < 0 or > 1 ||
                 !double.IsFinite(evidence.StructureLikelihood) || evidence.StructureLikelihood is < 0 or > 1);
            if (string.IsNullOrWhiteSpace(region.RegionId) ||
                !region.Polygon.Bounds.IsValid ||
                !double.IsFinite(region.OrientationDegrees) ||
                !double.IsFinite(region.DetectionConfidence) ||
                region.DetectionConfidence is < 0 or > 1 ||
                !string.Equals(region.CoordinateSpace, OcrContract.CoordinateSpace, StringComparison.Ordinal) ||
                (requireEvidence && evidence is null) ||
                invalidEvidence)
            {
                throw new InvalidDataException($"The {label} detector returned invalid evidence.");
            }
        }
    }

    private static double OverlapCoefficient(OcrRectangle left, OcrRectangle right)
    {
        double intersectionWidth = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));
        double intersectionHeight = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top));
        double intersection = intersectionWidth * intersectionHeight;
        double denominator = Math.Min(left.Width * left.Height, right.Width * right.Height);
        return denominator <= 0 ? 0 : intersection / denominator;
    }

    private static string MatchedComponentRegionId(
        OcrDetectedRegion model,
        OcrDetectedRegion candidate)
    {
        using var material = new MemoryStream();
        using (var writer = new BinaryWriter(material, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(MatchedComponentCompositionVersion);
            writer.Write(model.RegionId);
            writer.Write(candidate.RegionId);
            writer.Write(candidate.Polygon.Points.Count);
            foreach (OcrPoint point in candidate.Polygon.Points)
            {
                writer.Write(BitConverter.DoubleToInt64Bits(point.X));
                writer.Write(BitConverter.DoubleToInt64Bits(point.Y));
            }
        }

        byte[] hash = SHA256.HashData(material.ToArray());
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static OcrRegionEvidence MatchedComponentEvidence(
        OcrDetectedRegion model,
        OcrDetectedRegion candidate)
    {
        OcrRegionEvidence evidence = candidate.Evidence ??
            throw new InvalidOperationException("Matched component evidence is missing.");
        return evidence with
        {
            Reasons = Array.AsReadOnly(evidence.Reasons
                .Concat([
                    $"consensus_model_region_id:{model.RegionId}",
                    $"consensus_component_region_id:{candidate.RegionId}",
                ])
                .ToArray()),
        };
    }

    private readonly record struct IndexedRegion(int Index, OcrDetectedRegion Region);

    private readonly record struct Match(
        int ModelIndex,
        int CandidateIndex,
        double OverlapCoefficient);
}
