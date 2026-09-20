// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;

namespace GraphReader.Export;

/// <summary>
/// Stable values shared by export callers and implementations.
/// </summary>
public static class ExportContract
{
    public const int Version = 1;
    public const string MinimalCsvHeader = "x_value,y_value,phase";
    public const string CoordinateSpace = "original_pixels";
    public const string DeterministicOrdering =
        "phase_order,x_value,observation_index,source_series_id,point_id";
}

/// <summary>
/// Selects the scientific meaning of the exported x value.
/// </summary>
public enum ExportMode
{
    PrintedSession,
    ObservationOrder,
}

[Flags]
public enum ExportAuditMode
{
    None = 0,
    ExtendedCsv = 1,
    Json = 2,
    ExtendedCsvAndJson = ExtendedCsv | Json,
}

public enum ExportOperation
{
    Preview,
    WriteFiles,
}

public enum ExportCalibrationStatus
{
    Missing,
    NeedsReview,
    Valid,
    InvalidSessionOrigin,
}

public enum InvalidSessionOriginBehavior
{
    Block,
    AllowWithExplicitOverride,
}

public enum ExportPhaseType
{
    Baseline,
    Intervention,
    Maintenance,
    Generalization,
    Unknown,
}

public enum ExportSeriesRole
{
    Baseline,
    Intervention,
    Maintenance,
    Generalization,
    Unknown,
}

public enum ExportXValueSource
{
    Printed,
    Estimated,
    ObservationOrder,
    Unknown,
}

public enum ExportReviewStatus
{
    Unreviewed,
    Accepted,
    Corrected,
    Rejected,
}

public enum ExportRowInclusion
{
    Intervention,
    SharedBaseline,
    ApplicableProbe,
}

public enum ExportAuditArtifactFormat
{
    Csv,
    Json,
}

public enum ExportFailureSeverity
{
    Warning,
    Error,
}

public readonly record struct ExportPixelPoint(double X, double Y);

/// <summary>
/// Calibration evidence used only for export validation. Export never mutates it.
/// </summary>
public sealed class ExportCalibration
{
    public ExportCalibration(
        ExportCalibrationStatus status,
        bool hasYCalibration,
        bool hasPrintedSessionCalibration,
        bool hasAbsoluteSessionOrigin,
        double? firstObservedSession,
        double confidence,
        IEnumerable<string>? reasons = null)
    {
        Status = status;
        HasYCalibration = hasYCalibration;
        HasPrintedSessionCalibration = hasPrintedSessionCalibration;
        HasAbsoluteSessionOrigin = hasAbsoluteSessionOrigin;
        FirstObservedSession = firstObservedSession;
        Confidence = confidence;
        Reasons = ExportCollections.Freeze(reasons ?? Array.Empty<string>());
    }

    public ExportCalibrationStatus Status { get; }

    public bool HasYCalibration { get; }

    public bool HasPrintedSessionCalibration { get; }

    public bool HasAbsoluteSessionOrigin { get; }

    public double? FirstObservedSession { get; }

    public double Confidence { get; }

    public IReadOnlyList<string> Reasons { get; }
}

/// <summary>
/// Records whether an invalid session origin must block final export.
/// An override is explicit only when the behavior, reason, and confirmation time are supplied.
/// </summary>
public sealed record ExportSessionOriginPolicy(
    bool RequireFirstObservedSessionOne,
    InvalidSessionOriginBehavior InvalidOriginBehavior,
    string? OverrideReason = null,
    DateTimeOffset? OverrideConfirmedAtUtc = null)
{
    public static ExportSessionOriginPolicy Default { get; } = new(
        RequireFirstObservedSessionOne: true,
        InvalidSessionOriginBehavior.Block);

    public bool HasExplicitOverride =>
        InvalidOriginBehavior == InvalidSessionOriginBehavior.AllowWithExplicitOverride &&
        !string.IsNullOrWhiteSpace(OverrideReason) &&
        OverrideConfirmedAtUtc.HasValue;
}

public sealed record ExportPhase(
    Guid PhaseId,
    int Order,
    string Code,
    ExportPhaseType NormalizedType,
    string? LabelText,
    double OriginalXMinimum,
    double OriginalXMaximum,
    double Confidence);

public sealed class ExportSeries
{
    public ExportSeries(
        Guid seriesId,
        string symbol,
        string displayName,
        ExportSeriesRole semanticRole,
        IEnumerable<Guid> pointIds,
        double confidence,
        string? legendText = null)
    {
        SeriesId = seriesId;
        Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        SemanticRole = semanticRole;
        PointIds = ExportCollections.Freeze(pointIds ?? throw new ArgumentNullException(nameof(pointIds)));
        Confidence = confidence;
        LegendText = legendText;
    }

    public Guid SeriesId { get; }

    /// <summary>
    /// The actual Unicode marker symbol. It is metadata and is never required in a filename.
    /// </summary>
    public string Symbol { get; }

    public string DisplayName { get; }

    public ExportSeriesRole SemanticRole { get; }

    public IReadOnlyList<Guid> PointIds { get; }

    public double Confidence { get; }

    public string? LegendText { get; }
}

/// <summary>
/// Defines the rows that belong in one intervention-specific export.
/// Point objects remain unique even when their rows are copied into multiple artifacts.
/// </summary>
public sealed class ExportSeriesRelation
{
    public ExportSeriesRelation(
        Guid interventionSeriesId,
        Guid? sharedBaselineSeriesId,
        IEnumerable<Guid>? applicableProbeSeriesIds = null)
    {
        InterventionSeriesId = interventionSeriesId;
        SharedBaselineSeriesId = sharedBaselineSeriesId;
        ApplicableProbeSeriesIds = ExportCollections.Freeze(
            applicableProbeSeriesIds ?? Array.Empty<Guid>());
    }

    public Guid InterventionSeriesId { get; }

    public Guid? SharedBaselineSeriesId { get; }

    public IReadOnlyList<Guid> ApplicableProbeSeriesIds { get; }
}

/// <summary>
/// Scientifically relevant point state at the immutable export boundary.
/// Unknown graph or session values remain null.
/// </summary>
public sealed record ExportPoint(
    Guid PointId,
    Guid? MarkerId,
    Guid? SeriesId,
    Guid? PhaseId,
    ExportPixelPoint OriginalPixel,
    double? GraphX,
    double? GraphY,
    int ObservationIndex,
    double? PrintedXValue,
    double? EstimatedXValue,
    ExportXValueSource XSource,
    double XConfidence,
    double YConfidence,
    double PointConfidence,
    ExportReviewStatus ReviewStatus,
    string SourceStage,
    string? ModelVersion);

/// <summary>
/// Complete immutable input snapshot for preview or final file generation.
/// </summary>
public sealed class ExportRequest
{
    public ExportRequest(
        Guid runId,
        Guid projectId,
        Guid panelId,
        string outputDirectory,
        string? participant,
        ExportMode mode,
        ExportAuditMode auditMode,
        ExportOperation operation,
        ExportCalibration calibration,
        ExportSessionOriginPolicy sessionOriginPolicy,
        IEnumerable<ExportPhase> phases,
        IEnumerable<ExportSeries> series,
        IEnumerable<ExportPoint> points,
        IEnumerable<ExportSeriesRelation> relations,
        IEnumerable<Guid>? selectedInterventionSeriesIds = null,
        int contractVersion = ExportContract.Version)
    {
        RunId = runId;
        ProjectId = projectId;
        PanelId = panelId;
        OutputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        Participant = participant;
        Mode = mode;
        AuditMode = auditMode;
        Operation = operation;
        Calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        SessionOriginPolicy = sessionOriginPolicy ?? throw new ArgumentNullException(nameof(sessionOriginPolicy));
        Phases = ExportCollections.Freeze(phases ?? throw new ArgumentNullException(nameof(phases)));
        Series = ExportCollections.Freeze(series ?? throw new ArgumentNullException(nameof(series)));
        Points = ExportCollections.Freeze(points ?? throw new ArgumentNullException(nameof(points)));
        Relations = ExportCollections.Freeze(relations ?? throw new ArgumentNullException(nameof(relations)));
        SelectedInterventionSeriesIds = ExportCollections.Freeze(
            selectedInterventionSeriesIds ?? Array.Empty<Guid>());
        ContractVersion = contractVersion;
    }

    public Guid RunId { get; }

    public Guid ProjectId { get; }

    public Guid PanelId { get; }

    public string OutputDirectory { get; }

    public string? Participant { get; }

    /// <summary>
    /// Optional batch filename scope. Does not change participant metadata or serialized contracts.
    /// </summary>
    public string? FileNamePrefix { get; init; }

    public ExportMode Mode { get; }

    public ExportAuditMode AuditMode { get; }

    public ExportOperation Operation { get; }

    public ExportCalibration Calibration { get; }

    public ExportSessionOriginPolicy SessionOriginPolicy { get; }

    public IReadOnlyList<ExportPhase> Phases { get; }

    public IReadOnlyList<ExportSeries> Series { get; }

    public IReadOnlyList<ExportPoint> Points { get; }

    public IReadOnlyList<ExportSeriesRelation> Relations { get; }

    /// <summary>
    /// Empty means every intervention series represented by a relation.
    /// </summary>
    public IReadOnlyList<Guid> SelectedInterventionSeriesIds { get; }

    public int ContractVersion { get; }
}

/// <summary>
/// The exact three-column row used by every minimal CSV artifact.
/// </summary>
public sealed record MinimalExportRow(double XValue, double YValue, string Phase);

/// <summary>
/// One audit row for one minimal row, retaining source and duplication provenance.
/// </summary>
public sealed record ExtendedAuditRow(
    double XValue,
    double YValue,
    string Phase,
    Guid PointId,
    Guid SourceSeriesId,
    Guid TargetInterventionSeriesId,
    Guid PhaseId,
    ExportPixelPoint OriginalPixel,
    ExportXValueSource XSource,
    double XConfidence,
    double YConfidence,
    double PointConfidence,
    ExportReviewStatus ReviewStatus,
    ExportRowInclusion Inclusion,
    ExportMode ExportMode,
    ExportCalibrationStatus CalibrationStatus,
    bool SessionOriginOverrideApplied,
    string? SessionOriginOverrideReason,
    DateTimeOffset? SessionOriginOverrideConfirmedAtUtc,
    string SeriesSymbol,
    string SeriesName,
    string SourceStage,
    string? ModelVersion);

public sealed class ExportPreviewFile
{
    public ExportPreviewFile(
        Guid interventionSeriesId,
        string seriesSymbol,
        string seriesName,
        string minimalFileName,
        IEnumerable<string> auditFileNames,
        IEnumerable<MinimalExportRow> rows)
    {
        InterventionSeriesId = interventionSeriesId;
        SeriesSymbol = seriesSymbol ?? throw new ArgumentNullException(nameof(seriesSymbol));
        SeriesName = seriesName ?? throw new ArgumentNullException(nameof(seriesName));
        MinimalFileName = minimalFileName ?? throw new ArgumentNullException(nameof(minimalFileName));
        AuditFileNames = ExportCollections.Freeze(
            auditFileNames ?? throw new ArgumentNullException(nameof(auditFileNames)));
        Rows = ExportCollections.Freeze(rows ?? throw new ArgumentNullException(nameof(rows)));
    }

    public Guid InterventionSeriesId { get; }

    public string SeriesSymbol { get; }

    public string SeriesName { get; }

    public string MinimalFileName { get; }

    public IReadOnlyList<string> AuditFileNames { get; }

    public IReadOnlyList<MinimalExportRow> Rows { get; }

    public int RowCount => Rows.Count;
}

public sealed class ExportPreview
{
    public ExportPreview(
        IEnumerable<ExportPreviewFile> files,
        bool finalExportBlocked,
        IEnumerable<string>? warnings = null)
    {
        Files = ExportCollections.Freeze(files ?? throw new ArgumentNullException(nameof(files)));
        FinalExportBlocked = finalExportBlocked;
        Warnings = ExportCollections.Freeze(warnings ?? Array.Empty<string>());
    }

    public IReadOnlyList<ExportPreviewFile> Files { get; }

    public bool FinalExportBlocked { get; }

    public IReadOnlyList<string> Warnings { get; }
}

public sealed class MinimalCsvArtifact
{
    public MinimalCsvArtifact(
        Guid interventionSeriesId,
        string seriesSymbol,
        string seriesName,
        string fileName,
        string content,
        string sha256,
        IEnumerable<MinimalExportRow> rows,
        string? writtenPath = null)
    {
        InterventionSeriesId = interventionSeriesId;
        SeriesSymbol = seriesSymbol ?? throw new ArgumentNullException(nameof(seriesSymbol));
        SeriesName = seriesName ?? throw new ArgumentNullException(nameof(seriesName));
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Sha256 = sha256 ?? throw new ArgumentNullException(nameof(sha256));
        Rows = ExportCollections.Freeze(rows ?? throw new ArgumentNullException(nameof(rows)));
        WrittenPath = writtenPath;
    }

    public Guid InterventionSeriesId { get; }

    public string SeriesSymbol { get; }

    public string SeriesName { get; }

    public string FileName { get; }

    public string Content { get; }

    public string Sha256 { get; }

    public IReadOnlyList<MinimalExportRow> Rows { get; }

    public string? WrittenPath { get; }
}

public sealed class ExtendedAuditArtifact
{
    public ExtendedAuditArtifact(
        Guid interventionSeriesId,
        ExportAuditArtifactFormat format,
        string fileName,
        string content,
        string sha256,
        IEnumerable<ExtendedAuditRow> rows,
        string? writtenPath = null)
    {
        InterventionSeriesId = interventionSeriesId;
        Format = format;
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Sha256 = sha256 ?? throw new ArgumentNullException(nameof(sha256));
        Rows = ExportCollections.Freeze(rows ?? throw new ArgumentNullException(nameof(rows)));
        WrittenPath = writtenPath;
    }

    public Guid InterventionSeriesId { get; }

    public ExportAuditArtifactFormat Format { get; }

    public string FileName { get; }

    public string Content { get; }

    public string Sha256 { get; }

    public IReadOnlyList<ExtendedAuditRow> Rows { get; }

    public string? WrittenPath { get; }
}

public sealed record ExportFailure(
    string Code,
    ExportFailureSeverity Severity,
    string UserMessageKey,
    string TechnicalMessage,
    bool Recoverable,
    string SuggestedAction,
    Guid? EntityId = null);

public sealed record ExportTiming(
    double ValidationMilliseconds,
    double PreparationMilliseconds,
    double SerializationMilliseconds,
    double WriteMilliseconds,
    double TotalMilliseconds);

/// <summary>
/// Describes stable ordering and hashes for byte-for-byte result comparison.
/// </summary>
public sealed record ExportDeterminism(
    string Ordering,
    string ArtifactSetSha256);

public sealed class ExportResult
{
    public ExportResult(
        Guid runId,
        Guid projectId,
        Guid panelId,
        ExportMode mode,
        ExportPreview preview,
        IEnumerable<MinimalCsvArtifact> minimalArtifacts,
        IEnumerable<ExtendedAuditArtifact> auditArtifacts,
        ExportDeterminism determinism,
        ExportTiming timing,
        IEnumerable<string>? warnings = null,
        IEnumerable<ExportFailure>? failures = null)
    {
        RunId = runId;
        ProjectId = projectId;
        PanelId = panelId;
        Mode = mode;
        Preview = preview ?? throw new ArgumentNullException(nameof(preview));
        MinimalArtifacts = ExportCollections.Freeze(
            minimalArtifacts ?? throw new ArgumentNullException(nameof(minimalArtifacts)));
        AuditArtifacts = ExportCollections.Freeze(
            auditArtifacts ?? throw new ArgumentNullException(nameof(auditArtifacts)));
        Determinism = determinism ?? throw new ArgumentNullException(nameof(determinism));
        Timing = timing ?? throw new ArgumentNullException(nameof(timing));
        Warnings = ExportCollections.Freeze(warnings ?? Array.Empty<string>());
        Failures = ExportCollections.Freeze(failures ?? Array.Empty<ExportFailure>());
    }

    public Guid RunId { get; }

    public Guid ProjectId { get; }

    public Guid PanelId { get; }

    public ExportMode Mode { get; }

    public ExportPreview Preview { get; }

    public IReadOnlyList<MinimalCsvArtifact> MinimalArtifacts { get; }

    public IReadOnlyList<ExtendedAuditArtifact> AuditArtifacts { get; }

    public ExportDeterminism Determinism { get; }

    public ExportTiming Timing { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<ExportFailure> Failures { get; }

    public bool Succeeded => Failures.All(static failure => failure.Severity != ExportFailureSeverity.Error);
}

public interface IExportService
{
    Task<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken);
}

internal static class ExportCollections
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());
}
