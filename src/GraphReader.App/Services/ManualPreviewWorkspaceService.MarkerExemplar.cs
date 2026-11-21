// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.App.Integration.MarkerExemplar;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.ViewModels;
using GraphReader.App.Models;
using GraphReader.Domain;

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
        if (panel is null || (ManualCalibrationMapper.IsAxisAligned(calibration) && !calibration.UsesPrintedXCoordinates
            ? !IsConfirmedManualThreeAnchorCalibration(panel.Calibration)
            : !IsConfirmedReviewedMarkerCalibration(panel, calibration))) return false;
        var source = CurrentProject.Sources.SingleOrDefault(item => item.SourceId == panel.SourceId);
        var point = tab.Points.SingleOrDefault(item => item.PointId == pointId && item.SeriesId == seriesId);
        var series = tab.SeriesCards.SingleOrDefault(item => item.SeriesId == seriesId);
        if (source is null || point is null || series is null ||
            !double.IsFinite(point.PixelX) || !double.IsFinite(point.PixelY)) return false;
        if (!MarkerExemplarGeometry.TryCreatePlot(calibration, tab.PixelWidth, tab.PixelHeight, out var plot, out var frame)) return false;
        int x = (int)Math.Floor(point.PixelX - size / 2d), y = (int)Math.Floor(point.PixelY - size / 2d);
        var exemplar = new MarkerExemplarRectangle(x, y, size, size);
        bool filledCircle = series.Shape == MarkerShape.Circle && series.Fill == MarkerFill.Filled;
        string payload = JsonSerializer.Serialize(new { Axis = AxisSuggestionBinding(tab), panel.Calibration,
            point.PointId, point.SeriesId, point.PixelX, point.PixelY, size, filledCircle });
        string binding = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        request = new MarkerExemplarRequest(requestId, binding, source.Sha256, imageHash, tab.PanelId,
            tab.PixelWidth, tab.PixelHeight, exemplar, plot, pointId, seriesId, new(point.PixelX, point.PixelY))
        {
            AffinePlot = frame,
            IsFilledCircleSeries = filledCircle,
        };
        return MarkerExemplarValidation.ValidMetadata(request);
    }

    private static bool IsConfirmedReviewedMarkerCalibration(PanelRecord panel, ManualCalibrationState calibration)
    {
        bool affine = !ManualCalibrationMapper.IsAxisAligned(calibration);
        if ((!affine && !calibration.UsesPrintedXCoordinates) || panel.Calibration is not
                { Status: CalibrationStatus.Valid, UserConfirmed: true, SessionLattice: null, Reasons: { } reasons } accepted ||
            affine && !reasons.Contains(ManualCalibrationMapper.AffineReason, StringComparer.Ordinal) ||
            FromDomainCalibration(accepted) is null) return false;
        // Compare in the persisted source frame. An inverse crop round trip can differ
        // by an ulp from the live panel anchors even though the original forward sum is exact.
        CalibrationRecord expected = MapCalibrationPanelToSource(panel, ToDomainCalibration(calibration))!;
        return accepted.Confidence == expected.Confidence && accepted.Anchors.Count == expected.Anchors.Count &&
            expected.Anchors.All(anchor => accepted.Anchors.Contains(anchor));
    }
}
