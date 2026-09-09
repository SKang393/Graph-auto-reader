// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Export;
using GraphReader.Pdf;

namespace GraphReader.App.Integration.Workflow;

public sealed class ProductionWorkflowExportStage : IWorkflowExportStage
{
    private readonly ProductionWorkflowPanelStore panelStore;
    private readonly IExportService exportService;

    public ProductionWorkflowExportStage(
        ProductionWorkflowPanelStore panelStore,
        IExportService exportService)
    {
        this.panelStore = panelStore ?? throw new ArgumentNullException(nameof(panelStore));
        this.exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
    }

    public async Task<WorkflowExportResult> ExportAsync(
        WorkflowReviewState review,
        WorkflowExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var exportRequests = new List<ExportRequest>(review.Panels.Count);
        foreach (WorkflowReviewPanel panel in review.Panels.OrderBy(static panel => panel.PanelId))
        {
            if (!TryBuildRequest(review.ProjectId, panel, request, out ExportRequest? exportRequest))
            {
                return RecalibrationRequired();
            }

            exportRequests.Add(exportRequest!);
        }

        if (exportRequests.Count == 0)
        {
            return RecalibrationRequired();
        }

        var artifacts = new List<WorkflowExportArtifact>();
        var warnings = new List<string>();
        foreach (ExportRequest exportRequest in exportRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportResult result = await exportService
                .ExportAsync(exportRequest, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            warnings.AddRange(result.Warnings);
            if (!result.Succeeded)
            {
                ExportFailure? failure = result.Failures.FirstOrDefault(static item =>
                    item.Severity == ExportFailureSeverity.Error);
                return new WorkflowExportResult(
                    succeeded: false,
                    artifacts,
                    warnings.Concat(result.Failures.Select(static item => item.TechnicalMessage)),
                    failureCode: failure?.Code ?? "WORKFLOW_EXPORT_FAILED");
            }

            artifacts.AddRange(result.MinimalArtifacts.Select(static artifact =>
                new WorkflowExportArtifact(
                    artifact.FileName,
                    artifact.Sha256,
                    artifact.Rows.Count,
                    artifact.WrittenPath)));
            artifacts.AddRange(result.AuditArtifacts.Select(static artifact =>
                new WorkflowExportArtifact(
                    artifact.FileName,
                    artifact.Sha256,
                    artifact.Rows.Count,
                    artifact.WrittenPath)));
        }

        return new WorkflowExportResult(
            succeeded: true,
            artifacts,
            warnings.Distinct(StringComparer.Ordinal));
    }

    private bool TryBuildRequest(
        Guid projectId,
        WorkflowReviewPanel reviewPanel,
        WorkflowExportRequest workflowRequest,
        out ExportRequest? request)
    {
        request = null;
        if (!panelStore.TryGet(reviewPanel.PanelId, out ProductionPanelEvidence? panel) ||
            panel is null ||
            reviewPanel.PreparedPanel.ImportedPanel.SourceId != panel.Panel.SourceId ||
            reviewPanel.PreparedPanel.Original.Width != panel.Panel.Original.Width ||
            reviewPanel.PreparedPanel.Original.Height != panel.Panel.Original.Height ||
            !string.Equals(reviewPanel.PreparedPanel.Original.Sha256, panel.Panel.Original.Sha256, StringComparison.OrdinalIgnoreCase) ||
            panel.ExportEvidence is not { } evidence ||
            evidence.Calibration.Status != ExportCalibrationStatus.Valid ||
            !evidence.Calibration.HasYCalibration ||
            evidence.Phases.Count == 0 ||
            evidence.Series.Count == 0 ||
            evidence.Provenance.Count == 0)
        {
            return false;
        }

        if (evidence.Provenance.Any(envelope =>
                envelope.ProjectId != projectId ||
                envelope.PanelId != reviewPanel.PanelId ||
                (!string.Equals(envelope.InputSha256, panel.Panel.Original.Sha256, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(envelope.InputSha256, panel.Enhanced?.Sha256, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        var phaseIds = evidence.Phases.Select(static phase => phase.PhaseId).ToHashSet();
        var exportPhases = new List<ExportPhase>(evidence.Phases.Count);
        foreach (ExportPhase phase in evidence.Phases)
        {
            if (!TryMapOriginalPixel(panel, phase.OriginalXMinimum, 0, out ExportPixelPoint minimum) ||
                !TryMapOriginalPixel(panel, phase.OriginalXMaximum, 0, out ExportPixelPoint maximum))
            {
                return false;
            }
            exportPhases.Add(phase with { OriginalXMinimum = minimum.X, OriginalXMaximum = maximum.X });
        }
        var seriesById = evidence.Series.ToDictionary(static series => series.SeriesId);
        var pointEvidence = evidence.Points.ToDictionary(static point => point.PointId);
        var points = new List<ExportPoint>(reviewPanel.Points.Count);
        foreach (WorkflowPoint point in reviewPanel.Points)
        {
            if (!Guid.TryParse(point.PointId, out Guid pointId) ||
                !Guid.TryParse(point.SeriesId, out Guid seriesId) ||
                !Guid.TryParse(point.PhaseId, out Guid phaseId) ||
                point.GraphX is null ||
                point.GraphY is null ||
                !seriesById.ContainsKey(seriesId) ||
                !phaseIds.Contains(phaseId) ||
                !pointEvidence.TryGetValue(pointId, out ProductionPointExportEvidence? retained) ||
                retained.ObservationIndex < 1 ||
                !TryMapOriginalPixel(panel, point.OriginalPixelX, point.OriginalPixelY, out ExportPixelPoint originalPixel))
            {
                return false;
            }

            points.Add(new ExportPoint(
                pointId,
                retained.MarkerId,
                seriesId,
                phaseId,
                originalPixel,
                point.GraphX,
                point.GraphY,
                retained.ObservationIndex,
                retained.PrintedXValue,
                retained.EstimatedXValue,
                retained.XSource,
                retained.XConfidence,
                retained.YConfidence,
                point.Confidence,
                MapReviewStatus(point.ReviewStatus),
                point.SourceStage,
                point.ModelVersion));
        }

        if (points.Count == 0)
        {
            return false;
        }

        ExportSeries[] overlaidSeries = evidence.Series.Select(series =>
            new ExportSeries(
                series.SeriesId,
                series.Symbol,
                series.DisplayName,
                series.SemanticRole,
                points.Where(point => point.SeriesId == series.SeriesId)
                    .Select(static point => point.PointId),
                series.Confidence,
                series.LegendText)).ToArray();
        if (overlaidSeries.Any(static series => series.PointIds.Count == 0))
        {
            return false;
        }

        request = new ExportRequest(
            workflowRequest.RunId,
            projectId,
            reviewPanel.PanelId,
            workflowRequest.OutputDirectory,
            evidence.Participant,
            evidence.Mode,
            evidence.AuditMode,
            ExportOperation.WriteFiles,
            evidence.Calibration,
            evidence.SessionOriginPolicy,
            exportPhases,
            overlaidSeries,
            points,
            evidence.Relations);
        return true;
    }

    private static bool TryMapOriginalPixel(
        ProductionPanelEvidence panel, double x, double y, out ExportPixelPoint originalPixel)
    {
        originalPixel = default;
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return false;
        }
        if (panel.RasterPanelSource is not { } rasterSource)
        {
            originalPixel = new ExportPixelPoint(x, y);
            return true;
        }
        if (x < 0 || y < 0 || x > panel.Panel.Original.Width || y > panel.Panel.Original.Height)
        {
            return false;
        }
        PdfPointD sourcePixel = rasterSource.MapPanelPixelToSource(new PdfPointD(x, y));
        originalPixel = new ExportPixelPoint(sourcePixel.X, sourcePixel.Y);
        return true;
    }

    private static ExportReviewStatus MapReviewStatus(WorkflowReviewStatus status) => status switch
    {
        WorkflowReviewStatus.Accepted => ExportReviewStatus.Accepted,
        WorkflowReviewStatus.Corrected => ExportReviewStatus.Corrected,
        WorkflowReviewStatus.Rejected => ExportReviewStatus.Rejected,
        _ => ExportReviewStatus.Unreviewed,
    };

    private static WorkflowExportResult RecalibrationRequired() =>
        new(
            succeeded: false,
            warnings:
            [
                "Calibration, phase, series, relation, point, or provenance evidence is incomplete; no scientific values were guessed.",
            ],
            failureCode: ProductionWorkflowFailureCodes.RecalibrationRequired);
}
