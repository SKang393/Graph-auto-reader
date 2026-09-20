// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

/// <summary>
/// Proposes missing text crops from original pixels aligned with already read
/// axis labels. It never supplies a character, tick value or expected sequence.
/// </summary>
public static class TickLaneTextRegionRecovery
{
    public const string CompositionVersion = "original-pixel-tick-lane-recovery-v1";

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
        return SelectCandidates(components, detected, recognized, plot, cancellationToken);
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

    private static Lane? CreateLane(OcrRegion[] anchors, OcrTextRole role)
    {
        OcrRectangle[] bounds = anchors.Where(region => region.Role == role)
            .Select(static region => region.Polygon.Bounds).ToArray();
        if (bounds.Length < 3)
        {
            return null;
        }
        double height = Median(bounds.Select(static rectangle => rectangle.Height));
        double cross = Median(bounds.Select(rectangle => role == OcrTextRole.XTick ? rectangle.Center.Y : rectangle.Right));
        double[] along = bounds.Select(rectangle => role == OcrTextRole.XTick ? rectangle.Center.X : rectangle.Center.Y)
            .Distinct().Order().ToArray();
        if (along.Length < 3 || along[^1] - along[0] < 4 * height ||
            bounds.Any(rectangle => Math.Abs((role == OcrTextRole.XTick ? rectangle.Center.Y : rectangle.Right) - cross) > height / 2))
        {
            return null;
        }
        return new Lane(role, height, bounds.Max(static rectangle => rectangle.Width), cross);
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
                bounds.Center.X <= plot.Right + tolerance && Math.Abs(bounds.Center.Y - lane.Cross) <= lane.Height / 2;
        }
        double verticalTolerance = Math.Max(4, plot.Height * 0.05);
        return bounds.Right < plot.Left && bounds.Center.Y >= plot.Top - verticalTolerance &&
            bounds.Center.Y <= plot.Bottom + verticalTolerance && Math.Abs(bounds.Right - lane.Cross) <= lane.Height / 2;
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

    private sealed record Lane(OcrTextRole Role, double Height, double MaximumWidth, double Cross);
}
