// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.App.ViewModels;
using GraphReader.Axis;

namespace GraphReader.App.Services;

public sealed record WorkspaceAxisSuggestion(string TabId, string Binding, RustAxisGeometry Geometry)
{
    internal object? OriginalImageIdentity { get; init; }
    public bool IsUserDefined { get; init; }
    public string Provider { get; init; } = "rust";
    public IReadOnlyList<string> ReviewReasons { get; init; } = Array.Empty<string>();
}

public interface IManualAxisDraftWorkspace
{
    bool CanCreateManualAxisDraft { get; }
    WorkspaceAxisSuggestion? CreateManualAxisDraft(string tabId, IReadOnlyList<PixelPoint> anchors);
}

public interface IAxisSuggestionWorkspace
{
    bool CanSuggestAxes { get; }
    Task<WorkspaceAxisSuggestion?> SuggestAxesAsync(string tabId, CancellationToken cancellationToken);
    bool IsAxisSuggestionCurrent(WorkspaceAxisSuggestion suggestion);
}

public sealed record WorkspaceCalibrationLabelSuggestion(WorkspaceAxisSuggestion AxisSuggestion, CalibrationOcrResponse Response)
{
    internal RustAxisGeometry FitGeometry { get; init; } = AxisSuggestion.Geometry;
    internal object? TickEvidenceIdentity { get; init; }
    internal bool TickMeasurementAttempted { get; init; }
    internal string? TickMeasurementErrorCode { get; init; }
}

public interface ICalibrationLabelSuggestionWorkspace
{
    bool CanReadCalibrationLabels { get; }
    Task<WorkspaceCalibrationLabelSuggestion?> ReadCalibrationLabelsAsync(WorkspaceAxisSuggestion suggestion, CancellationToken cancellationToken);
    bool IsCalibrationLabelSuggestionCurrent(WorkspaceCalibrationLabelSuggestion result);
}

public partial class ManualPreviewWorkspaceService : IManualAxisDraftWorkspace
{
    private readonly IAxisProposalService? _axisProposalService;
    private readonly IAxisTickMeasurementService? _axisTickMeasurementService;
    private readonly ICalibrationOcrSuggestionService? _calibrationOcrSuggestionService;
    private sealed record CalibrationTickEvidenceIdentity(WorkspaceAxisSuggestion Suggestion,
        object? OriginalImageIdentity, string Binding, string ImageSha256, RustAxisGeometry Geometry);
    private int _calibrationOcrDisposed;
    public bool CanSuggestAxes => _axisProposalService is not null &&
        RuntimeEnvironment == GraphReader.App.Integration.Workflow.WorkflowRuntimeEnvironment.ManualPreview;

    public bool CanCreateManualAxisDraft =>
        RuntimeEnvironment == GraphReader.App.Integration.Workflow.WorkflowRuntimeEnvironment.ManualPreview;

    public WorkspaceAxisSuggestion? CreateManualAxisDraft(string tabId, IReadOnlyList<PixelPoint> anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        WorkspaceTabViewModel? tab = _tabs.SingleOrDefault(item => item.TabId == tabId);
        if (!CanCreateManualAxisDraft || tab is null || anchors.Count != 3 ||
            !_originalPanelBytesByTab.TryGetValue(tabId, out var image)) return null;
        PixelPoint[] points = anchors.ToArray();
        if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X < 0 || point.Y < 0 || point.X > tab.PixelWidth - 1 || point.Y > tab.PixelHeight - 1) ||
            points[2].X <= Math.Max(points[0].X, points[1].X) ||
            points[1].Y >= Math.Min(points[0].Y, points[2].Y)) return null;
        double determinant = (points[1].X - points[0].X) * (points[2].Y - points[0].Y) -
            (points[1].Y - points[0].Y) * (points[2].X - points[0].X);
        if (determinant == 0) return null;
        // This is a user-selected OCR search rectangle, not measured axes or numeric evidence.
        // Keep the VM's exact fractional anchors; round only the enclosing pixel search bounds.
        var geometry = new RustAxisGeometry((int)Math.Floor(Math.Min(points[0].X, points[1].X)),
            (int)Math.Ceiling(Math.Max(points[0].Y, points[2].Y)), (int)Math.Ceiling(points[2].X),
            (int)Math.Floor(points[1].Y), 0);
        var draft = new WorkspaceAxisSuggestion(tabId, AxisSuggestionBinding(tab), geometry)
            { OriginalImageIdentity = image, IsUserDefined = true, Provider = "manual" };
        return IsAxisSuggestionCurrent(draft) ? draft : null;
    }

    public async Task<WorkspaceAxisSuggestion?> SuggestAxesAsync(string tabId, CancellationToken cancellationToken)
    {
        if (!CanSuggestAxes) return null;
        WorkspaceTabViewModel tab = RequireTab(tabId);
        string binding = AxisSuggestionBinding(tab);
        var originalImage = _originalPanelBytesByTab[tabId];
        byte[] bytes = originalImage.Copy();
        RustAxisProposalResult result = await _axisProposalService!.SuggestAsync(bytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Geometry is not { } geometry || geometry.Left < 0 || geometry.Right >= tab.PixelWidth ||
            geometry.Top < 0 || geometry.Bottom >= tab.PixelHeight || geometry.Left >= geometry.Right ||
            geometry.Top >= geometry.Bottom || !double.IsFinite(geometry.Confidence) || geometry.Confidence is < 0 or > 1)
            return null;
        if (result.Provider == "opencv-review" && (geometry.FirstTickX is not null || geometry.LastTickX is not null ||
            geometry.XTickPositions.Count != 0 || geometry.YTickPositions.Count != 0)) return null;
        var suggestion = new WorkspaceAxisSuggestion(tabId, binding, geometry)
        {
            OriginalImageIdentity = originalImage, Provider = result.Provider,
            ReviewReasons = Array.AsReadOnly(result.ReviewReasons.ToArray()),
        };
        return IsAxisSuggestionCurrent(suggestion) ? suggestion : null;
    }

    public bool IsAxisSuggestionCurrent(WorkspaceAxisSuggestion suggestion)
    {
        WorkspaceTabViewModel? tab = _tabs.SingleOrDefault(tab => tab.TabId == suggestion.TabId);
        return tab is not null && _originalPanelBytesByTab.TryGetValue(tab.TabId, out var image) &&
            ReferenceEquals(image, suggestion.OriginalImageIdentity) && suggestion.Binding == AxisSuggestionBinding(tab);
    }

    public bool CanReadCalibrationLabels => _calibrationOcrSuggestionService is not null &&
        Volatile.Read(ref _calibrationOcrDisposed) == 0 &&
        RuntimeEnvironment == GraphReader.App.Integration.Workflow.WorkflowRuntimeEnvironment.ManualPreview;

    public async Task<WorkspaceCalibrationLabelSuggestion?> ReadCalibrationLabelsAsync(
        WorkspaceAxisSuggestion suggestion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanReadCalibrationLabels || !IsAxisSuggestionCurrent(suggestion)) return null;
        WorkspaceTabViewModel tab = RequireTab(suggestion.TabId);
        RustAxisGeometry geometry = suggestion.Geometry;
        if (geometry.Left < 0 || geometry.Top < 0 || geometry.Right >= tab.PixelWidth ||
            geometry.Bottom >= tab.PixelHeight || geometry.Left >= geometry.Right || geometry.Top >= geometry.Bottom)
            return null;
        var panel = CurrentProject.Panels.Single(item => item.PanelId.Value.ToString() == tab.PanelId);
        var source = CurrentProject.Sources.Single(item => item.SourceId == panel.SourceId);
        byte[] image = _originalPanelBytesByTab[suggestion.TabId].Copy();
        string imageHash = Convert.ToHexStringLower(SHA256.HashData(image));
        bool tickMeasurementAttempted = suggestion.Provider == "opencv-review" && _axisTickMeasurementService is not null;
        string? tickMeasurementError = null;
        CalibrationTickEvidenceIdentity? tickIdentity = null;
        if (tickMeasurementAttempted)
        {
            AxisTickMeasurementResult measurement = await _axisTickMeasurementService!.MeasureAsync(image, geometry, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAxisSuggestionCurrent(suggestion)) return null;
            if (TryBindMeasuredTicks(measurement, suggestion, imageHash, tab.PixelWidth, tab.PixelHeight) is { } measured)
            {
                geometry = measured;
                tickIdentity = new(suggestion, suggestion.OriginalImageIdentity, suggestion.Binding, imageHash, measured);
            }
            else tickMeasurementError = measurement.ErrorCode ?? "INVALID_TICK_EVIDENCE";
        }
        var request = new CalibrationOcrRequest(Guid.NewGuid().ToString("N"), suggestion.Binding, source.Sha256,
            imageHash, tab.PixelWidth, tab.PixelHeight,
            new CalibrationOcrPlot(geometry.Left, geometry.Top, geometry.Right, geometry.Bottom))
        {
            MeasuredXTicks = geometry.XTickPositions.Count > 0 ? geometry.XTickPositions.ToArray() : null,
            MeasuredYTicks = geometry.YTickPositions.Count > 0 ? geometry.YTickPositions.ToArray() : null,
        };
        CalibrationOcrResponse response = await _calibrationOcrSuggestionService!.SuggestAsync(request, image, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAxisSuggestionCurrent(suggestion) || !CalibrationOcrWorkerClient.IsValidResponse(request, response)) return null;
        var result = new WorkspaceCalibrationLabelSuggestion(suggestion, response)
        {
            FitGeometry = geometry, TickEvidenceIdentity = tickIdentity,
            TickMeasurementAttempted = tickMeasurementAttempted, TickMeasurementErrorCode = tickMeasurementError,
        };
        return IsCalibrationLabelSuggestionCurrent(result) ? result : null;
    }

    public bool IsCalibrationLabelSuggestionCurrent(WorkspaceCalibrationLabelSuggestion result) =>
        result is not null && CanReadCalibrationLabels && IsAxisSuggestionCurrent(result.AxisSuggestion) &&
            result.Response.Request is { } request && CalibrationOcrWorkerClient.IsValidResponse(request, result.Response) &&
        request.Binding == result.AxisSuggestion.Binding &&
        request.Plot == new CalibrationOcrPlot(result.AxisSuggestion.Geometry.Left, result.AxisSuggestion.Geometry.Top,
                result.AxisSuggestion.Geometry.Right, result.AxisSuggestion.Geometry.Bottom) &&
            IsCalibrationTickEvidenceCurrent(result) &&
            (request.MeasuredXTicks ?? []).SequenceEqual(result.FitGeometry.XTickPositions) &&
            (request.MeasuredYTicks ?? []).SequenceEqual(result.FitGeometry.YTickPositions);

    private static RustAxisGeometry? TryBindMeasuredTicks(AxisTickMeasurementResult measurement,
        WorkspaceAxisSuggestion suggestion, string imageHash, int width, int height)
    {
        RustAxisGeometry box = suggestion.Geometry;
        if (measurement.ErrorCode is not null || measurement.PanelImageSha256 != imageHash ||
            measurement.Width != width || measurement.Height != height || measurement.Evidence is not { } evidence ||
            evidence.Left != box.Left || evidence.Bottom != box.Bottom || evidence.Right != box.Right || evidence.Top != box.Top ||
            evidence.XTickPositions is null || evidence.YTickPositions is null ||
            evidence.XTickPositions.Count > 128 || evidence.YTickPositions.Count > 128) return null;
        int[] xSnapshot = evidence.XTickPositions.ToArray(), ySnapshot = evidence.YTickPositions.ToArray();
        if (!ValidMeasuredTicks(xSnapshot, box.Left, box.Right) || !ValidMeasuredTicks(ySnapshot, box.Top, box.Bottom)) return null;
        var x = Array.AsReadOnly(xSnapshot);
        var y = Array.AsReadOnly(ySnapshot);
        return box with { XTickPositions = x, YTickPositions = y,
            FirstTickX = x.Count == 0 ? null : x[0], LastTickX = x.Count == 0 ? null : x[^1] };
    }

    private static bool ValidMeasuredTicks(int[]? ticks, int minimum, int maximum)
    {
        if (ticks is null || ticks.Length is 1 or 2 or > 128) return false;
        for (int i = 0; i < ticks.Length; i++)
            if (ticks[i] < minimum || ticks[i] > maximum || i > 0 && ticks[i] - ticks[i - 1] < 4) return false;
        return true;
    }

    private static bool IsCalibrationTickEvidenceCurrent(WorkspaceCalibrationLabelSuggestion result)
    {
        if (ReferenceEquals(result.FitGeometry, result.AxisSuggestion.Geometry)) return result.TickEvidenceIdentity is null;
        return result.TickEvidenceIdentity is CalibrationTickEvidenceIdentity identity &&
            ReferenceEquals(identity.Suggestion, result.AxisSuggestion) &&
            ReferenceEquals(identity.OriginalImageIdentity, result.AxisSuggestion.OriginalImageIdentity) &&
            ReferenceEquals(identity.Geometry, result.FitGeometry) && identity.Binding == result.AxisSuggestion.Binding &&
            identity.ImageSha256 == result.Response.Request?.PanelImageSha256;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _calibrationOcrDisposed, 1) == 0 && _calibrationOcrSuggestionService is not null)
            _calibrationOcrSuggestionService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Interlocked.Exchange(ref _markerExemplarDisposed, 1) == 0 && _markerExemplarSuggestionService is not null)
            _markerExemplarSuggestionService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private string AxisSuggestionBinding(WorkspaceTabViewModel tab)
    {
        var panel = CurrentProject.Panels.Single(panel => panel.PanelId.Value.ToString() == tab.PanelId);
        var source = CurrentProject.Sources.Single(source => source.SourceId == panel.SourceId);
        string payload = JsonSerializer.Serialize(new
        {
            CurrentProject.ProjectId, panel.PanelId, panel.SourceId, source.Sha256,
            tab.SourceSha256, tab.PixelWidth, tab.PixelHeight, panel.PageNumber, panel.Crop, panel.Transforms, tab.Calibration,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
