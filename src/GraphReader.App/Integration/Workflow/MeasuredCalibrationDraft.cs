// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Axis;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Original tick positions for manual review. No horizontal scale is selected.</summary>
internal sealed record MeasuredCalibrationDraft(WorkflowVisionEnvelope Evidence,
    double LeftX, double RightX, double ZeroY, double UpperY, double UpperValue,
    string LeftReadings, string RightReadings)
{
    internal static MeasuredCalibrationDraft? From(WorkflowVisionEnvelope evidence, AxisGeometryResult axis,
        OcrResult ocr, TickLabelGeometryResult geometry, SessionFirstCalibrationResult calibration)
    {
        if (axis.Uncertainty.NeedsReview || geometry.PrintedXRow is null || !calibration.YTransform.IsValid ||
            calibration.Reasons.Any(reason => reason.StartsWith("ocr_tick_sequence_needs_review:YTick:", StringComparison.Ordinal))) return null;

        // Reuse the manual editor’s screen-alignment rule. Rotated panels need its affine workflow.
        if (!Services.ManualCalibrationMapper.HasLegacyAlignment(axis.PlotPolygon.BottomLeft,
                axis.PlotPolygon.TopLeft, axis.PlotPolygon.BottomRight)) return null;
        var ticks = ocr.Regions.Where(r => r.SourceImage == OcrSourceImage.Original &&
            r.ReviewStatus != OcrReviewStatus.Rejected && geometry.Positions.ContainsKey(r.RegionId) &&
            geometry.AttachedStrokeIds.Contains(r.RegionId)).ToArray();
        var x = ticks.Where(r => r.Role == OcrTextRole.XTick && geometry.PrintedXRow.Positions.ContainsKey(r.RegionId))
            .OrderBy(r => geometry.Positions[r.RegionId]).ToArray();
        var y = ticks.Where(r => r.Role == OcrTextRole.YTick && calibration.YTransform.Diagnostics.InlierIds.Contains(r.RegionId))
            .OrderBy(r => geometry.Positions[r.RegionId]).ToArray();
        if (x.Length < 2 || y.Length < 2 || !GraphNumericParser.IsLiteralGraphNumber(y[^1].Text) ||
            GraphNumericParser.Parse(y[^1].Text).Value != 0 || !GraphNumericParser.IsLiteralGraphNumber(y[0].Text) ||
            GraphNumericParser.Parse(y[0].Text).Value is not double upper || upper <= 0) return null;
        double left = geometry.Positions[x[0].RegionId], right = geometry.Positions[x[^1].RegionId];
        double zero = geometry.Positions[y[^1].RegionId], top = geometry.Positions[y[0].RegionId];
        if (!(right > left && zero > top) || !double.IsFinite(upper)) return null;
        return new(evidence, left, right, zero, top, upper, Readings(x[0]), Readings(x[^1]));
    }

    private static string Readings(OcrRegion region) => string.Join(" / ", new[] { region.Text }
        .Concat(region.Alternatives.Where(a => a.Confidence is > 0 and <= 1 && !string.IsNullOrWhiteSpace(a.Text))
            .Select(a => a.Text)).Distinct(StringComparer.Ordinal));
}
