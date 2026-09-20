// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections;

namespace GraphReader.Phases;

public static class PhaseReasoningContract
{
    public const int Version = 1;
    public const string Stage = "phases";
    public const string StageVersion = "0.1.1";
    public const string CoordinateSpace = "original_pixels";
}

public enum PhaseDividerStyle
{
    Solid,
    Dashed,
    Dotted,
}

public enum PhaseSegmentKind
{
    Candidate,
    YAxis,
    PanelBorder,
    AnnotationStroke,
}

public enum PhaseNormalizedType
{
    Baseline,
    Intervention,
    Maintenance,
    Generalization,
    Unknown,
}

public enum PhaseEvidenceSource
{
    ProfilePrior,
    Ocr,
    Manual,
    CrossPanel,
}

public readonly record struct PhasePoint(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

public readonly record struct PhaseRectangle(double X, double Y, double Width, double Height)
{
    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public PhasePoint Center => new(X + (Width / 2), Y + (Height / 2));

    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height) &&
        Width > 0 && Height > 0;

    public bool Contains(PhasePoint point) =>
        point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;
}

public sealed record PhaseDividerSegment(
    string SegmentId,
    string PanelId,
    PhasePoint Start,
    PhasePoint End,
    double Thickness,
    PhaseDividerStyle Style,
    double Confidence,
    PhaseSegmentKind Kind = PhaseSegmentKind.Candidate);

public sealed record PhaseHeadingEvidence(
    string HeadingId,
    string PanelId,
    PhaseRectangle Bounds,
    string Text,
    double Confidence,
    bool Rejected = false);

public sealed record PhasePointEvidence(
    string PointId,
    string SeriesId,
    string PanelId,
    PhasePoint Center);

public sealed class PhaseSeriesEvidence
{
    public PhaseSeriesEvidence(
        string seriesId,
        PhaseNormalizedType semanticRole,
        IEnumerable<string> pointIds,
        IEnumerable<string>? applicableInterventionSeriesIds = null)
    {
        SeriesId = seriesId ?? throw new ArgumentNullException(nameof(seriesId));
        SemanticRole = semanticRole;
        PointIds = PhaseCollections.Freeze(pointIds ?? throw new ArgumentNullException(nameof(pointIds)));
        ApplicableInterventionSeriesIds = PhaseCollections.Freeze(
            applicableInterventionSeriesIds ?? Array.Empty<string>());
    }

    public string SeriesId { get; }

    public PhaseNormalizedType SemanticRole { get; }

    public IReadOnlyList<string> PointIds { get; }

    public IReadOnlyList<string> ApplicableInterventionSeriesIds { get; }
}

public sealed class PhasePanelEvidence
{
    public PhasePanelEvidence(
        string panelId,
        PhaseRectangle plotBounds,
        IEnumerable<PhaseDividerSegment> segments,
        IEnumerable<PhaseHeadingEvidence> headings,
        bool shareDividersWithTarget = false)
    {
        PanelId = panelId ?? throw new ArgumentNullException(nameof(panelId));
        PlotBounds = plotBounds;
        Segments = PhaseCollections.Freeze(segments ?? throw new ArgumentNullException(nameof(segments)));
        Headings = PhaseCollections.Freeze(headings ?? throw new ArgumentNullException(nameof(headings)));
        ShareDividersWithTarget = shareDividersWithTarget;
    }

    public string PanelId { get; }

    public PhaseRectangle PlotBounds { get; }

    public IReadOnlyList<PhaseDividerSegment> Segments { get; }

    public IReadOnlyList<PhaseHeadingEvidence> Headings { get; }

    public bool ShareDividersWithTarget { get; }
}

public sealed record PhaseManualDivider(
    string DividerId,
    double OriginalX,
    PhaseDividerStyle Style,
    double Confidence = 1,
    double? ReplacedAutomaticOriginalX = null);

public sealed record PhaseDeletedDivider(
    string DividerId,
    double? ReplacedAutomaticOriginalX = null);

public sealed record PhaseLabelOverride(
    string PhaseId,
    string Code,
    PhaseNormalizedType NormalizedType,
    string LabelText,
    double? OriginalXMinimum = null,
    double? OriginalXMaximum = null);

public sealed class PhaseManualOverrides
{
    public PhaseManualOverrides(
        IEnumerable<PhaseManualDivider>? dividers = null,
        IEnumerable<string>? deletedDividerIds = null,
        IEnumerable<PhaseLabelOverride>? labels = null,
        IEnumerable<PhaseDeletedDivider>? deletedDividers = null)
    {
        Dividers = PhaseCollections.Freeze((dividers ?? Array.Empty<PhaseManualDivider>())
            .Select(static divider => divider with { DividerId = CanonicalUuid(divider.DividerId) }));
        DeletedDividers = PhaseCollections.Freeze(
            (deletedDividerIds ?? Array.Empty<string>())
                .Select(static id => new PhaseDeletedDivider(CanonicalUuid(id)))
                .Concat((deletedDividers ?? Array.Empty<PhaseDeletedDivider>())
                    .Select(static divider => divider with { DividerId = CanonicalUuid(divider.DividerId) })));
        DeletedDividerIds = PhaseCollections.Freeze(DeletedDividers.Select(static divider => divider.DividerId));
        Labels = PhaseCollections.Freeze((labels ?? Array.Empty<PhaseLabelOverride>())
            .Select(static label => label with { PhaseId = CanonicalUuid(label.PhaseId) }));
    }

    public IReadOnlyList<PhaseManualDivider> Dividers { get; }

    public IReadOnlyList<string> DeletedDividerIds { get; }

    public IReadOnlyList<PhaseDeletedDivider> DeletedDividers { get; }

    public IReadOnlyList<PhaseLabelOverride> Labels { get; }

    private static string CanonicalUuid(string value) =>
        Guid.TryParse(value, out Guid parsed) ? parsed.ToString("D") : value;
}

public sealed record PhaseReasoningOptions
{
    public double MaximumVerticalDriftPixels { get; init; } = 2;

    public double DividerClusterTolerancePixels { get; init; } = 3;

    public double CrossPanelAlignmentTolerancePixels { get; init; } = 3;

    public double MinimumVerticalCoverageFraction { get; init; } = 0.45;

    public double BorderExclusionPixels { get; init; } = 5;

    public double MaximumHeadingDistancePixels { get; init; } = 80;

    public double MinimumConfidence { get; init; } = 0.60;

    public string StageVersion { get; init; } = PhaseReasoningContract.StageVersion;
}

public sealed class PhaseReasoningRequest
{
    public PhaseReasoningRequest(
        string projectId,
        string panelId,
        string inputSha256,
        PhaseRectangle plotBounds,
        IEnumerable<PhaseDividerSegment> segments,
        IEnumerable<PhaseHeadingEvidence> headings,
        IEnumerable<PhasePointEvidence> points,
        IEnumerable<PhaseSeriesEvidence> series,
        IEnumerable<PhasePanelEvidence>? alignedPanels = null,
        PhaseManualOverrides? manualOverrides = null,
        PhaseReasoningOptions? options = null,
        int contractVersion = PhaseReasoningContract.Version)
    {
        ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
        PanelId = panelId ?? throw new ArgumentNullException(nameof(panelId));
        InputSha256 = inputSha256 ?? throw new ArgumentNullException(nameof(inputSha256));
        PlotBounds = plotBounds;
        Segments = PhaseCollections.Freeze(segments ?? throw new ArgumentNullException(nameof(segments)));
        Headings = PhaseCollections.Freeze(headings ?? throw new ArgumentNullException(nameof(headings)));
        Points = PhaseCollections.Freeze(points ?? throw new ArgumentNullException(nameof(points)));
        Series = PhaseCollections.Freeze(series ?? throw new ArgumentNullException(nameof(series)));
        AlignedPanels = PhaseCollections.Freeze(alignedPanels ?? Array.Empty<PhasePanelEvidence>());
        ManualOverrides = manualOverrides ?? new PhaseManualOverrides();
        Options = options ?? new PhaseReasoningOptions();
        ContractVersion = contractVersion;
    }

    public string ProjectId { get; }

    public string PanelId { get; }

    public string InputSha256 { get; }

    public PhaseRectangle PlotBounds { get; }

    public IReadOnlyList<PhaseDividerSegment> Segments { get; }

    public IReadOnlyList<PhaseHeadingEvidence> Headings { get; }

    public IReadOnlyList<PhasePointEvidence> Points { get; }

    public IReadOnlyList<PhaseSeriesEvidence> Series { get; }

    public IReadOnlyList<PhasePanelEvidence> AlignedPanels { get; }

    public PhaseManualOverrides ManualOverrides { get; }

    public PhaseReasoningOptions Options { get; }

    public int ContractVersion { get; }
}

public sealed class PhaseDivider
{
    public PhaseDivider(
        string dividerId,
        double originalX,
        PhaseDividerStyle style,
        IEnumerable<string> segmentIds,
        IEnumerable<string> sourcePanelIds,
        double confidence,
        PhaseEvidenceSource source)
    {
        DividerId = dividerId ?? throw new ArgumentNullException(nameof(dividerId));
        OriginalX = originalX;
        Style = style;
        SegmentIds = PhaseCollections.Freeze(segmentIds ?? throw new ArgumentNullException(nameof(segmentIds)));
        SourcePanelIds = PhaseCollections.Freeze(
            sourcePanelIds ?? throw new ArgumentNullException(nameof(sourcePanelIds)));
        Confidence = confidence;
        Source = source;
    }

    public string DividerId { get; }

    public double OriginalX { get; }

    public PhaseDividerStyle Style { get; }

    public IReadOnlyList<string> SegmentIds { get; }

    public IReadOnlyList<string> SourcePanelIds { get; }

    public double Confidence { get; }

    public PhaseEvidenceSource Source { get; }
}

public sealed record PhaseRegion(
    string PhaseId,
    int Order,
    string Code,
    PhaseNormalizedType NormalizedType,
    string LabelText,
    double OriginalXMinimum,
    double OriginalXMaximum,
    string? BoundaryLeftId,
    string? BoundaryRightId,
    double Confidence,
    PhaseEvidenceSource Source);

public sealed record PhasePointAssignment(
    string PointId,
    string PhaseId,
    double OriginalX);

public sealed class PhaseSeriesRelation
{
    public PhaseSeriesRelation(
        string interventionSeriesId,
        string? sharedBaselineSeriesId,
        IEnumerable<string> applicableProbeSeriesIds)
    {
        InterventionSeriesId = interventionSeriesId ?? throw new ArgumentNullException(nameof(interventionSeriesId));
        SharedBaselineSeriesId = sharedBaselineSeriesId;
        ApplicableProbeSeriesIds = PhaseCollections.Freeze(
            applicableProbeSeriesIds ?? throw new ArgumentNullException(nameof(applicableProbeSeriesIds)));
    }

    public string InterventionSeriesId { get; }

    public string? SharedBaselineSeriesId { get; }

    public IReadOnlyList<string> ApplicableProbeSeriesIds { get; }
}

public sealed class PhaseReasoningPayload
{
    public PhaseReasoningPayload(
        IEnumerable<PhaseDivider> dividers,
        IEnumerable<PhaseRegion> phases,
        IEnumerable<PhasePointAssignment> assignments,
        IEnumerable<PhaseSeriesRelation> seriesRelations,
        PhaseManualOverrides manualOverrides)
    {
        Dividers = PhaseCollections.Freeze(dividers ?? throw new ArgumentNullException(nameof(dividers)));
        Phases = PhaseCollections.Freeze(phases ?? throw new ArgumentNullException(nameof(phases)));
        Assignments = PhaseCollections.Freeze(assignments ?? throw new ArgumentNullException(nameof(assignments)));
        SeriesRelations = PhaseCollections.Freeze(
            seriesRelations ?? throw new ArgumentNullException(nameof(seriesRelations)));
        ManualOverrides = manualOverrides ?? throw new ArgumentNullException(nameof(manualOverrides));
    }

    public IReadOnlyList<PhaseDivider> Dividers { get; }

    public IReadOnlyList<PhaseRegion> Phases { get; }

    public IReadOnlyList<PhasePointAssignment> Assignments { get; }

    public IReadOnlyList<PhaseSeriesRelation> SeriesRelations { get; }

    public PhaseManualOverrides ManualOverrides { get; }
}

public sealed record PhaseReasoningTiming(
    double PreprocessMilliseconds,
    double InferenceMilliseconds,
    double PostprocessMilliseconds,
    double TotalMilliseconds);

public sealed record PhaseReasoningFailure(
    string Code,
    string Severity,
    string UserMessageKey,
    string TechnicalMessage,
    bool Recoverable,
    string SuggestedAction);

public sealed class PhaseReasoningResult
{
    public PhaseReasoningResult(
        int contractVersion,
        string runId,
        string projectId,
        string panelId,
        string stage,
        string stageVersion,
        string inputSha256,
        string coordinateSpace,
        PhaseReasoningPayload payload,
        PhaseReasoningTiming timing,
        double confidence,
        IEnumerable<string> warnings,
        PhaseReasoningFailure? failure)
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
        Warnings = PhaseCollections.Freeze(warnings ?? throw new ArgumentNullException(nameof(warnings)));
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

    public PhaseReasoningPayload Payload { get; }

    public PhaseReasoningTiming Timing { get; }

    public double Confidence { get; }

    public IReadOnlyList<string> Warnings { get; }

    public PhaseReasoningFailure? Failure { get; }

    public bool Succeeded => Failure is null;
}

public interface IPhaseDividerDetector
{
    IReadOnlyList<PhaseDivider> Detect(
        PhaseReasoningRequest request,
        CancellationToken cancellationToken);
}

public interface IPhaseReasoningService
{
    Task<PhaseReasoningResult> ResolveAsync(
        PhaseReasoningRequest request,
        CancellationToken cancellationToken);
}

public abstract record PhaseEditCommand(string CommandId);

public sealed record MovePhaseDividerCommand(
    string CommandId,
    string DividerId,
    double OriginalX,
    PhaseDividerStyle Style,
    double PreviousOriginalX) : PhaseEditCommand(CommandId);

public sealed record AddPhaseDividerCommand(
    string CommandId,
    string DividerId,
    double OriginalX,
    PhaseDividerStyle Style) : PhaseEditCommand(CommandId);

public sealed record DeletePhaseDividerCommand(
    string CommandId,
    string DividerId,
    double PreviousOriginalX) : PhaseEditCommand(CommandId);

public sealed record RelabelPhaseCommand(
    string CommandId,
    string PhaseId,
    string Code,
    PhaseNormalizedType NormalizedType,
    string LabelText,
    double PreviousOriginalXMinimum,
    double PreviousOriginalXMaximum) : PhaseEditCommand(CommandId);

public sealed record PhaseEditAudit(
    string CommandId,
    string Action,
    string TargetId);

public sealed record PhaseEditResult(
    PhaseManualOverrides Overrides,
    PhaseEditAudit? Audit,
    PhaseReasoningFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public interface IPhaseManualEditor
{
    PhaseEditResult Apply(
        PhaseManualOverrides current,
        PhaseEditCommand command,
        PhaseRectangle plotBounds,
        CancellationToken cancellationToken);
}

internal static class PhaseCollections
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
