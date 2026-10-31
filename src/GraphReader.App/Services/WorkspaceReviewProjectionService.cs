// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Models;
using GraphReader.App.Localization;
using GraphReader.App.ViewModels;
using GraphReader.Domain;

namespace GraphReader.App.Services;

public static class WorkspaceReviewProjectionService
{
    public static IReadOnlyList<FilteredMarkerCandidateViewModel> ProjectFilteredMarkers(
        WorkspaceTabViewModel tab, ProjectDocument? project)
    {
        ArgumentNullException.ThrowIfNull(tab);
        PanelRecord? panel = FindPanel(tab, project);
        if (panel is null) return [];
        HashSet<MarkerId> linked = panel.Points.Where(static point => point.MarkerId.HasValue)
            .Select(static point => point.MarkerId!.Value).ToHashSet();
        return panel.Markers.Where(marker => marker.ReviewStatus == ReviewStatus.Rejected && !linked.Contains(marker.MarkerId))
            .Select(marker =>
            {
                PixelPoint local = ManualPreviewWorkspaceService.MapPointSourceToPanel(panel, marker.Center);
                return new FilteredMarkerCandidateViewModel(marker.MarkerId.Value.ToString("D"), marker.Symbol,
                    local.X, local.Y, marker.ArtifactProbability);
            })
            .OrderBy(static marker => marker.PixelX).ThenBy(static marker => marker.PixelY)
            .ThenBy(static marker => marker.MarkerId, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<ReviewIssueViewModel> ProjectIssues(
        WorkspaceTabViewModel tab,
        ProjectDocument? project,
        Func<string, string> localize,
        IEnumerable<PipelineWarningPresentation>? pipelineWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(localize);
        PanelRecord? panel = FindPanel(tab, project);
        var issues = new List<ReviewIssueViewModel>();

        if (tab.Calibration is null)
        {
            issues.Add(Create(
                tab,
                "calibration-missing",
                null,
                ReviewIssueKind.Calibration,
                ReviewIssueSeverity.Blocking,
                "Review.CalibrationMissing",
                localize));
        }
        else if (panel?.Calibration is { Status: not CalibrationStatus.Valid })
        {
            issues.Add(Create(
                tab,
                "calibration-invalid",
                panel.Calibration.CalibrationId.Value.ToString("D"),
                ReviewIssueKind.Calibration,
                ReviewIssueSeverity.Blocking,
                "Review.CalibrationInvalid",
                localize));
        }

        if (panel is null)
        {
            AppendPipelineWarnings(issues, tab, localize, pipelineWarnings);
            return issues;
        }

        HashSet<SeriesId> knownSeries = panel.Series.Select(static series => series.SeriesId).ToHashSet();
        HashSet<PhaseId> knownPhases = panel.Phases.Select(static phase => phase.PhaseId).ToHashSet();
        foreach (PointRecord point in panel.Points.OrderBy(static point => point.ObservationIndex))
        {
            string pointId = point.PointId.Value.ToString("D");
            if (point.ReviewStatus == ReviewStatus.Rejected)
            {
                issues.Add(Create(tab, $"point-rejected-{pointId}", pointId,
                    ReviewIssueKind.Point, ReviewIssueSeverity.Warning,
                    "Review.PointRejected", localize));
            }
            else if (point.ReviewStatus == ReviewStatus.Unreviewed)
            {
                issues.Add(Create(tab, $"point-unreviewed-{pointId}", pointId,
                    ReviewIssueKind.Point, ReviewIssueSeverity.Warning,
                    "Review.PointUnreviewed", localize));
            }

            if (point.SeriesId is not { } seriesId || !knownSeries.Contains(seriesId))
            {
                issues.Add(Create(tab, $"point-series-{pointId}", pointId,
                    ReviewIssueKind.Point, ReviewIssueSeverity.Blocking,
                    "Review.PointMissingSeries", localize));
            }

            if (point.GraphX is null || point.GraphY is null)
            {
                issues.Add(Create(tab, $"point-coordinates-{pointId}", pointId,
                    ReviewIssueKind.Point, ReviewIssueSeverity.Blocking,
                    "Review.PointMissingCoordinates", localize));
            }

            if (point.PhaseId is not { } phaseId || !knownPhases.Contains(phaseId))
            {
                issues.Add(Create(tab, $"point-phase-{pointId}", pointId,
                    ReviewIssueKind.Phase, ReviewIssueSeverity.Blocking,
                    "Review.PointMissingPhase", localize));
            }
        }

        foreach (SeriesRecord series in panel.Series.Where(static series =>
                     series.SemanticRole == SemanticRole.Unknown))
        {
            string seriesId = series.SeriesId.Value.ToString("D");
            issues.Add(Create(tab, $"series-unknown-{seriesId}", seriesId,
                ReviewIssueKind.Series, ReviewIssueSeverity.Warning,
                "Review.SeriesUnknown", localize));
        }

        foreach (PhaseRecord phase in panel.Phases.Where(static phase =>
                     phase.NormalizedType == PhaseNormalizedType.Unknown))
        {
            string phaseId = phase.PhaseId.Value.ToString("D");
            issues.Add(Create(tab, $"phase-unknown-{phaseId}", phaseId,
                ReviewIssueKind.Phase, ReviewIssueSeverity.Warning,
                "Review.PhaseUnknown", localize));
        }

        AppendPipelineWarnings(issues, tab, localize, pipelineWarnings);

        return issues;
    }

    private static void AppendPipelineWarnings(
        List<ReviewIssueViewModel> issues,
        WorkspaceTabViewModel tab,
        Func<string, string> localize,
        IEnumerable<PipelineWarningPresentation>? pipelineWarnings)
    {
        int warningIndex = 0;
        foreach (PipelineWarningPresentation warning in
                 (pipelineWarnings ?? Array.Empty<PipelineWarningPresentation>())
                 .Where(static value => !string.IsNullOrWhiteSpace(value.TechnicalMessage))
                 .DistinctBy(static value => value.TechnicalMessage, StringComparer.Ordinal))
        {
            string interpretationKey = LocalizationKeys.All.Contains(
                warning.UserMessageKey,
                StringComparer.Ordinal)
                    ? warning.UserMessageKey
                    : LocalizationKeys.ReviewPipelineWarningInterpretation;
            issues.Add(new ReviewIssueViewModel(
                $"pipeline-warning-{warningIndex++}",
                tab.TabId,
                EntityId: null,
                ReviewIssueKind.PipelineWarning,
                ReviewIssueSeverity.Warning,
                LocalizationKeys.ReviewPipelineWarningTitle,
                interpretationKey,
                LocalizationKeys.ReviewPipelineWarningAction,
                localize(LocalizationKeys.ReviewPipelineWarningTitle),
                localize(interpretationKey),
                localize(LocalizationKeys.ReviewPipelineWarningAction),
                warning.TechnicalMessage.Trim()));
        }
    }

    public static IReadOnlyList<DataPreviewRowViewModel> ProjectRows(
        WorkspaceTabViewModel tab,
        ProjectDocument? project)
    {
        ArgumentNullException.ThrowIfNull(tab);
        PanelRecord? panel = FindPanel(tab, project);
        Dictionary<string, PointRecord> domainPoints = panel?.Points.ToDictionary(
            static point => point.PointId.Value.ToString("D"),
            StringComparer.Ordinal) ?? new Dictionary<string, PointRecord>(StringComparer.Ordinal);
        Dictionary<string, string> seriesLabels = tab.SeriesCards.ToDictionary(
            static series => series.SeriesId,
            static series => series.Label,
            StringComparer.Ordinal);

        return tab.Points
            .OrderBy(static point => point.ObservationIndex)
            .ThenBy(static point => point.PixelX)
            .Select(point =>
            {
                _ = domainPoints.TryGetValue(point.PointId, out PointRecord? domainPoint);
                return new DataPreviewRowViewModel(
                    point.PointId,
                    point.ObservationIndex,
                    domainPoint?.PrintedXValue,
                    domainPoint?.EstimatedXValue,
                    domainPoint?.GraphX ?? (tab.Calibration is null ? null : point.GraphX),
                    domainPoint?.GraphY ?? (tab.Calibration is null ? null : point.GraphY),
                    point.PhaseCode,
                    point.SeriesId,
                    seriesLabels.GetValueOrDefault(point.SeriesId, point.SeriesId),
                    point.PixelX,
                    point.PixelY);
            })
            .ToArray();
    }

    private static PanelRecord? FindPanel(WorkspaceTabViewModel tab, ProjectDocument? project) =>
        project?.Panels.FirstOrDefault(panel => string.Equals(
            panel.PanelId.Value.ToString("D"),
            tab.PanelId,
            StringComparison.OrdinalIgnoreCase));

    private static ReviewIssueViewModel Create(
        WorkspaceTabViewModel tab,
        string issueId,
        string? entityId,
        ReviewIssueKind kind,
        ReviewIssueSeverity severity,
        string keyPrefix,
        Func<string, string> localize)
    {
        string titleKey = $"{keyPrefix}.Title";
        string interpretationKey = $"{keyPrefix}.Interpretation";
        string actionKey = $"{keyPrefix}.Action";
        return new ReviewIssueViewModel(
            issueId,
            tab.TabId,
            entityId,
            kind,
            severity,
            titleKey,
            interpretationKey,
            actionKey,
            localize(titleKey),
            localize(interpretationKey),
            localize(actionKey));
    }
}
