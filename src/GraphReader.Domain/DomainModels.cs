// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;

namespace GraphReader.Domain;

public interface IStableId<TSelf>
    where TSelf : struct, IStableId<TSelf>
{
    Guid Value { get; }

    static abstract TSelf FromGuid(Guid value);
}

public readonly record struct ProjectId(Guid Value) : IStableId<ProjectId>
{
    public static ProjectId New() => new(Guid.NewGuid());

    public static ProjectId FromGuid(Guid value) => new(value);
}

public readonly record struct SourceId(Guid Value) : IStableId<SourceId>
{
    public static SourceId New() => new(Guid.NewGuid());

    public static SourceId FromGuid(Guid value) => new(value);
}

public readonly record struct PanelId(Guid Value) : IStableId<PanelId>
{
    public static PanelId New() => new(Guid.NewGuid());

    public static PanelId FromGuid(Guid value) => new(value);
}

public readonly record struct TransformId(Guid Value) : IStableId<TransformId>
{
    public static TransformId New() => new(Guid.NewGuid());

    public static TransformId FromGuid(Guid value) => new(value);
}

public readonly record struct CalibrationId(Guid Value) : IStableId<CalibrationId>
{
    public static CalibrationId New() => new(Guid.NewGuid());

    public static CalibrationId FromGuid(Guid value) => new(value);
}

public readonly record struct OcrRegionId(Guid Value) : IStableId<OcrRegionId>
{
    public static OcrRegionId New() => new(Guid.NewGuid());

    public static OcrRegionId FromGuid(Guid value) => new(value);
}

public readonly record struct MarkerId(Guid Value) : IStableId<MarkerId>
{
    public static MarkerId New() => new(Guid.NewGuid());

    public static MarkerId FromGuid(Guid value) => new(value);
}

public readonly record struct SeriesId(Guid Value) : IStableId<SeriesId>
{
    public static SeriesId New() => new(Guid.NewGuid());

    public static SeriesId FromGuid(Guid value) => new(value);
}

public readonly record struct PointId(Guid Value) : IStableId<PointId>
{
    public static PointId New() => new(Guid.NewGuid());

    public static PointId FromGuid(Guid value) => new(value);
}

public readonly record struct PhaseId(Guid Value) : IStableId<PhaseId>
{
    public static PhaseId New() => new(Guid.NewGuid());

    public static PhaseId FromGuid(Guid value) => new(value);
}

public readonly record struct AuditEventId(Guid Value) : IStableId<AuditEventId>
{
    public static AuditEventId New() => new(Guid.NewGuid());

    public static AuditEventId FromGuid(Guid value) => new(value);
}

public enum SourceKind
{
    Image,
    Pdf
}

public enum TransformKind
{
    Crop,
    Scale,
    Affine,
    Perspective,
    Rotation
}

public enum CoordinateSpace
{
    OriginalPixels,
    PagePixels,
    PanelPixels,
    DeskewedPixels,
    EnhancedPixels,
    ModelTensor,
    GraphUnits
}

public enum CalibrationStatus
{
    NeedsReview,
    Valid,
    InvalidSessionOrigin
}

public enum CalibrationAnchorKind
{
    Session1Y0,
    Session1Ymax,
    SessionmaxY0,
    OcrTick,
    Manual,
    FirstVisibleSessionY0,
    FirstVisibleSessionYmax
}

public enum OcrRole
{
    YTick,
    XTick,
    AxisTitle,
    PhaseHeading,
    LegendText,
    Participant,
    Annotation,
    Other
}

public enum SourceImageKind
{
    Original,
    Enhanced,
    Consensus
}

public enum ReviewStatus
{
    Unreviewed,
    Accepted,
    Corrected,
    Rejected
}

public enum MarkerShape
{
    Circle,
    Square,
    TriangleUp,
    TriangleDown,
    Diamond,
    Star,
    Asterisk,
    Cross,
    Other
}

public enum MarkerFill
{
    Filled,
    Open,
    Unknown
}

public enum SemanticRole
{
    Baseline,
    Intervention,
    Maintenance,
    Generalization,
    Unknown
}

public enum PhaseNormalizedType
{
    Baseline,
    Intervention,
    Maintenance,
    Generalization,
    Unknown
}

public enum PhaseSource
{
    ProfilePrior,
    Ocr,
    Manual,
    CrossPanel
}

public enum PointXSource
{
    Printed,
    Estimated,
    ObservationOrder,
    Unknown
}

public enum DomainEventKind
{
    CalibrationChanged,
    DetectionAccepted,
    PointEdited,
    PhaseEdited,
    ExportSettingsChanged,
    TimerAutosave
}

public sealed record ProjectDocument(
    int SchemaVersion,
    ProjectId ProjectId,
    string AppVersion,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    ProjectSettings Settings,
    IReadOnlyList<SourceReference> Sources,
    IReadOnlyList<PanelRecord> Panels,
    AuditTrail Audit)
{
    public const int CurrentSchemaVersion = 1;

    public static ProjectDocument Create(string appVersion, DateTimeOffset nowUtc) =>
        new(
            CurrentSchemaVersion,
            ProjectId.New(),
            appVersion,
            nowUtc.ToUniversalTime(),
            nowUtc.ToUniversalTime(),
            ProjectSettings.Default,
            Array.Empty<SourceReference>(),
            Array.Empty<PanelRecord>(),
            AuditTrail.Empty);
}

public sealed record ProjectSettings(
    bool RequireFirstSessionOne,
    int DefaultEnhancementScale,
    bool PhaseOverlayVisible,
    AppearanceMode Appearance,
    string Locale)
{
    public static ProjectSettings Default { get; } = new(true, 2, true, AppearanceMode.System, "en-US");
}

public enum AppearanceMode
{
    System,
    Light,
    Dark
}

public sealed record SourceReference(
    SourceId SourceId,
    SourceKind Kind,
    string DisplayName,
    string? LocalPath,
    string Sha256,
    JsonElement? ArticleMetadata);

public sealed record PanelRecord(
    PanelId PanelId,
    SourceId SourceId,
    int? PageNumber,
    string DisplayName,
    string? Participant,
    CropRectangle Crop,
    IReadOnlyList<TransformRecord> Transforms,
    JsonElement? Enhancement,
    CalibrationRecord? Calibration,
    IReadOnlyList<OcrEvidence> OcrRegions,
    IReadOnlyList<MarkerRecord> Markers,
    IReadOnlyList<SeriesRecord> Series,
    IReadOnlyList<PointRecord> Points,
    IReadOnlyList<PhaseRecord> Phases,
    ExportSettingsRecord? ExportSettings,
    JsonElement? Validation);

public sealed record CropRectangle(double X, double Y, double Width, double Height);

public sealed record TransformRecord(
    TransformId TransformId,
    TransformKind Kind,
    CoordinateSpace SourceSpace,
    CoordinateSpace TargetSpace,
    IReadOnlyList<double> Matrix3x3,
    IReadOnlyList<double>? InverseMatrix3x3,
    JsonElement Parameters,
    bool Lossy);

public sealed record CalibrationRecord(
    CalibrationId CalibrationId,
    CalibrationStatus Status,
    IReadOnlyList<CalibrationAnchor> Anchors,
    SessionLatticeRecord? SessionLattice,
    bool UserConfirmed,
    double Confidence,
    IReadOnlyList<string> Reasons);

public sealed record CalibrationAnchor(
    CalibrationAnchorKind Kind,
    PixelPoint Screen,
    GraphPoint Graph,
    double Confidence,
    OcrRegionId? EvidenceRegionId);

public sealed record SessionLatticeRecord(
    double Session1PixelX,
    double PitchPixels,
    double? PrintedMin,
    double? PrintedMax,
    double Confidence,
    string Source);

public sealed record PixelPoint(double X, double Y);

public sealed record GraphPoint(double X, double Y);

public sealed record OcrEvidence(
    OcrRegionId RegionId,
    IReadOnlyList<PixelPoint> Polygon,
    string Text,
    IReadOnlyList<OcrAlternative> Alternatives,
    OcrRole Role,
    double Confidence,
    SourceImageKind SourceImage,
    ReviewStatus ReviewStatus);

public sealed record OcrAlternative(string Text, double Confidence);

public sealed record MarkerRecord(
    MarkerId MarkerId,
    PixelPoint Center,
    double Radius,
    MarkerShape Shape,
    MarkerFill Fill,
    string Symbol,
    double ArtifactProbability,
    double CenterConfidence,
    double ShapeConfidence,
    double FillConfidence,
    IReadOnlyList<double>? Embedding,
    SeriesId? CandidateSeriesId,
    SourceImageKind SourceImage,
    ReviewStatus ReviewStatus);

public sealed record SeriesRecord(
    SeriesId SeriesId,
    string Symbol,
    MarkerShape Shape,
    MarkerFill Fill,
    string DisplayName,
    SemanticRole SemanticRole,
    string? LegendText,
    IReadOnlyList<PointId> PointIds,
    double Confidence,
    SeriesId? SharedBaselineSeriesId,
    IReadOnlyList<SeriesId> ApplicableProbeSeriesIds,
    bool UserConfirmedName);

public sealed record PointRecord(
    PointId PointId,
    MarkerId? MarkerId,
    SeriesId? SeriesId,
    PhaseId? PhaseId,
    PixelPoint OriginalPixel,
    double? GraphX,
    double? GraphY,
    int ObservationIndex,
    double? PrintedXValue,
    double? EstimatedXValue,
    PointXSource XSource,
    double XConfidence,
    double YConfidence,
    double PointConfidence,
    string SourceStage,
    string? ModelVersion,
    ReviewStatus ReviewStatus,
    IReadOnlyList<PointModification> ModificationHistory);

public sealed record PointModification(
    AuditEventId EventId,
    DateTimeOffset OccurredUtc,
    PixelPoint? PreviousPixel,
    GraphPoint? PreviousGraph,
    string Reason);

public sealed record PhaseRecord(
    PhaseId PhaseId,
    int Order,
    string Code,
    PhaseNormalizedType NormalizedType,
    string? LabelText,
    double ScreenXMin,
    double ScreenXMax,
    PhaseId? BoundaryLeftId,
    PhaseId? BoundaryRightId,
    double Confidence,
    PhaseSource Source,
    bool UserConfirmed);

public sealed record ExportSettingsRecord(
    string XValueMode,
    bool IncludeAuditSidecar,
    IReadOnlyList<SeriesId> SelectedSeriesIds);

public sealed record AuditTrail(
    IReadOnlyList<AuditEvent> Events,
    DateTimeOffset? LastAutosaveUtc)
{
    public static AuditTrail Empty { get; } = new(Array.Empty<AuditEvent>(), null);
}

public sealed record AuditEvent(
    AuditEventId EventId,
    DateTimeOffset OccurredUtc,
    DomainEventKind Kind,
    PanelId? PanelId,
    string? EntityId,
    string? Note,
    JsonElement? Details);
