// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections;
using GraphReader.Markers.Classification;
using GraphReader.Ocr;

namespace GraphReader.Legends;

public static class LegendReasoningContract
{
    public const int Version = 1;
    public const string Stage = "legends";
    public const string StageVersion = "0.1.2";
    public const string CoordinateSpace = "original_pixels";
}

public enum LegendRegionLocation
{
    InsidePlot,
    OutsidePlot,
}

public enum LegendEvidenceSource
{
    DetectedLegend,
    CrossPanel,
    SymbolFallback,
    UserConfirmed,
}

public enum LegendSemanticHint
{
    Unknown,
    Generalization,
    Maintenance,
}

public enum LegendArtifactKind
{
    ArrowShaft,
    Arrowhead,
}

public readonly record struct LegendPoint(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    public double DistanceTo(LegendPoint other) => Math.Sqrt(
        Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2));
}

public readonly record struct LegendRectangle(double X, double Y, double Width, double Height)
{
    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public LegendPoint Center => new(X + (Width / 2), Y + (Height / 2));

    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height) &&
        Width > 0 && Height > 0;

    public bool Contains(LegendPoint point) =>
        point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    public static LegendRectangle Union(LegendRectangle left, LegendRectangle right)
    {
        double minimumX = Math.Min(left.Left, right.Left);
        double minimumY = Math.Min(left.Top, right.Top);
        double maximumX = Math.Max(left.Right, right.Right);
        double maximumY = Math.Max(left.Bottom, right.Bottom);
        return new LegendRectangle(minimumX, minimumY, maximumX - minimumX, maximumY - minimumY);
    }
}

public sealed record LegendTextRegion(
    string RegionId,
    LegendRectangle Bounds,
    string Text,
    OcrTextRole Role,
    double Confidence,
    OcrReviewStatus ReviewStatus = OcrReviewStatus.Unreviewed);

public sealed class LegendGlyphCandidate
{
    public LegendGlyphCandidate(
        string glyphId,
        LegendRectangle bounds,
        MarkerShape shape,
        MarkerFill fill,
        IEnumerable<float> embedding,
        double confidence)
    {
        GlyphId = glyphId ?? throw new ArgumentNullException(nameof(glyphId));
        Bounds = bounds;
        Shape = shape;
        Fill = fill;
        Embedding = LegendCollections.Freeze(embedding ?? throw new ArgumentNullException(nameof(embedding)));
        Confidence = confidence;
    }

    public string GlyphId { get; }

    public LegendRectangle Bounds { get; }

    public MarkerShape Shape { get; }

    public MarkerFill Fill { get; }

    public IReadOnlyList<float> Embedding { get; }

    public double Confidence { get; }
}

public sealed class LegendSeriesCandidate
{
    public LegendSeriesCandidate(
        string seriesId,
        MarkerShape shape,
        MarkerFill fill,
        string symbol,
        string accessibleName,
        IEnumerable<string> markerIds,
        IEnumerable<float> embedding,
        string? currentName = null,
        bool userConfirmedName = false)
    {
        SeriesId = seriesId ?? throw new ArgumentNullException(nameof(seriesId));
        Shape = shape;
        Fill = fill;
        Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        AccessibleName = accessibleName ?? throw new ArgumentNullException(nameof(accessibleName));
        MarkerIds = LegendCollections.Freeze(markerIds ?? throw new ArgumentNullException(nameof(markerIds)));
        Embedding = LegendCollections.Freeze(embedding ?? throw new ArgumentNullException(nameof(embedding)));
        CurrentName = currentName;
        UserConfirmedName = userConfirmedName;
    }

    public string SeriesId { get; }

    public MarkerShape Shape { get; }

    public MarkerFill Fill { get; }

    public string Symbol { get; }

    public string AccessibleName { get; }

    public IReadOnlyList<string> MarkerIds { get; }

    public IReadOnlyList<float> Embedding { get; }

    public string? CurrentName { get; }

    public bool UserConfirmedName { get; }
}

public sealed record LegendPlotMarker(
    string MarkerId,
    string SeriesId,
    LegendPoint Center,
    MarkerShape Shape,
    MarkerFill Fill);

public sealed record LegendStrokeCandidate(
    string StrokeId,
    LegendPoint Start,
    LegendPoint End,
    double Thickness,
    double Confidence);

public sealed class LegendTriangleCandidate
{
    public LegendTriangleCandidate(
        string triangleId,
        IEnumerable<LegendPoint> points,
        double confidence)
    {
        TriangleId = triangleId ?? throw new ArgumentNullException(nameof(triangleId));
        Points = LegendCollections.Freeze(points ?? throw new ArgumentNullException(nameof(points)));
        Confidence = confidence;
    }

    public string TriangleId { get; }

    public IReadOnlyList<LegendPoint> Points { get; }

    public double Confidence { get; }

    public LegendRectangle Bounds
    {
        get
        {
            double minimumX = Points.Min(static point => point.X);
            double minimumY = Points.Min(static point => point.Y);
            double maximumX = Points.Max(static point => point.X);
            double maximumY = Points.Max(static point => point.Y);
            return new LegendRectangle(minimumX, minimumY, maximumX - minimumX, maximumY - minimumY);
        }
    }

    public LegendPoint Center => new(
        Points.Average(static point => point.X),
        Points.Average(static point => point.Y));
}

public sealed record LegendSemanticEvidence(
    LegendSemanticHint Hint,
    string NormalizedText,
    double Confidence);

public sealed record LegendEntry(
    string EntryId,
    string GlyphId,
    string TextRegionId,
    string Text,
    MarkerShape Shape,
    MarkerFill Fill,
    double Confidence,
    string SourcePanelId,
    LegendEvidenceSource Source,
    LegendSemanticEvidence Semantic,
    string? NormalizedSeriesId = null);

public sealed class LegendRegion
{
    public LegendRegion(
        string regionId,
        LegendRectangle bounds,
        LegendRegionLocation location,
        IEnumerable<LegendEntry> entries,
        double confidence)
    {
        RegionId = regionId ?? throw new ArgumentNullException(nameof(regionId));
        Bounds = bounds;
        Location = location;
        Entries = LegendCollections.Freeze(entries ?? throw new ArgumentNullException(nameof(entries)));
        Confidence = confidence;
    }

    public string RegionId { get; }

    public LegendRectangle Bounds { get; }

    public LegendRegionLocation Location { get; }

    public IReadOnlyList<LegendEntry> Entries { get; }

    public double Confidence { get; }
}

public sealed record LegendSeriesResolution(
    string SeriesId,
    string Name,
    string Symbol,
    string AccessibleName,
    LegendEvidenceSource Source,
    string? EntryId,
    string? SourcePanelId,
    double Confidence,
    LegendSemanticEvidence Semantic,
    bool UserConfirmedPreserved);

public sealed record LegendAnnotationCallout(
    string CalloutId,
    string TextRegionId,
    string Text,
    string TargetMarkerId,
    string StrokeId,
    string ArrowheadId,
    double Confidence);

public sealed record LegendArtifact(
    string ArtifactId,
    LegendArtifactKind Kind,
    LegendRectangle Bounds,
    double Confidence);

public sealed record LegendParticipantMetadata(
    string TextRegionId,
    string Name,
    LegendRectangle Bounds,
    double Confidence);

public sealed class LegendPeerPanelEvidence
{
    public LegendPeerPanelEvidence(string panelId, IEnumerable<LegendEntry> entries)
    {
        PanelId = panelId ?? throw new ArgumentNullException(nameof(panelId));
        Entries = LegendCollections.Freeze(entries ?? throw new ArgumentNullException(nameof(entries)));
    }

    public string PanelId { get; }

    public IReadOnlyList<LegendEntry> Entries { get; }
}

public sealed record LegendReasoningOptions
{
    public double MaximumGlyphTextHorizontalGap { get; init; } = 90;

    public double MaximumGlyphTextVerticalOffset { get; init; } = 14;

    public double MinimumPairConfidence { get; init; } = 0.60;

    public double MinimumEmbeddingSimilarity { get; init; } = 0.75;

    public double MaximumArrowJoinDistance { get; init; } = 12;

    public double MaximumArrowTargetDistance { get; init; } = 28;

    public double MaximumAnnotationTextDistance { get; init; } = 80;

    public double ParticipantBandFraction { get; init; } = 0.18;

    public double MinimumParticipantConfidence { get; init; } = 0.65;

    public string StageVersion { get; init; } = LegendReasoningContract.StageVersion;
}

public sealed class LegendReasoningRequest
{
    public LegendReasoningRequest(
        string projectId,
        string panelId,
        string inputSha256,
        LegendRectangle panelBounds,
        LegendRectangle plotBounds,
        IEnumerable<LegendTextRegion> textRegions,
        IEnumerable<LegendGlyphCandidate> glyphs,
        IEnumerable<LegendSeriesCandidate> series,
        IEnumerable<LegendPlotMarker> plotMarkers,
        IEnumerable<LegendStrokeCandidate>? strokes = null,
        IEnumerable<LegendTriangleCandidate>? triangles = null,
        IEnumerable<LegendPeerPanelEvidence>? peerPanels = null,
        LegendReasoningOptions? options = null,
        int contractVersion = LegendReasoningContract.Version)
    {
        ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
        PanelId = panelId ?? throw new ArgumentNullException(nameof(panelId));
        InputSha256 = inputSha256 ?? throw new ArgumentNullException(nameof(inputSha256));
        PanelBounds = panelBounds;
        PlotBounds = plotBounds;
        TextRegions = LegendCollections.Freeze(textRegions ?? throw new ArgumentNullException(nameof(textRegions)));
        Glyphs = LegendCollections.Freeze(glyphs ?? throw new ArgumentNullException(nameof(glyphs)));
        Series = LegendCollections.Freeze(series ?? throw new ArgumentNullException(nameof(series)));
        PlotMarkers = LegendCollections.Freeze(plotMarkers ?? throw new ArgumentNullException(nameof(plotMarkers)));
        Strokes = LegendCollections.Freeze(strokes ?? Array.Empty<LegendStrokeCandidate>());
        Triangles = LegendCollections.Freeze(triangles ?? Array.Empty<LegendTriangleCandidate>());
        PeerPanels = LegendCollections.Freeze(peerPanels ?? Array.Empty<LegendPeerPanelEvidence>());
        Options = options ?? new LegendReasoningOptions();
        ContractVersion = contractVersion;
    }

    public string ProjectId { get; }

    public string PanelId { get; }

    public string InputSha256 { get; }

    public LegendRectangle PanelBounds { get; }

    public LegendRectangle PlotBounds { get; }

    public IReadOnlyList<LegendTextRegion> TextRegions { get; }

    public IReadOnlyList<LegendGlyphCandidate> Glyphs { get; }

    public IReadOnlyList<LegendSeriesCandidate> Series { get; }

    public IReadOnlyList<LegendPlotMarker> PlotMarkers { get; }

    public IReadOnlyList<LegendStrokeCandidate> Strokes { get; }

    public IReadOnlyList<LegendTriangleCandidate> Triangles { get; }

    public IReadOnlyList<LegendPeerPanelEvidence> PeerPanels { get; }

    public LegendReasoningOptions Options { get; }

    public int ContractVersion { get; }
}

public sealed record LegendReasoningTiming(
    double PreprocessMilliseconds,
    double InferenceMilliseconds,
    double PostprocessMilliseconds,
    double TotalMilliseconds);

public sealed record LegendModelReport(
    string? ModelId,
    string? Version,
    string? Sha256,
    string? Provider);

public sealed record LegendReasoningFailure(
    string Code,
    string Severity,
    string UserMessageKey,
    string TechnicalMessage,
    bool Recoverable,
    string SuggestedAction);

public sealed class LegendReasoningPayload
{
    public LegendReasoningPayload(
        IEnumerable<LegendRegion> regions,
        IEnumerable<LegendSeriesResolution> series,
        IEnumerable<LegendAnnotationCallout> callouts,
        IEnumerable<LegendArtifact> artifacts,
        IEnumerable<LegendParticipantMetadata> participants,
        IEnumerable<string> excludedArtifactMarkerIds)
    {
        Regions = LegendCollections.Freeze(regions ?? throw new ArgumentNullException(nameof(regions)));
        Series = LegendCollections.Freeze(series ?? throw new ArgumentNullException(nameof(series)));
        Callouts = LegendCollections.Freeze(callouts ?? throw new ArgumentNullException(nameof(callouts)));
        Artifacts = LegendCollections.Freeze(artifacts ?? throw new ArgumentNullException(nameof(artifacts)));
        Participants = LegendCollections.Freeze(participants ?? throw new ArgumentNullException(nameof(participants)));
        ExcludedArtifactMarkerIds = LegendCollections.Freeze(
            excludedArtifactMarkerIds ?? throw new ArgumentNullException(nameof(excludedArtifactMarkerIds)));
    }

    public IReadOnlyList<LegendRegion> Regions { get; }

    public IReadOnlyList<LegendSeriesResolution> Series { get; }

    public IReadOnlyList<LegendAnnotationCallout> Callouts { get; }

    public IReadOnlyList<LegendArtifact> Artifacts { get; }

    public IReadOnlyList<LegendParticipantMetadata> Participants { get; }

    public IReadOnlyList<string> ExcludedArtifactMarkerIds { get; }
}

public sealed class LegendReasoningResult
{
    public LegendReasoningResult(
        int contractVersion,
        string runId,
        string projectId,
        string panelId,
        string stage,
        string stageVersion,
        string inputSha256,
        string coordinateSpace,
        LegendReasoningPayload payload,
        LegendReasoningTiming timing,
        double confidence,
        IEnumerable<string> warnings,
        LegendModelReport? model,
        LegendReasoningFailure? failure)
    {
        ContractVersion = contractVersion;
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
        PanelId = panelId ?? throw new ArgumentNullException(nameof(panelId));
        Stage = stage ?? throw new ArgumentNullException(nameof(stage));
        StageVersion = stageVersion ?? throw new ArgumentNullException(nameof(stageVersion));
        InputSha256 = inputSha256 ?? throw new ArgumentNullException(nameof(inputSha256));
        CoordinateSpace = coordinateSpace ?? throw new ArgumentNullException(nameof(coordinateSpace));
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        Timing = timing ?? throw new ArgumentNullException(nameof(timing));
        Confidence = confidence;
        Warnings = LegendCollections.Freeze(warnings ?? throw new ArgumentNullException(nameof(warnings)));
        Model = model;
        Failure = failure;
    }

    public int ContractVersion { get; }

    public string RunId { get; }

    public string ProjectId { get; }

    public string PanelId { get; }

    public string Stage { get; }

    public string StageVersion { get; }

    public string InputSha256 { get; }

    public string CoordinateSpace { get; }

    public LegendReasoningPayload Payload { get; }

    public IReadOnlyList<LegendRegion> Regions => Payload.Regions;

    public IReadOnlyList<LegendSeriesResolution> Series => Payload.Series;

    public IReadOnlyList<LegendAnnotationCallout> Callouts => Payload.Callouts;

    public IReadOnlyList<LegendArtifact> Artifacts => Payload.Artifacts;

    public IReadOnlyList<LegendParticipantMetadata> Participants => Payload.Participants;

    public IReadOnlyList<string> ExcludedArtifactMarkerIds => Payload.ExcludedArtifactMarkerIds;

    public LegendReasoningTiming Timing { get; }

    public double Confidence { get; }

    public IReadOnlyList<string> Warnings { get; }

    public LegendModelReport? Model { get; }

    public LegendReasoningFailure? Failure { get; }

    public bool Succeeded => Failure is null;
}

public interface ILegendRegionResolver
{
    IReadOnlyList<LegendRegion> Resolve(
        LegendReasoningRequest request,
        CancellationToken cancellationToken);
}

public interface IAnnotationArrowResolver
{
    (IReadOnlyList<LegendAnnotationCallout> Callouts, IReadOnlyList<LegendArtifact> Artifacts) Resolve(
        LegendReasoningRequest request,
        CancellationToken cancellationToken);
}

public interface ILegendReasoningService
{
    Task<LegendReasoningResult> ResolveAsync(
        LegendReasoningRequest request,
        CancellationToken cancellationToken);
}

internal static class LegendCollections
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => new FrozenList<T>(values);

    private sealed class FrozenList<T> : IReadOnlyList<T>
    {
        private readonly T[] _items;

        public FrozenList(IEnumerable<T> values) => _items = values.ToArray();

        public int Count => _items.Length;

        public T this[int index] => _items[index];

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }
}
