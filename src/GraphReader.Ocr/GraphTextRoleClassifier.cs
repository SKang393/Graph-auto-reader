// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

public sealed record RoleClassification(
    OcrTextRole Role,
    double Confidence,
    IReadOnlyList<string> Reasons);

public static class GraphTextRoleClassifier
{
    public const string Version = "graph-text-role-classifier-v11-left-axis-crossing-cues";

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

        var bounds = region.Polygon.Bounds;
        var center = bounds.Center;
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
        // A horizontal margin label can extend across the axis. Its box, not
        // only its center, supplies geometry for an existing literal cue.
        var crossesLeftAxis = !context.NumericExpected &&
            bounds.Left < plotBounds.Left && bounds.Right >= plotBounds.Left;
        var leftHeader = (center.X < plotBounds.Left || crossesLeftAxis) && bounds.Bottom <= plotBounds.Top;
        if (!numeric && horizontalText &&
            (plotPeripheral || leftHeader || (crossesLeftAxis && alignedWithPlot)) && HasParticipantLabelCue(recognizedText))
        {
            return Classification(OcrTextRole.Participant, 0.90, leftHeader
                ? "participant_label_and_left_header_geometry"
                : "participant_label_and_peripheral_geometry");
        }

        if (!numeric && horizontalText && alignedWithPlot)
        {
            if ((center.X < plotBounds.Left || crossesLeftAxis) && HasMeasurementTitleCue(recognizedText))
            {
                return Classification(OcrTextRole.AxisTitle, 0.70,
                    "measurement_term_and_axis_margin_requires_review");
            }
            if (center.X < plotBounds.Left)
            {
                return Classification(OcrTextRole.Other, 0.48, "ambiguous_peripheral_text_requires_review");
            }
        }

        var abovePlot = region.Polygon.Bounds.Bottom <= plotBounds.Top + verticalTolerance;
        var rightOfPlot = region.Polygon.Bounds.Left >= plotBounds.Right - horizontalTolerance;
        var insidePlot = center.X >= plotBounds.Left && center.X <= plotBounds.Right &&
            center.Y >= plotBounds.Top && center.Y <= plotBounds.Bottom;
        if (!numeric && abovePlot && withinPlotX && IsPhaseHeadingTerm(recognizedText, region.Polygon.Bounds))
        {
            return Classification(OcrTextRole.PhaseHeading, 0.86, "phase_term_above_plot");
        }

        if (!numeric && insidePlot)
        {
            return Classification(OcrTextRole.Annotation, 0.64, "text_inside_plot");
        }

        if (!numeric && abovePlot && withinPlotX)
        {
            return Classification(OcrTextRole.Other, 0.48, "ambiguous_text_above_plot_requires_review");
        }

        if (!numeric && rightOfPlot && withinPlotY)
        {
            return Classification(OcrTextRole.LegendText, 0.70, "text_right_of_plot");
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

    private static bool HasMeasurementTitleCue(string text)
    {
        // General measurement vocabulary is a reviewable role cue, never a
        // source of calibration numbers or participant identity.
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Any(word => word.Trim('(', ')', '[', ']', ':', ',', '.').ToLowerInvariant() is
            "outcome" or "outcomes" or "frequency" or "count" or "duration" or
            "percentage" or "percent" or "rate" or "score" or "scores");
    }

    internal static bool IsStandalonePhaseCode(string text)
    {
        ReadOnlySpan<char> value = text.AsSpan().Trim();
        const string prefix = "phase";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        value = value[prefix.Length..].Trim();
        if (value.IsEmpty)
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsPhaseHeadingTerm(string text, OcrRectangle bounds)
    {
        var normalized = text.Trim().Replace('_', ' ').Replace('-', ' ');
        string wordsWithoutSpaces = new(normalized.Where(character => !char.IsWhiteSpace(character)).ToArray());
        return normalized.Equals("a", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("b", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("ab", StringComparison.OrdinalIgnoreCase) ||
            IsCompactLetterPhaseCode(normalized, bounds) ||
            EndsWithHeadingTerm(normalized, "baseline") ||
            EndsWithHeadingTerm(normalized, "intervention") ||
            EndsWithHeadingTerm(normalized, "interventions") ||
            EndsWithHeadingTerm(normalized, "treatment") ||
            EndsWithHeadingTerm(normalized, "treatments") ||
            EndsWithHeadingTerm(normalized, "alternating treatments") ||
            EndsWithHeadingTerm(normalized, "withdrawal") ||
            EndsWithHeadingTerm(normalized, "withdrawal continued") ||
            wordsWithoutSpaces.Equals("withdrawalcontinued", StringComparison.OrdinalIgnoreCase) ||
            EndsWithHeadingTerm(normalized, "reintroduction") ||
            EndsWithHeadingTerm(normalized, "reintroduction continued") ||
            wordsWithoutSpaces.Equals("reintroductioncontinued", StringComparison.OrdinalIgnoreCase) ||
            EndsWithHeadingTerm(normalized, "maintenance") ||
            EndsWithHeadingTerm(normalized, "generalization") ||
            EndsWithHeadingTerm(normalized, "followup") ||
            EndsWithHeadingTerm(normalized, "follow up") ||
            IsCriterionHeading(text.Trim()) ||
            normalized.StartsWith("phase", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompactLetterPhaseCode(string text, OcrRectangle bounds)
    {
        if (text.Length == 0 || char.ToLowerInvariant(text[0]) is not ('a' or 'b' or 'm' or 'g'))
        {
            return false;
        }
        for (int index = 1; index < text.Length; index++)
        {
            if (!char.IsAsciiDigit(text[index]))
            {
                return false;
            }
        }

        // Phase reasoning already supports these printed codes. A word-sized
        // detector box recognized as one letter is not sufficient evidence:
        // allow one text-height per glyph and one for detector padding.
        return bounds.Width <= bounds.Height * (text.Length + 1);
    }

    private static bool EndsWithHeadingTerm(string text, string term) =>
        text.EndsWith(term, StringComparison.OrdinalIgnoreCase) &&
        (text.Length == term.Length || char.IsWhiteSpace(text[text.Length - term.Length - 1]));

    private static bool IsCriterionHeading(string text)
    {
        const string term = "criterion";
        if (!text.StartsWith(term, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> suffix = text.AsSpan(term.Length);
        if (suffix.IsEmpty)
        {
            return true;
        }

        if (!char.IsWhiteSpace(suffix[0]))
        {
            return false;
        }

        suffix = suffix.Trim();
        foreach (char character in suffix)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return !suffix.IsEmpty;
    }

    private static RoleClassification Classification(OcrTextRole role, double confidence, string reason) =>
        new(role, confidence, Array.AsReadOnly([reason]));
}
