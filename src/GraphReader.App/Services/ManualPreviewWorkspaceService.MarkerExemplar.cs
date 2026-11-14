// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.App.Integration.MarkerExemplar;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.ViewModels;

namespace GraphReader.App.Services;

public partial class ManualPreviewWorkspaceService
{
    private readonly IMarkerExemplarSuggestionService? _markerExemplarSuggestionService;
    private int _markerExemplarDisposed;

    public bool CanFindMarkerExamples => _markerExemplarSuggestionService is not null &&
        Volatile.Read(ref _markerExemplarDisposed) == 0 && RuntimeEnvironment == WorkflowRuntimeEnvironment.ManualPreview;

    public async Task<WorkspaceMarkerExemplarSuggestion?> FindMarkerExamplesAsync(string tabId, string pointId,
        string seriesId, int exampleSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanFindMarkerExamples || !_originalPanelBytesByTab.TryGetValue(tabId, out var original)) return null;
        string requestId = Guid.NewGuid().ToString("N");
        // Bind state before dispatch, and repeat the check after hashing and after worker completion.
        if (!TryCreateMarkerRequest(tabId, pointId, seriesId, exampleSize, requestId, new string('0', 64), out var before)) return null;
        byte[] bytes = original.Copy();
        string hash = await Task.Run(() => Convert.ToHexStringLower(SHA256.HashData(bytes)), cancellationToken);
        if (!TryCreateMarkerRequest(tabId, pointId, seriesId, exampleSize, requestId, hash, out var request) ||
            (before! with { PanelImageSha256 = hash }) != request ||
            !_originalPanelBytesByTab.TryGetValue(tabId, out var current) || !ReferenceEquals(original, current)) return null;
        MarkerExemplarResponse response = await _markerExemplarSuggestionService!.FindAsync(request!, bytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new WorkspaceMarkerExemplarSuggestion(tabId, request!, response) { OriginalImageIdentity = original };
        return IsMarkerExemplarSuggestionCurrent(result) ? result : null;
    }

    public bool IsMarkerExemplarSuggestionCurrent(WorkspaceMarkerExemplarSuggestion suggestion)
    {
        if (suggestion is null || !CanFindMarkerExamples ||
            !_originalPanelBytesByTab.TryGetValue(suggestion.TabId, out var original) ||
            !ReferenceEquals(original, suggestion.OriginalImageIdentity) ||
            !MarkerExemplarValidation.ValidResponse(suggestion.Request, suggestion.Response)) return false;
        MarkerExemplarRequest request = suggestion.Request;
        return TryCreateMarkerRequest(suggestion.TabId, request.SelectedPointId, request.SelectedSeriesId,
            request.Exemplar.Width, request.RequestId, request.PanelImageSha256, out var current) && request == current;
    }

    private bool TryCreateMarkerRequest(string tabId, string pointId, string seriesId, int size,
        string requestId, string imageHash, out MarkerExemplarRequest? request)
    {
        request = null;
        if (size is < 5 or > 65 || string.IsNullOrWhiteSpace(tabId) || string.IsNullOrWhiteSpace(pointId) ||
            string.IsNullOrWhiteSpace(seriesId)) return false;
        WorkspaceTabViewModel? tab = _tabs.SingleOrDefault(item => item.TabId == tabId);
        if (tab?.PanelId is null || tab.Calibration is not { } calibration ||
            !_originalPanelBytesByTab.ContainsKey(tabId)) return false;
        var panel = CurrentProject.Panels.SingleOrDefault(item => item.PanelId.Value.ToString() == tab.PanelId);
        if (panel is null || !IsConfirmedManualThreeAnchorCalibration(panel.Calibration)) return false;
        var source = CurrentProject.Sources.SingleOrDefault(item => item.SourceId == panel.SourceId);
        var point = tab.Points.SingleOrDefault(item => item.PointId == pointId && item.SeriesId == seriesId);
        if (source is null || point is null || !tab.SeriesCards.Any(item => item.SeriesId == seriesId) ||
            !double.IsFinite(point.PixelX) || !double.IsFinite(point.PixelY)) return false;
        var origin = calibration.Session1Y0;
        var upper = calibration.Session1YMaximum;
        var right = calibration.SessionMaximumY0;
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y) || !double.IsFinite(upper.X) ||
            !double.IsFinite(upper.Y) || !double.IsFinite(right.X) || !double.IsFinite(right.Y) ||
            origin.X < 0 || upper.Y < 0 ||
            right.X >= tab.PixelWidth || origin.Y >= tab.PixelHeight || right.X <= origin.X || upper.Y >= origin.Y)
            return false;
        int left = (int)Math.Ceiling(origin.X), top = (int)Math.Ceiling(upper.Y);
        int plotRight = (int)Math.Floor(right.X), bottom = (int)Math.Floor(origin.Y);
        int x = (int)Math.Floor(point.PixelX - size / 2d), y = (int)Math.Floor(point.PixelY - size / 2d);
        var plot = new MarkerExemplarRectangle(left, top, plotRight - left + 1, bottom - top + 1);
        var exemplar = new MarkerExemplarRectangle(x, y, size, size);
        if (x < 0 || y < 0 || (long)x + size > tab.PixelWidth || (long)y + size > tab.PixelHeight ||
            !MarkerExemplarGeometry.ContainsCenter(plot, new MarkerExemplarPoint(point.PixelX, point.PixelY))) return false;
        string payload = JsonSerializer.Serialize(new { Axis = AxisSuggestionBinding(tab), panel.Calibration,
            point.PointId, point.SeriesId, point.PixelX, point.PixelY, size });
        string binding = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        request = new MarkerExemplarRequest(requestId, binding, source.Sha256, imageHash, tab.PanelId,
            tab.PixelWidth, tab.PixelHeight, exemplar, plot, pointId, seriesId, new(point.PixelX, point.PixelY));
        return MarkerExemplarValidation.ValidMetadata(request);
    }
}
