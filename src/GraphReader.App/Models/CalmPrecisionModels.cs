// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.ViewModels;

namespace GraphReader.App.Models;

public enum WorkspaceSurfaceState
{
    Empty,
    Ready,
    Analyzing,
    Reviewing,
    ExportPreview,
}

public enum ReviewIssueKind
{
    Calibration,
    Point,
    Series,
    Phase,
    PipelineWarning,
}

public enum ReviewIssueSeverity
{
    Information,
    Warning,
    Blocking,
}

public enum ExportDestinationStatus
{
    PendingSelection,
    Written,
}

public sealed record WorkspaceOperationStatus(
    string Code,
    ReviewIssueSeverity Severity,
    string UserMessageKey,
    string TechnicalMessage,
    bool Recoverable,
    string SuggestedAction);

public sealed record PipelineWarningPresentation(
    string UserMessageKey,
    string TechnicalMessage);

public sealed record ReviewIssueViewModel(
    string IssueId,
    string TabId,
    string? EntityId,
    ReviewIssueKind Kind,
    ReviewIssueSeverity Severity,
    string TitleKey,
    string InterpretationKey,
    string RecommendedActionKey,
    string Title,
    string Interpretation,
    string RecommendedAction,
    string? TechnicalMessage = null)
{
    public bool IsBlocking => Severity == ReviewIssueSeverity.Blocking;
}

public sealed record FilteredMarkerCandidateViewModel(
    string MarkerId,
    string Symbol,
    double PixelX,
    double PixelY,
    double ArtifactProbability);

public sealed class DataPreviewRowViewModel : ObservableObject
{
    private bool _isSelected;

    public DataPreviewRowViewModel(
        string pointId,
        int observationIndex,
        double? printedXValue,
        double? estimatedXValue,
        double? graphX,
        double? graphY,
        string phaseCode,
        string seriesId,
        string seriesLabel,
        double pixelX,
        double pixelY)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(phaseCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesLabel);
        PointId = pointId;
        ObservationIndex = observationIndex;
        PrintedXValue = printedXValue;
        EstimatedXValue = estimatedXValue;
        GraphX = graphX;
        GraphY = graphY;
        PhaseCode = phaseCode;
        SeriesId = seriesId;
        SeriesLabel = seriesLabel;
        PixelX = pixelX;
        PixelY = pixelY;
    }

    public string PointId { get; }

    public int ObservationIndex { get; }

    public double? PrintedXValue { get; }

    public double? EstimatedXValue { get; }

    public double? GraphX { get; }

    public double? GraphY { get; }

    public string PhaseCode { get; }

    public string SeriesId { get; }

    public string SeriesLabel { get; }

    public double PixelX { get; }

    public double PixelY { get; }

    public string XValueDisplay => PrintedXValue?.ToString("G", System.Globalization.CultureInfo.CurrentCulture)
        ?? EstimatedXValue?.ToString("G", System.Globalization.CultureInfo.CurrentCulture)
        ?? GraphX?.ToString("G", System.Globalization.CultureInfo.CurrentCulture)
        ?? ObservationIndex.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}

public sealed class ExportSummaryViewModel : ObservableObject
{
    private bool _warningsAcknowledged;

    public ExportSummaryViewModel(
        int pointCount,
        int seriesCount,
        int phaseCount,
        int blockingIssueCount,
        int warningCount,
        string? outputDirectory,
        IReadOnlyList<string> outputFileNames,
        string? provenanceSummary = null,
        string? destinationPendingText = null,
        IReadOnlyList<ReviewIssueViewModel>? blockingIssues = null,
        IReadOnlyList<ReviewIssueViewModel>? warningIssues = null,
        ExportDestinationStatus destinationStatus = ExportDestinationStatus.PendingSelection)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pointCount);
        ArgumentOutOfRangeException.ThrowIfNegative(seriesCount);
        ArgumentOutOfRangeException.ThrowIfNegative(phaseCount);
        ArgumentOutOfRangeException.ThrowIfNegative(blockingIssueCount);
        ArgumentOutOfRangeException.ThrowIfNegative(warningCount);
        PointCount = pointCount;
        SeriesCount = seriesCount;
        PhaseCount = phaseCount;
        BlockingIssueCount = blockingIssueCount;
        WarningCount = warningCount;
        OutputDirectory = outputDirectory;
        OutputDirectoryDisplay = outputDirectory ?? destinationPendingText ?? string.Empty;
        OutputFileNames = outputFileNames ?? throw new ArgumentNullException(nameof(outputFileNames));
        ProvenanceSummary = provenanceSummary ?? string.Empty;
        BlockingIssues = blockingIssues ?? Array.Empty<ReviewIssueViewModel>();
        WarningIssues = warningIssues ?? Array.Empty<ReviewIssueViewModel>();
        DestinationStatus = destinationStatus;
    }

    public int PointCount { get; }

    public int SeriesCount { get; }

    public int PhaseCount { get; }

    public int BlockingIssueCount { get; }

    public int WarningCount { get; }

    public bool RequiresWarningAcknowledgement => WarningCount > 0;

    public bool WarningsAcknowledged
    {
        get => _warningsAcknowledged;
        set
        {
            if (SetProperty(ref _warningsAcknowledged, value))
            {
                OnPropertyChanged(nameof(CanExport));
                OnPropertyChanged(nameof(AcknowledgedWarningIssues));
            }
        }
    }

    public bool CanExport => BlockingIssueCount == 0 && OutputFileNames.Count > 0 &&
        (!RequiresWarningAcknowledgement || WarningsAcknowledged);

    public string? OutputDirectory { get; }

    public string OutputDirectoryDisplay { get; }

    public IReadOnlyList<string> OutputFileNames { get; }

    public string ProvenanceSummary { get; }

    public IReadOnlyList<ReviewIssueViewModel> BlockingIssues { get; }

    public IReadOnlyList<ReviewIssueViewModel> WarningIssues { get; }

    public IReadOnlyList<ReviewIssueViewModel> AcknowledgedWarningIssues =>
        WarningsAcknowledged ? WarningIssues : Array.Empty<ReviewIssueViewModel>();

    public ExportDestinationStatus DestinationStatus { get; }

    public bool IsDestinationPending => DestinationStatus == ExportDestinationStatus.PendingSelection;
}

public sealed class TabEditHistory : ObservableObject
{
    private readonly Stack<ManualEditEntry> _undo = [];
    private readonly Stack<ManualEditEntry> _redo = [];

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoDescription => _undo.TryPeek(out ManualEditEntry? entry) ? entry.Description : null;

    public string? RedoDescription => _redo.TryPeek(out ManualEditEntry? entry) ? entry.Description : null;

    public void Record(string description, Action undo, Action redo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        _undo.Push(new ManualEditEntry(description, undo, redo));
        _redo.Clear();
        NotifyStateChanged();
    }

    public void Undo()
    {
        if (!_undo.TryPop(out ManualEditEntry? entry))
        {
            return;
        }

        entry.Undo();
        _redo.Push(entry);
        NotifyStateChanged();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out ManualEditEntry? entry))
        {
            return;
        }

        entry.Redo();
        _undo.Push(entry);
        NotifyStateChanged();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
    }

    private sealed record ManualEditEntry(string Description, Action Undo, Action Redo);
}
