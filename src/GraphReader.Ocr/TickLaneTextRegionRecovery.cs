// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

/// <summary>
/// Proposes missing text crops from original pixels aligned with already read
/// axis labels. It never supplies a character, tick value or expected sequence.
/// </summary>
public static class TickLaneTextRegionRecovery
{
    public const string CompositionVersion = "original-pixel-tick-lane-recovery-v3";

    public static async ValueTask<IReadOnlyList<OcrDetectedRegion>> FindAsync(
        OcrImage image,
        IReadOnlyList<OcrDetectedRegion> detected,
        IReadOnlyList<OcrRegion> recognized,
        OcrRectangle plot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.SourceImage != OcrSourceImage.Original ||
            image.OriginalToImage != OcrFrameTransform.Identity ||
            image.CoordinateSpace != OcrContract.CoordinateSpace)
        {
            throw new ArgumentException("Tick recovery requires aligned original pixels.", nameof(image));
        }
        var components = await new ConnectedComponentTextRegionDetector()
            .DetectAsync(image, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<OcrDetectedRegion> established = SelectCandidates(
            components, detected, recognized, plot, cancellationToken);
        var result = established.ToList();
        var byId = detected.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        OcrRegion[] anchors = recognized.Where(region =>
            region.SourceImage == OcrSourceImage.Original && region.ReviewStatus != OcrReviewStatus.Rejected &&
            region.Role is OcrTextRole.XTick or OcrTextRole.YTick &&
            GraphNumericParser.IsLiteralGraphNumber(region.Text) &&
            byId.TryGetValue(region.RegionId, out OcrDetectedRegion? source) &&
            source.Polygon.Points.SequenceEqual(region.Polygon.Points)).ToArray();
        foreach (OcrTextRole role in new[] { OcrTextRole.XTick, OcrTextRole.YTick })
        {
            OcrRegion[] sameAxis = anchors.Where(region => region.Role == role).ToArray();
            // Sparse axes cannot supply the three labels needed by the general
            // lane. Independently measured axis-connected ticks support crops,
            // never a predicted numeric value or a replacement baseline reading.
            if (sameAxis.Length is < 1 or > 2 || CreateLane(sameAxis, role, minimumAnchors: 1) is not { } lane)
                continue;
            double?[] anchorTicks = sameAxis.Select(region => Fits(region.Polygon.Bounds, lane, plot)
                ? UniqueOutwardTick(image, region.Polygon.Bounds, plot, role, cancellationToken) : null).ToArray();
            if (anchorTicks.Any(static position => position is null) || anchorTicks.Distinct().Count() != sameAxis.Length)
                continue;
            double offset = Median(sameAxis.Select((region, index) => anchorTicks[index]!.Value -
                Along(region.Polygon.Bounds, role)));
            if (sameAxis.Where((region, index) => Math.Abs(anchorTicks[index]!.Value -
                    Along(region.Polygon.Bounds, role) - offset) > lane.Height / 2).Any())
                continue;
            foreach (OcrDetectedRegion component in components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OcrRectangle bounds = component.Polygon.Bounds;
                if (!Fits(bounds, lane, plot) || detected.Any(region => Covered(bounds, region.Polygon.Bounds)) ||
                    result.Any(region => Covered(bounds, region.Polygon.Bounds))) continue;
                double? tick = UniqueOutwardTick(image, bounds, plot, role, cancellationToken);
                if (tick is null || anchorTicks.Contains(tick) ||
                    Math.Abs(tick.Value - Along(bounds, role) - offset) > lane.Height / 2) continue;
                result.Add(component with { RegionId = "tick-lane:" + component.RegionId,
                    OrientationDegrees = 0, Context = null });
            }
        }
        return OcrCollections.Freeze(result.OrderBy(static region => region.Polygon.Bounds.Top)
            .ThenBy(static region => region.Polygon.Bounds.Left)
            .ThenBy(static region => region.RegionId, StringComparer.Ordinal));
    }

    public static IReadOnlyList<OcrDetectedRegion> SelectCandidates(
        IReadOnlyList<OcrDetectedRegion> components,
        IReadOnlyList<OcrDetectedRegion> detected,
        IReadOnlyList<OcrRegion> recognized,
        OcrRectangle plot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(recognized);
        if (!plot.IsValid || !double.IsFinite(plot.Right) || !double.IsFinite(plot.Bottom))
        {
            throw new ArgumentException("Tick recovery requires a valid plot.", nameof(plot));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var detectedById = detected.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        OcrRegion[] anchors = recognized.Where(region =>
            region.ReviewStatus != OcrReviewStatus.Rejected &&
            region.Role is OcrTextRole.XTick or OcrTextRole.YTick &&
            GraphNumericParser.IsLiteralGraphNumber(region.Text) &&
            detectedById.TryGetValue(region.RegionId, out OcrDetectedRegion? source) &&
            source.Polygon.Points.SequenceEqual(region.Polygon.Points)).ToArray();
        Lane? horizontal = CreateLane(anchors, OcrTextRole.XTick);
        Lane? vertical = CreateLane(anchors, OcrTextRole.YTick);
        if (horizontal is null && vertical is null)
        {
            return Array.Empty<OcrDetectedRegion>();
        }
        var result = new List<OcrDetectedRegion>();
        foreach (OcrDetectedRegion component in components.OrderBy(static region => region.Polygon.Bounds.Top)
                     .ThenBy(static region => region.Polygon.Bounds.Left)
                     .ThenBy(static region => region.RegionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle bounds = component.Polygon.Bounds;
            if (!bounds.IsValid || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom) ||
                component.CoordinateSpace != OcrContract.CoordinateSpace)
            {
                throw new ArgumentException("Tick components must have valid original geometry.", nameof(components));
            }
            bool inLane = horizontal is not null && Fits(bounds, horizontal, plot) ||
                          vertical is not null && Fits(bounds, vertical, plot);
            if (!inLane || detected.Any(region => Covered(bounds, region.Polygon.Bounds)) ||
                result.Any(region => Covered(bounds, region.Polygon.Bounds)))
            {
                continue;
            }
            result.Add(component with
            {
                RegionId = "tick-lane:" + component.RegionId,
                OrientationDegrees = 0,
                Context = null,
            });
        }
        return OcrCollections.Freeze(result.OrderBy(static region => region.Polygon.Bounds.Top)
            .ThenBy(static region => region.Polygon.Bounds.Left)
            .ThenBy(static region => region.RegionId, StringComparer.Ordinal));
    }

    private static Lane? CreateLane(OcrRegion[] anchors, OcrTextRole role, int minimumAnchors = 3)
    {
        OcrRectangle[] bounds = anchors.Where(region => region.Role == role)
            .Select(static region => region.Polygon.Bounds).ToArray();
        if (bounds.Length < minimumAnchors)
        {
            return null;
        }
        double height = Median(bounds.Select(static rectangle => rectangle.Height));
        double cross = Median(bounds.Select(rectangle => role == OcrTextRole.XTick ? rectangle.Center.Y : rectangle.Right));
        double? alignedCross = bounds.All(rectangle => Math.Abs(
            (role == OcrTextRole.XTick ? rectangle.Center.Y : rectangle.Right) - cross) <= height / 2)
            ? cross : null;
        // Y labels can align either edge. Different digit counts do not move
        // their leading edge on a left-aligned axis. Measure both possibilities
        // from the existing readings without supplying a missing tick value.
        double leadingEdge = Median(bounds.Select(static rectangle => rectangle.Left));
        double? alignedLeadingEdge = role == OcrTextRole.YTick &&
            bounds.All(rectangle => Math.Abs(rectangle.Left - leadingEdge) <= height / 2)
            ? leadingEdge : null;
        double[] along = bounds.Select(rectangle => role == OcrTextRole.XTick ? rectangle.Center.X : rectangle.Center.Y)
            .Distinct().Order().ToArray();
        if (along.Length < Math.Min(bounds.Length, 3) ||
            along.Length > 1 && along[^1] - along[0] < 4 * height ||
            alignedCross is null && alignedLeadingEdge is null)
        {
            return null;
        }
        return new Lane(role, height, bounds.Max(static rectangle => rectangle.Width), alignedCross, alignedLeadingEdge);
    }

    private static double Along(OcrRectangle bounds, OcrTextRole role) =>
        role == OcrTextRole.XTick ? bounds.Center.X : bounds.Center.Y;

    private static double? UniqueOutwardTick(OcrImage image, OcrRectangle box, OcrRectangle plot,
        OcrTextRole role, CancellationToken cancellationToken)
    {
        bool vertical = role == OcrTextRole.YTick;
        double cross = vertical ? plot.Left : plot.Bottom;
        int alongLimit = vertical ? image.Height : image.Width;
        int crossLimit = vertical ? image.Width : image.Height;
        if (cross < 0 || cross >= crossLimit) return null;
        int axis = (int)Math.Round(cross);
        if (axis >= crossLimit) return null;
        double center = Along(box, role);
        int first = (int)Math.Clamp(Math.Floor(center - box.Height), 0, alongLimit - 1);
        int last = (int)Math.Clamp(Math.Ceiling(center + box.Height), 0, alongLimit - 1);
        int low = (int)Math.Clamp(Math.Ceiling(vertical ? Math.Max(cross - box.Height, box.Right + 2) : cross + 2), 0, crossLimit);
        int high = (int)Math.Clamp(Math.Floor(vertical ? cross - 2 : Math.Min(cross + box.Height, box.Top - 2)), -1, crossLimit - 1);
        if (high - low < 1) return null;
        var candidates = new List<double>();
        int groupStart = -1;
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        for (int along = first; along <= last + 1; along++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int support = 0;
            bool connected = along <= last;
            if (connected)
            {
                // Require a continuous connection to the actual axis as well
                // as two outward pixels separated from the label glyph.
                int near = vertical ? high : low;
                for (int offset = Math.Min(axis, near); offset <= Math.Max(axis, near); offset++)
                    if (pixels[(vertical ? along : offset) * image.Stride + (vertical ? offset : along)] > 196)
                        connected = false;
                for (int offset = low; offset <= high; offset++)
                    if (pixels[(vertical ? along : offset) * image.Stride + (vertical ? offset : along)] <= 196)
                        support++;
            }
            if (connected && support >= 2)
            {
                if (groupStart < 0) groupStart = along;
            }
            else if (groupStart >= 0)
            {
                if (along - groupStart <= Math.Max(2, box.Height / 2))
                    candidates.Add((groupStart + along - 1) / 2d);
                groupStart = -1;
            }
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool Fits(OcrRectangle bounds, Lane lane, OcrRectangle plot)
    {
        if (bounds.Height < lane.Height / 2 || bounds.Height > lane.Height * 2 ||
            bounds.Width > 2 * Math.Max(lane.Height, lane.MaximumWidth))
        {
            return false;
        }
        if (lane.Role == OcrTextRole.XTick)
        {
            double tolerance = Math.Max(4, plot.Width * 0.05);
            return bounds.Top > plot.Bottom && bounds.Center.X >= plot.Left - tolerance &&
                bounds.Center.X <= plot.Right + tolerance && lane.Cross is { } cross &&
                Math.Abs(bounds.Center.Y - cross) <= lane.Height / 2;
        }
        double verticalTolerance = Math.Max(4, plot.Height * 0.05);
        return bounds.Right < plot.Left && bounds.Center.Y >= plot.Top - verticalTolerance &&
            bounds.Center.Y <= plot.Bottom + verticalTolerance &&
            (lane.Cross is { } trailing && Math.Abs(bounds.Right - trailing) <= lane.Height / 2 ||
             lane.LeadingEdge is { } leading && Math.Abs(bounds.Left - leading) <= lane.Height / 2);
    }

    private static bool Covered(OcrRectangle a, OcrRectangle b)
    {
        double overlap = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
                         Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return overlap >= 0.5 * Math.Min(a.Width * a.Height, b.Width * b.Height);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    private sealed record Lane(OcrTextRole Role, double Height, double MaximumWidth,
        double? Cross, double? LeadingEdge);
}
