// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;

namespace GraphReader.Ocr;

public sealed record GraphStructureConsensusTextRegionDetectorOptions
{
    public double MinimumOverlapCoefficient { get; init; } = 0.50;

    public double MinimumTextLikelihood { get; init; } = 0.45;
}

/// <summary>
/// Keeps at most one model detection for each independently derived
/// connected-component text candidate. Candidate regions must carry explicit
/// non-structure evidence. This boundary rejects graph-shaped detections
/// without substituting heuristic regions for model detections.
/// </summary>
public sealed class GraphStructureConsensusTextRegionDetector : ITextRegionDetector
{
    public const string CompositionVersion = "graph-structure-consensus-v1";

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
            this.options.MinimumTextLikelihood is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public string ConfigurationFingerprint => string.Create(
        CultureInfo.InvariantCulture,
        $"{CompositionVersion}:{options.MinimumOverlapCoefficient:R}:{options.MinimumTextLikelihood:R}:model={modelDetector.ConfigurationFingerprint}:candidate={structureCandidateDetector.ConfigurationFingerprint}");

    public async ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<OcrDetectedRegion> modelRegions = await modelDetector
            .DetectAsync(image, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (modelRegions.Count == 0)
        {
            return Array.Empty<OcrDetectedRegion>();
        }

        IReadOnlyList<OcrDetectedRegion> candidateRegions = await structureCandidateDetector
            .DetectAsync(image, cancellationToken)
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
            output.Add(model with
            {
                OrientationDegrees = Math.Abs(model.OrientationDegrees) <= double.Epsilon
                    ? candidate.OrientationDegrees
                    : model.OrientationDegrees,
                Context = model.Context ?? candidate.Context,
                Evidence = candidate.Evidence,
            });
        }

        return Array.AsReadOnly(output
            .OrderBy(static region => region.Polygon.Bounds.Top)
            .ThenBy(static region => region.Polygon.Bounds.Left)
            .ThenBy(static region => region.RegionId, StringComparer.Ordinal)
            .ToArray());
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

    private readonly record struct IndexedRegion(int Index, OcrDetectedRegion Region);

    private readonly record struct Match(
        int ModelIndex,
        int CandidateIndex,
        double OverlapCoefficient);
}
