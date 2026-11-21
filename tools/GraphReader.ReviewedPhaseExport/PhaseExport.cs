// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using GraphReader.Axis;
using GraphReader.Export;

namespace GraphReader.ReviewedPhaseExport;

internal sealed record PhaseTicks(Guid PhaseId, double LeftPixel, double LeftValue, double RightPixel, double RightValue);

// The workspace supplies its ordinary synchronized export snapshot and audit evidence.
// Only this invocation gets a local X calibration and a phase selection.
internal sealed class PhaseExport(PhaseTicks ticks) : IExportService
{
    public Task<ExportResult> ExportAsync(ExportRequest source, CancellationToken cancellationToken)
    {
        if (source.Mode != ExportMode.ObservationOrder || source.Calibration.Status != ExportCalibrationStatus.Valid ||
            !source.Calibration.HasYCalibration || source.SessionOriginPolicy.HasExplicitOverride)
            throw new InvalidDataException("Phase-local ticks require a valid Y calibration and an unresolved whole-panel X scale.");
        var phase = source.Phases.SingleOrDefault(p => p.PhaseId == ticks.PhaseId)
            ?? throw new InvalidDataException("Unknown phase ID in this panel.");
        bool InPhase(double x) => double.IsFinite(x) && x >= phase.OriginalXMinimum && x <= phase.OriginalXMaximum &&
            !(x == phase.OriginalXMaximum && source.Phases.Any(p => p.Order > phase.Order && p.OriginalXMinimum == x));
        if (!double.IsFinite(ticks.LeftValue) || !double.IsFinite(ticks.RightValue) ||
            ticks.LeftValue >= ticks.RightValue || ticks.LeftPixel >= ticks.RightPixel ||
            !InPhase(ticks.LeftPixel) || !InPhase(ticks.RightPixel))
            throw new InvalidDataException("Confirm two increasing X ticks inside the selected phase in original source pixels.");

        var phaseIds = source.Phases.Select(p => p.PhaseId).ToHashSet();
        if (source.Points.Any(p => p.PhaseId is not Guid id || !phaseIds.Contains(id)))
            throw new InvalidDataException("Resolve points with missing phase assignments before scoping the export.");
        double slope = (ticks.RightValue - ticks.LeftValue) / (ticks.RightPixel - ticks.LeftPixel);
        var transform = new LinearAxisTransform(slope, ticks.LeftValue - slope * ticks.LeftPixel);
        // Apply the same finite, invertible slope check used by the ordinary linear axis mapper.
        if (!double.IsFinite(transform.GraphToPixel(ticks.LeftValue)))
            throw new InvalidDataException("The supplied X ticks do not define a finite mapping.");
        var retained = new List<ExportPoint>();
        var outside = new HashSet<Guid>();
        foreach (var point in source.Points)
        {
            if (point.PhaseId != ticks.PhaseId)
            {
                outside.Add(point.PointId);
                continue;
            }
            if (point.ReviewStatus != ExportReviewStatus.Accepted ||
                point.ReviewEvidence is not { PhaseSource: "manual", PhaseLabelReview: "explicit_label" } evidence ||
                !evidence.PointActions.Split('|').Contains("point_accepted"))
                throw new InvalidDataException($"Point {point.PointId:D} requires explicit point and phase evidence.");
            if (!InPhase(point.OriginalPixel.X) || point.PrintedXValue.HasValue || point.XSource == ExportXValueSource.Printed)
                throw new InvalidDataException($"Review phase membership or existing printed X for point {point.PointId:D}.");
            double x = transform.PixelToGraph(point.OriginalPixel.X);
            if (!double.IsFinite(x))
                throw new InvalidDataException("The supplied X ticks do not define a finite increasing mapping.");
            retained.Add(point with { GraphX = x, EstimatedXValue = x, XSource = ExportXValueSource.Estimated, XConfidence = 0 });
        }
        if (retained.Count == 0) throw new InvalidDataException("The selected phase has no placed points.");
        // Remove only known out-of-scope members. Dangling references still reach the normal validator.
        var series = source.Series.Select(s => new ExportSeries(s.SeriesId, s.Symbol, s.DisplayName, s.SemanticRole,
            s.PointIds.Where(id => !outside.Contains(id)), s.Confidence, s.LegendText));
        var request = new ExportRequest(source.RunId, source.ProjectId, source.PanelId, source.OutputDirectory, source.Participant,
            ExportMode.PrintedSession, source.AuditMode, source.Operation, new ExportCalibration(source.Calibration.Status,
                true, true, false, null, source.Calibration.Confidence), source.SessionOriginPolicy,
            source.Phases, series, retained, source.Relations, source.SelectedInterventionSeriesIds, source.ContractVersion);
        return new ExportService().ExportAsync(request, cancellationToken);
    }
}
