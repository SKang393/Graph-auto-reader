// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

public sealed record RoleClassification(
    OcrTextRole Role,
    double Confidence,
    IReadOnlyList<string> Reasons);

public static class GraphTextRoleClassifier
{
    internal const string Version = "graph-text-role-classifier-v2";

    private const string ParticipantLabelPrefix = "Participant ";

    public static RoleClassification Classify(
        OcrDetectedRegion region,
        string recognizedText,
        OcrRectangle plotBounds)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(recognizedText);
        if (!plotBounds.IsValid)
        {
            throw new ArgumentException("Plot bounds must be finite and have positive dimensions.", nameof(plotBounds));
        }

        var context = region.Context ?? new OcrRegionContext();
        if (context.ExplicitRoleHint is { } explicitRole)
        {
            return Classification(explicitRole, 0.99, "explicit_role_hint");
        }

        if (context.InParticipantBand)
        {
            return Classification(OcrTextRole.Participant, 0.96, "participant_band_geometry");
        }

        if (context.NearLegendGlyph)
        {
            return Classification(OcrTextRole.LegendText, 0.96, "near_legend_glyph");
        }

        if (context.NearPhaseDivider)
        {
            return Classification(OcrTextRole.PhaseHeading, 0.93, "near_phase_divider");
        }

        if (context.NearAnnotationArrow)
        {
            return Classification(OcrTextRole.Annotation, 0.95, "annotation_context");
        }

        var center = region.Polygon.Bounds.Center;
        var numeric = GraphNumericParser.IsLiteralGraphNumber(recognizedText);
        var horizontalTolerance = Math.Max(4, plotBounds.Width * 0.05);
        var verticalTolerance = Math.Max(4, plotBounds.Height * 0.05);
        var withinPlotX = center.X >= plotBounds.Left - horizontalTolerance &&
            center.X <= plotBounds.Right + horizontalTolerance;
        var withinPlotY = center.Y >= plotBounds.Top - verticalTolerance &&
            center.Y <= plotBounds.Bottom + verticalTolerance;

        if ((numeric || context.NumericExpected) && center.Y > plotBounds.Bottom && withinPlotX)
        {
            return Classification(OcrTextRole.XTick, numeric ? 0.94 : 0.72, "numeric_below_plot");
        }

        if ((numeric || context.NumericExpected) && center.X < plotBounds.Left && withinPlotY)
        {
            return Classification(OcrTextRole.YTick, numeric ? 0.94 : 0.72, "numeric_left_of_plot");
        }

        var verticalText = IsVertical(region.OrientationDegrees);
        if (context.AxisTitleExpected || (verticalText && center.X < plotBounds.Left))
        {
            return Classification(OcrTextRole.AxisTitle, 0.90, "axis_title_orientation_and_position");
        }

        var horizontalText = GetOrientation(region.OrientationDegrees) == OcrOrientation.Horizontal;
        var alignedWithPlot = center.Y >= plotBounds.Top && center.Y <= plotBounds.Bottom;
        var plotPeripheral = (center.X < plotBounds.Left || center.X > plotBounds.Right) && alignedWithPlot;
        if (!numeric && horizontalText && plotPeripheral && HasParticipantLabelCue(recognizedText))
        {
            return Classification(OcrTextRole.Participant, 0.90, "participant_label_and_peripheral_geometry");
        }

        if (!numeric && horizontalText && center.X < plotBounds.Left && alignedWithPlot)
        {
            return Classification(OcrTextRole.Other, 0.48, "ambiguous_peripheral_text_requires_review");
        }

        var abovePlot = region.Polygon.Bounds.Bottom <= plotBounds.Top + verticalTolerance;
        var rightOfPlot = region.Polygon.Bounds.Left >= plotBounds.Right - horizontalTolerance;
        var insidePlot = center.X >= plotBounds.Left && center.X <= plotBounds.Right &&
            center.Y >= plotBounds.Top && center.Y <= plotBounds.Bottom;
        if (!numeric && abovePlot && withinPlotX && IsPhaseHeadingTerm(recognizedText))
        {
            return Classification(OcrTextRole.PhaseHeading, 0.86, "phase_term_above_plot");
        }

        if (!numeric && abovePlot && withinPlotX)
        {
            return Classification(OcrTextRole.Other, 0.48, "ambiguous_text_above_plot_requires_review");
        }

        if (!numeric && rightOfPlot && withinPlotY)
        {
            return Classification(OcrTextRole.LegendText, 0.70, "text_right_of_plot");
        }

        if (!numeric && insidePlot)
        {
            return Classification(OcrTextRole.Annotation, 0.64, "text_inside_plot");
        }

        if (!numeric && center.Y > plotBounds.Bottom && withinPlotX)
        {
            return Classification(OcrTextRole.AxisTitle, 0.76, "text_below_plot");
        }

        return Classification(OcrTextRole.Other, 0.55, "no_role_geometry_match");
    }

    public static OcrOrientation GetOrientation(double orientationDegrees)
    {
        if (!double.IsFinite(orientationDegrees))
        {
            return OcrOrientation.Arbitrary;
        }

        var normalized = ((orientationDegrees % 360) + 360) % 360;
        if (normalized <= 20 || normalized >= 340 || Math.Abs(normalized - 180) <= 20)
        {
            return OcrOrientation.Horizontal;
        }

        if (Math.Abs(normalized - 90) <= 20)
        {
            return OcrOrientation.RotatedClockwise;
        }

        if (Math.Abs(normalized - 270) <= 20)
        {
            return OcrOrientation.RotatedCounterClockwise;
        }

        return OcrOrientation.Arbitrary;
    }

    private static bool IsVertical(double orientationDegrees) =>
        GetOrientation(orientationDegrees) is
            OcrOrientation.RotatedClockwise or OcrOrientation.RotatedCounterClockwise;

    private static bool HasParticipantLabelCue(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length > ParticipantLabelPrefix.Length &&
            trimmed.StartsWith(ParticipantLabelPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPhaseHeadingTerm(string text)
    {
        var normalized = text.Trim().Replace('_', ' ').Replace('-', ' ');
        return normalized.Equals("a", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("b", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("ab", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("baseline", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("intervention", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("treatment", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("maintenance", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("generalization", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("followup", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("follow up", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("phase", StringComparison.OrdinalIgnoreCase);
    }

    private static RoleClassification Classification(OcrTextRole role, double confidence, string reason) =>
        new(role, confidence, Array.AsReadOnly([reason]));
}
