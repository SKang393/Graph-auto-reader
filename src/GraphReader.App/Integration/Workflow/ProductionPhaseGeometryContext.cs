// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Axis;
using GraphReader.Ocr;
using GraphReader.Phases;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Resolves measured lines using original non-text, non-marker pixels and heading layout.</summary>
internal static class ProductionPhaseGeometryContext
{
    internal const string Version = "original-pixel-phase-geometry-context-v2";
    private static readonly AxisGeometryOptions GeometryOptions = new();
    private static readonly PhaseReasoningOptions PhaseOptions = new();
    // Same row-overlap rule used by HeaderLayoutRoleResolver.
    private const double MinimumVerticalOverlapRatio = 0.35;

    // Axis geometry has already fitted and classified these lines by angle.
    // The raw-segment phase default (2 px) must not discard the same fitted
    // divider merely because a taller original image produces more pixel drift.
    internal static PhaseReasoningOptions CreateReasoningOptions(PhaseRectangle plot) => PhaseOptions with
    {
        MaximumVerticalDriftPixels = Math.Max(PhaseOptions.MaximumVerticalDriftPixels,
            plot.Height * Math.Tan(GeometryOptions.MaximumAxisDeviationDegrees * Math.PI / 180)),
    };

    internal static PhaseGeometryContextResult Resolve(
        AxisGeometryResult axis, OcrImage original, IReadOnlyList<OcrRegion> regions,
        IReadOnlyList<OcrRectangle> legendFrames, IReadOnlyList<OcrRectangle> markerBounds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (axis.CoordinateSpace != OcrContract.CoordinateSpace ||
            original.SourceImage != OcrSourceImage.Original || original.OriginalToImage != OcrFrameTransform.Identity ||
            original.CoordinateSpace != OcrContract.CoordinateSpace ||
            original.Width <= 0 || original.Height <= 0 || original.Stride < original.Width ||
            original.Pixels.Length < checked(original.Stride * original.Height) ||
            regions.Any(static region => region.CoordinateSpace != OcrContract.CoordinateSpace || !region.Polygon.Bounds.IsValid) ||
            legendFrames.Concat(markerBounds).Any(static frame => !frame.IsValid))
            throw new ArgumentException("Phase context requires aligned original-pixel evidence.", nameof(original));
        var plot = new OcrRectangle(axis.PlotPolygon.Points.Min(static point => point.X),
            axis.PlotPolygon.Points.Min(static point => point.Y),
            axis.PlotPolygon.Points.Max(static point => point.X) - axis.PlotPolygon.Points.Min(static point => point.X),
            axis.PlotPolygon.Points.Max(static point => point.Y) - axis.PlotPolygon.Points.Min(static point => point.Y));
        if (!plot.IsValid || axis.PhaseDividers.Select(static item => item.Line)
            .Concat(axis.AmbiguousGridOrDividers.Select(static item => item.Line)).Any(static line =>
                !line.Start.IsFinite || !line.End.IsFinite || line.Start.Y == line.End.Y))
            throw new ArgumentException("Phase context requires finite vertical geometry.", nameof(axis));
        var warnings = new List<string>();
        int foregroundThreshold = GetForegroundThreshold(original, cancellationToken);
        OcrRectangle[] excluded = legendFrames.Concat(markerBounds).Concat(regions.Where(static region =>
            region.ReviewStatus != OcrReviewStatus.Rejected && !string.IsNullOrWhiteSpace(region.Text))
            .Select(static region => region.Polygon.Bounds)).ToArray();
        var dividers = axis.PhaseDividers.Where(divider => HasDividerSupport(
            divider.DividerId, divider.Line)).ToList();
        AmbiguousGridOrDividerGeometry[] ambiguous = axis.AmbiguousGridOrDividers
            .Where(item => HasDividerSupport(item.AmbiguityId, item.Line)).ToArray();
        if (ambiguous.Length > 0)
        {
            double[] boundaries = dividers.Where(static item => item.Style != DividerStyle.Unknown)
                .Select(static item => item.Line.Midpoint.X)
                .Concat(ambiguous.Select(static item => item.Line.Midpoint.X))
                .Prepend(plot.Left).Append(plot.Right).Distinct().Order().ToArray();
            OcrRegion[] band = FindCorroboratingHeadingBand(regions, boundaries, plot, cancellationToken);
            foreach (AmbiguousGridOrDividerGeometry item in ambiguous)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (band.Length > 0 && item.GeometryConfidence >= PhaseOptions.MinimumConfidence)
                {
                    dividers.Add(new PhaseDividerGeometry(item.AmbiguityId, item.Line, DividerStyle.Solid,
                        Math.Min(item.GeometryConfidence, band.Min(static region => region.Confidence)),
                        item.PlotSpanFraction, item.CoverageFraction, item.SupportingCandidateIds));
                    warnings.Add($"phase_divider_corroborated_by_heading_layout:{item.AmbiguityId}:" +
                        string.Join(',', band.Select(static region => region.RegionId).Order(StringComparer.Ordinal)));
                }
                else warnings.Add($"phase_grid_ambiguity_requires_review:{item.AmbiguityId}");
            }
        }
        return new(Array.AsReadOnly(dividers.OrderBy(static item => item.Line.Midpoint.X).ToArray()),
            Array.AsReadOnly(warnings.ToArray()));

        bool HasDividerSupport(string id, GeometryLineSegment line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Math.Abs(line.End.X - line.Start.X) > Math.Abs(line.End.Y - line.Start.Y) *
                Math.Tan(GeometryOptions.MaximumAxisDeviationDegrees * Math.PI / 180))
            {
                warnings.Add($"phase_line_excluded_by_axis_angle:{id}");
                return false;
            }
            if (HasUnobscuredInkSpan(original, line, plot, excluded, foregroundThreshold, cancellationToken)) return true;
            double tolerance = GeometryOptions.MergeDistancePixels;
            bool crossesLegend = legendFrames.Any(frame => XAtY(line, frame.Center.Y) >= frame.Left - tolerance &&
                    XAtY(line, frame.Center.Y) <= frame.Right + tolerance &&
                    frame.Bottom >= plot.Top && frame.Top <= plot.Bottom);
            warnings.Add(crossesLegend ? $"phase_line_excluded_by_legend_context:{id}" :
                $"phase_line_excluded_by_pixel_context:{id}");
            return false;
        }
    }

    private static OcrRegion[] FindCorroboratingHeadingBand(
        IReadOnlyList<OcrRegion> regions, double[] boundaries, OcrRectangle plot, CancellationToken cancellationToken)
    {
        OcrRegion[] eligible = regions.Where(region =>
            region.Role is OcrTextRole.PhaseHeading or OcrTextRole.Other &&
            region.ReviewStatus != OcrReviewStatus.Rejected && region.Confidence >= PhaseOptions.MinimumConfidence &&
            !string.IsNullOrWhiteSpace(region.Text) && !GraphNumericParser.IsLiteralGraphNumber(region.Text) &&
            region.Polygon.Bounds.Bottom <= plot.Top &&
            plot.Top - region.Polygon.Bounds.Bottom <= PhaseOptions.MaximumHeadingDistancePixels).ToArray();
        foreach (OcrRegion anchor in eligible.Where(static region => region.Role == OcrTextRole.PhaseHeading)
            .OrderByDescending(static region => region.Polygon.Bounds.Bottom)
            .ThenBy(static region => region.RegionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle reference = anchor.Polygon.Bounds;
            OcrRegion[] band = eligible.Where(region =>
            {
                OcrRectangle box = region.Polygon.Bounds;
                return Math.Min(reference.Bottom, box.Bottom) - Math.Max(reference.Top, box.Top) >=
                    MinimumVerticalOverlapRatio * Math.Min(reference.Height, box.Height);
            }).ToArray();
            int phaseLabeledIntervals = 0;
            bool everyIntervalLabeled = true;
            var supporting = new List<OcrRegion>();
            for (int i = 0; i < boundaries.Length - 1; i++)
            {
                OcrRegion[] labels = band.Where(region => region.Polygon.Bounds.Left >= boundaries[i] &&
                    region.Polygon.Bounds.Right <= boundaries[i + 1]).ToArray();
                if (labels.Length == 0) { everyIntervalLabeled = false; break; }
                if (labels.Any(static region => region.Role == OcrTextRole.PhaseHeading)) phaseLabeledIntervals++;
                supporting.AddRange(labels);
            }
            if (everyIntervalLabeled && phaseLabeledIntervals >= 2) return supporting.ToArray();
        }
        return [];
    }

    private static int GetForegroundThreshold(OcrImage image, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        long sum = 0;
        for (int y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++) sum += pixels[y * image.Stride + x];
        }
        // Same foreground rule as FramedLegendRoleResolver and the component detector.
        return Math.Clamp((int)Math.Round(sum / ((double)image.Width * image.Height) * 0.80), 32, 224);
    }

    private static bool HasUnobscuredInkSpan(
        OcrImage image, GeometryLineSegment line, OcrRectangle plot, IReadOnlyList<OcrRectangle> excluded,
        int threshold, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        double tolerance = GeometryOptions.MergeDistancePixels;
        int first = int.MaxValue, last = -1, supported = 0;
        int top = Math.Max(0, (int)Math.Ceiling(plot.Top + tolerance));
        int bottom = Math.Min(image.Height - 1, (int)Math.Floor(plot.Bottom - tolerance));
        for (int y = top; y <= bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double center = XAtY(line, y);
            int left = Math.Max(0, (int)Math.Floor(center - tolerance));
            int right = Math.Min(image.Width - 1, (int)Math.Ceiling(center + tolerance));
            for (int x = left; x <= right; x++)
            {
                if (pixels[y * image.Stride + x] > threshold || excluded.Any(box =>
                    x >= box.Left - tolerance && x <= box.Right + tolerance &&
                    y >= box.Top - tolerance && y <= box.Bottom + tolerance)) continue;
                first = Math.Min(first, y); last = y; supported++; break;
            }
        }
        return last >= first && (last - first + 1) / plot.Height >= GeometryOptions.DividerMinimumSpanFraction &&
            supported / plot.Height >= GeometryOptions.DividerMinimumCoverageFraction;
    }

    private static double XAtY(GeometryLineSegment line, double y) => line.Start.X +
        (line.End.X - line.Start.X) * (y - line.Start.Y) / (line.End.Y - line.Start.Y);
}

internal sealed record PhaseGeometryContextResult(
    IReadOnlyList<PhaseDividerGeometry> Dividers, IReadOnlyList<string> Warnings);
