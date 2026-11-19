// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.App.ViewModels;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Localization;
using GraphReader.Axis;

namespace GraphReader.App.Services;

public sealed record WorkspaceAxisSuggestion(string TabId, string Binding, RustAxisGeometry? Geometry)
{
    public RustAffineAxisGeometry? AffineGeometry { get; init; }
    internal object? OriginalImageIdentity { get; init; }
    internal MeasuredCalibrationDraft? MeasuredDraft { get; init; }
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

public sealed record WorkspaceCalibrationLabelSuggestion
{
    public WorkspaceCalibrationLabelSuggestion(WorkspaceAxisSuggestion axisSuggestion, CalibrationOcrResponse response)
        : this(axisSuggestion, response, axisSuggestion.Geometry ??
            throw new ArgumentException("Label suggestions require a validated working frame.", nameof(axisSuggestion))) { }

    internal WorkspaceCalibrationLabelSuggestion(WorkspaceAxisSuggestion axisSuggestion, CalibrationOcrResponse response,
        RustAxisGeometry fitGeometry)
    {
        AxisSuggestion = axisSuggestion; Response = response; FitGeometry = fitGeometry;
    }

    public WorkspaceAxisSuggestion AxisSuggestion { get; init; }
    public CalibrationOcrResponse Response { get; init; }
    internal RustAxisGeometry FitGeometry { get; init; }
    internal CalibrationWorkingFrame? WorkingFrame { get; init; }
    internal object? WorkingFrameIdentity { get; init; }
    internal AxisTickMeasurementResult? TickMeasurementReceipt { get; init; }
    public bool IsRectified => WorkingFrame is not null;
    public CalibrationTickAnchors OriginalAnchors(CalibrationTickAnchors anchors) => WorkingFrame?.ToOriginal(anchors) ?? anchors;
    public CalibrationOcrPoint OriginalTickPoint(string axis, int position) => ToOriginal(axis switch
    {
        "X" => new(position, FitGeometry.Bottom),
        "Y" => new(FitGeometry.Left, position),
        _ => throw new ArgumentException("Unknown tick axis.", nameof(axis)),
    });
    public IReadOnlyList<CalibrationOcrPoint> OriginalPolygon(CalibrationOcrProposal proposal)
    {
        if (!Response.Proposals.Any(row => ReferenceEquals(row, proposal)))
            throw new ArgumentException("The row does not belong to this receipt.", nameof(proposal));
        return Array.AsReadOnly(proposal.Polygon.Select(ToOriginal).ToArray());
    }
    private CalibrationOcrPoint ToOriginal(CalibrationOcrPoint point) => WorkingFrame?.ToOriginal(point) ?? point;
    internal bool ContainsOriginal(CalibrationTickAnchors anchors) => WorkingFrame is null ||
        WorkingFrame.ContainsOriginal(anchors.FirstSessionY0) && WorkingFrame.ContainsOriginal(anchors.FirstSessionYMaximum) &&
        WorkingFrame.ContainsOriginal(anchors.FinalSessionY0);
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
    private sealed record CalibrationWorkingFrameIdentity(WorkspaceAxisSuggestion Suggestion, CalibrationWorkingFrame Frame,
        object? OriginalImageIdentity, RustAxisGeometry Geometry, CalibrationOcrRequest Request, CalibrationOcrResponse Response,
        AxisTickMeasurementResult? Measurement);
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
        bool legacyAligned = ManualCalibrationMapper.HasLegacyAlignment(points[0], points[1], points[2]);
        if (!legacyAligned)
        {
            var affine = new RustAffineAxisGeometry(
                new(points[0].X, points[0].Y), new(points[2].X, points[2].Y),
                new(points[1].X, points[1].Y), 0);
            if (!OpenCvAxisProposalService.ValidAffineGeometry(affine, tab.PixelWidth, tab.PixelHeight)) return null;
            var affineDraft = new WorkspaceAxisSuggestion(tabId, AxisSuggestionBinding(tab), null)
            {
                AffineGeometry = affine,
                OriginalImageIdentity = image,
                IsUserDefined = true,
                Provider = "manual",
                ReviewReasons = Array.AsReadOnly(new[] { ManualCalibrationMapper.AffineReason }),
            };
            return IsAxisSuggestionCurrent(affineDraft) ? affineDraft : null;
        }
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
        if (!_tabs.Contains(tab) || !_originalPanelBytesByTab.TryGetValue(tabId, out var currentImage) ||
            !ReferenceEquals(currentImage, originalImage) || binding != AxisSuggestionBinding(tab)) return null;
        if (result.AffineGeometry is { } frame)
        {
            if (!OpenCvAxisProposalService.Valid(result, tab.PixelWidth, tab.PixelHeight)) return null;
            var affine = new WorkspaceAxisSuggestion(tabId, binding, null)
            {
                AffineGeometry = frame, OriginalImageIdentity = originalImage, Provider = result.Provider,
                ReviewReasons = Array.AsReadOnly(result.ReviewReasons.ToArray()),
            };
            return IsAxisSuggestionCurrent(affine) ? affine : null;
        }
        if (result.Geometry is null && AxisFailure(result) is { } failure)
            throw new ProductionWorkflowStageException(failure);
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

    private static ProductionWorkflowFailure? AxisFailure(RustAxisProposalResult result)
    {
        if (result.Geometry is not null || result.AffineGeometry is not null || result.Provider is not ("rust" or "opencv-review")) return null;
        string? reason = result.ErrorCode is "WORKER_TIMEOUT" or "OPENCV_AXIS_TIMEOUT" ? "timeout" : null;
        if (reason is null && result.ErrorCode is ("NO_AXIS" or "NO_INTERSECTION" or "AMBIGUOUS_AXIS" or "OPENCV_AXIS_AMBIGUOUS") &&
            result.ReviewReasons is { Count: <= 32 } reasons &&
            reasons.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 128))
        {
            if (reasons.Contains("native_axis_timeout", StringComparer.Ordinal)) reason = "timeout";
            else if (reasons.Contains("native_pair_budget_exceeded", StringComparer.Ordinal) ||
                reasons.Contains("native_envelope_budget_exceeded", StringComparer.Ordinal)) reason = "budget";
            else if (reasons.Contains("native_rotated_geometry", StringComparer.Ordinal)) reason = "rotation";
        }
        return reason switch
        {
            "timeout" => new("WORKFLOW_AXIS_TIMEOUT", LocalizationKeys.ManualAxisTimeout,
                "The axis detection worker timed out.", true, "Mark axes manually and review the calibration."),
            "budget" => new("WORKFLOW_AXIS_PAIR_BUDGET_EXCEEDED", LocalizationKeys.ManualAxisPairBudgetExceeded,
                "The native axis search reached its pair-work limit.", true, "Mark axes manually and review the calibration."),
            "rotation" => new("WORKFLOW_AXIS_ROTATED_UNSUPPORTED", LocalizationKeys.ManualAxisRotatedUnsupported,
                "The detected axis geometry is rotated beyond the review representation.", true, "Mark axes manually and review the calibration."),
            _ => null,
        };
    }

    internal WorkspaceAxisSuggestion? BindMeasuredDraft(WorkspaceTabViewModel tab, MeasuredCalibrationDraft draft)
    {
        if (tab.Calibration is not null || !_tabs.Contains(tab) || !_originalPanelBytesByTab.TryGetValue(tab.TabId, out var image) ||
            draft.Evidence.ProjectId != CurrentProject.ProjectId.Value || draft.Evidence.PanelId.ToString() != tab.PanelId ||
            !string.Equals(draft.Evidence.InputSha256, tab.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !(draft.LeftX >= 0 && draft.RightX > draft.LeftX && draft.RightX <= tab.PixelWidth - 1 &&
              draft.UpperY >= 0 && draft.ZeroY > draft.UpperY && draft.ZeroY <= tab.PixelHeight - 1 &&
              double.IsFinite(draft.UpperValue) && draft.UpperValue > 0)) return null;
        var bounds = new RustAxisGeometry((int)Math.Floor(draft.LeftX), (int)Math.Ceiling(draft.ZeroY),
            (int)Math.Ceiling(draft.RightX), (int)Math.Floor(draft.UpperY), 0);
        return new(tab.TabId, AxisSuggestionBinding(tab), bounds)
            { OriginalImageIdentity = image, Provider = "measured-review", MeasuredDraft = draft };
    }

    public bool IsAxisSuggestionCurrent(WorkspaceAxisSuggestion suggestion)
    {
        WorkspaceTabViewModel? tab = _tabs.SingleOrDefault(tab => tab.TabId == suggestion.TabId);
        return tab is not null && _originalPanelBytesByTab.TryGetValue(tab.TabId, out var image) &&
            ReferenceEquals(image, suggestion.OriginalImageIdentity) && suggestion.Binding == AxisSuggestionBinding(tab) &&
            (suggestion.AffineGeometry is { } frame
                ? suggestion.Geometry is null &&
                    (suggestion.IsUserDefined && suggestion.Provider == "manual"
                        ? suggestion.ReviewReasons is { Count: 1 } manualReasons &&
                            manualReasons[0] == ManualCalibrationMapper.AffineReason &&
                            OpenCvAxisProposalService.ValidAffineGeometry(frame, tab.PixelWidth, tab.PixelHeight)
                        : !suggestion.IsUserDefined && suggestion.Provider == "opencv-review" &&
                            suggestion.ReviewReasons is { Count: <= 32 } reasons &&
                            reasons.All(reason => !string.IsNullOrWhiteSpace(reason) && reason.Length <= 128) &&
                            suggestion.ReviewReasons.Contains("native_affine_geometry", StringComparer.Ordinal) &&
                            OpenCvAxisProposalService.ValidAffineGeometry(frame, tab.PixelWidth, tab.PixelHeight))
                : suggestion.Geometry is not null);
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
        if (suggestion.AffineGeometry is not null)
            return await ReadRectifiedCalibrationLabelsAsync(suggestion, cancellationToken);
        if (suggestion.Geometry is not { } geometry) return null;
        WorkspaceTabViewModel tab = RequireTab(suggestion.TabId);
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
            if (TryBindMeasuredTicks(measurement, geometry, imageHash, tab.PixelWidth, tab.PixelHeight) is { } measured)
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

    private async Task<WorkspaceCalibrationLabelSuggestion?> ReadRectifiedCalibrationLabelsAsync(
        WorkspaceAxisSuggestion suggestion, CancellationToken token)
    {
        WorkspaceTabViewModel tab = RequireTab(suggestion.TabId);
        var panel = CurrentProject.Panels.Single(item => item.PanelId.Value.ToString() == tab.PanelId);
        var source = CurrentProject.Sources.Single(item => item.SourceId == panel.SourceId);
        // Decode, normalization and resampling belong off the dispatcher. No source buffer is mutated.
        var original = _originalPanelBytesByTab[suggestion.TabId];
        int width = tab.PixelWidth, height = tab.PixelHeight;
        RustAffineAxisGeometry affine = suggestion.AffineGeometry!;
        (CalibrationWorkingFrame Frame, byte[] Image, RustAffineTickSource OriginalSupport) prepared;
        try
        {
            prepared = await Task.Run(() => CalibrationWorkingFrame.Create(original.Copy(), width, height,
                affine, suggestion.Binding, token), token);
        }
        catch (Exception error) when (error is System.IO.IOException or NotSupportedException or ArgumentException or FormatException or OverflowException)
        {
            token.ThrowIfCancellationRequested();
            if (!IsAxisSuggestionCurrent(suggestion)) return null;
            throw new ProductionWorkflowStageException(new("CALIBRATION_RECTIFICATION_INVALID_FRAME",
                LocalizationKeys.ManualCalibrationRectificationFailed, "The affine calibration frame cannot be rectified.",
                true, "Review the original marked axes or enter calibration values manually."));
        }
        token.ThrowIfCancellationRequested();
        if (!CanReadCalibrationLabels || !IsAxisSuggestionCurrent(suggestion)) return null;
        CalibrationWorkingFrame frame = prepared.Frame;
        RustAxisGeometry geometry = frame.Geometry;
        bool attempted = _axisTickMeasurementService is not null;
        string? measurementError = null;
        AxisTickMeasurementResult? measurementReceipt = null;
        if (attempted)
        {
            // The compatible manual-partial API denotes label-review semantics.
            // Keeping independently observed ticks does not approve this frame;
            // automatic provenance and the existing review/Apply gates remain.
            AxisTickMeasurementResult measurement = await _axisTickMeasurementService!
                .MeasureAffineManualPartialAsync(prepared.Image, geometry, prepared.OriginalSupport, token);
            measurementReceipt = measurement;
            token.ThrowIfCancellationRequested();
            if (!CanReadCalibrationLabels || !IsAxisSuggestionCurrent(suggestion)) return null;
            if ((measurement.OriginalSupportBinding is null || measurement.OriginalSupportBinding == frame.OriginalSupportBinding) &&
                TryBindMeasuredTicks(measurement, geometry, frame.DerivedImageSha256, frame.Width, frame.Height) is { } measured)
                geometry = measured;
            else measurementError = measurement.ErrorCode ?? "INVALID_TICK_EVIDENCE";
        }
        var request = new CalibrationOcrRequest(Guid.NewGuid().ToString("N"), frame.Binding, source.Sha256,
            frame.DerivedImageSha256, frame.Width, frame.Height, new(geometry.Left, geometry.Top, geometry.Right, geometry.Bottom))
        {
            MeasuredXTicks = geometry.XTickPositions.Count > 0 ? geometry.XTickPositions.ToArray() : null,
            MeasuredYTicks = geometry.YTickPositions.Count > 0 ? geometry.YTickPositions.ToArray() : null,
        };
        CalibrationOcrRequest expected = SnapshotRequest(request);
        CalibrationOcrResponse response = await _calibrationOcrSuggestionService!.SuggestAsync(request, prepared.Image, token);
        token.ThrowIfCancellationRequested();
        if (!IsAxisSuggestionCurrent(suggestion) || !CalibrationOcrWorkerClient.IsValidResponse(expected, response)) return null;
        var result = new WorkspaceCalibrationLabelSuggestion(suggestion, response, geometry)
        {
            WorkingFrame = frame,
            WorkingFrameIdentity = new CalibrationWorkingFrameIdentity(suggestion, frame, original, geometry, expected, response, measurementReceipt),
            TickMeasurementReceipt = measurementReceipt,
            TickMeasurementAttempted = attempted, TickMeasurementErrorCode = measurementError,
        };
        return IsCalibrationLabelSuggestionCurrent(result) ? result : null;
    }

    private static CalibrationOcrRequest SnapshotRequest(CalibrationOcrRequest request) => request with
    {
        MeasuredXTicks = request.MeasuredXTicks?.ToArray(), MeasuredYTicks = request.MeasuredYTicks?.ToArray(),
    };

    public bool IsCalibrationLabelSuggestionCurrent(WorkspaceCalibrationLabelSuggestion result) =>
        result is not null && CanReadCalibrationLabels && (result.IsRectified ? IsRectifiedCalibrationCurrent(result) :
        result.AxisSuggestion.AffineGeometry is null &&
            result.AxisSuggestion.Geometry is { } geometry && IsAxisSuggestionCurrent(result.AxisSuggestion) &&
            result.Response.Request is { } request && CalibrationOcrWorkerClient.IsValidResponse(request, result.Response) &&
        request.Binding == result.AxisSuggestion.Binding &&
        request.Plot == new CalibrationOcrPlot(geometry.Left, geometry.Top, geometry.Right, geometry.Bottom) &&
            IsCalibrationTickEvidenceCurrent(result) &&
            (request.MeasuredXTicks ?? []).SequenceEqual(result.FitGeometry.XTickPositions) &&
            (request.MeasuredYTicks ?? []).SequenceEqual(result.FitGeometry.YTickPositions));

    private bool IsRectifiedCalibrationCurrent(WorkspaceCalibrationLabelSuggestion result) =>
        result.WorkingFrameIdentity is CalibrationWorkingFrameIdentity proof && result.WorkingFrame is { } frame &&
        ReferenceEquals(proof.Frame, frame) && ReferenceEquals(proof.Suggestion, result.AxisSuggestion) &&
        ReferenceEquals(proof.OriginalImageIdentity, result.AxisSuggestion.OriginalImageIdentity) &&
        ReferenceEquals(proof.Geometry, result.FitGeometry) && ReferenceEquals(proof.Response, result.Response) &&
        ReferenceEquals(proof.Measurement, result.TickMeasurementReceipt) &&
        IsAxisSuggestionCurrent(result.AxisSuggestion) && result.AxisSuggestion.AffineGeometry is not null &&
        result.TickEvidenceIdentity is null && result.Response.Request is { } request && proof.Request.Matches(request) &&
        request.Binding == frame.Binding && request.PanelImageSha256 == frame.DerivedImageSha256 &&
        request.Width == frame.Width && request.Height == frame.Height &&
        request.Plot == new CalibrationOcrPlot(frame.Geometry.Left, frame.Geometry.Top, frame.Geometry.Right, frame.Geometry.Bottom) &&
        CalibrationOcrWorkerClient.IsValidResponse(proof.Request, result.Response) &&
        (request.MeasuredXTicks ?? []).SequenceEqual(result.FitGeometry.XTickPositions) &&
        (request.MeasuredYTicks ?? []).SequenceEqual(result.FitGeometry.YTickPositions) &&
        result.Response.Proposals.All(row => row.Polygon.All(point => frame.ContainsOriginalEdge(frame.ToOriginal(point))));

    private static RustAxisGeometry? TryBindMeasuredTicks(AxisTickMeasurementResult measurement,
        RustAxisGeometry box, string imageHash, int width, int height)
    {
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
