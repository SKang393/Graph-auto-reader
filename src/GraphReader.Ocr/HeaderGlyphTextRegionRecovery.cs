// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

/// <summary>
/// Finds isolated glyph crops along a corroborated heading row. No heading
/// text, phase code or recognition alternative is supplied by this geometry.
/// </summary>
public static class HeaderGlyphTextRegionRecovery
{
    public const string CompositionVersion = "original-pixel-header-glyph-recovery-v1";

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
            throw new ArgumentException("Header glyph recovery requires aligned original pixels.", nameof(image));
        }
        var detector = new ConnectedComponentTextRegionDetector(
            new ConnectedComponentTextRegionDetectorOptions { MaximumLineGapHeightRatio = 0 });
        var components = await detector.DetectAsync(image, cancellationToken).ConfigureAwait(false);
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
            throw new ArgumentException("Header recovery requires a valid plot.", nameof(plot));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var detectedById = detected.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        OcrRectangle[] headings = recognized.Where(region =>
                region.Role == OcrTextRole.PhaseHeading && region.ReviewStatus != OcrReviewStatus.Rejected &&
                detectedById.TryGetValue(region.RegionId, out OcrDetectedRegion? source) &&
                source.Polygon.Points.SequenceEqual(region.Polygon.Points) &&
                GraphTextRoleClassifier.GetOrientation(source.OrientationDegrees) == OcrOrientation.Horizontal)
            .Select(static region => region.Polygon.Bounds)
            .Where(bounds => bounds.Left >= plot.Left && bounds.Right <= plot.Right &&
                bounds.Top >= 0 && bounds.Bottom <= plot.Top)
            .OrderBy(static bounds => bounds.Top).ThenBy(static bounds => bounds.Left).ToArray();
        OcrRectangle[] band = [];
        foreach (OcrRectangle anchor in headings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle[] aligned = headings.Where(bounds => VerticalOverlap(bounds, anchor) >=
                0.35 * Math.Min(bounds.Height, anchor.Height)).ToArray();
            if (aligned.Length > band.Length || (aligned.Length == band.Length &&
                    aligned.Max(static bounds => bounds.Bottom) > band.Max(static bounds => bounds.Bottom)))
            {
                band = aligned;
            }
        }
        if (band.Length < 3 || band.Select(static bounds => bounds.Center.X).Distinct().Count() < 3)
        {
            return Array.Empty<OcrDetectedRegion>();
        }
        double height = Median(band.Select(static bounds => bounds.Height));
        double centerY = Median(band.Select(static bounds => bounds.Center.Y));
        if (band.Max(static bounds => bounds.Right) - band.Min(static bounds => bounds.Left) < 4 * height)
        {
            return Array.Empty<OcrDetectedRegion>();
        }
        var selected = new List<OcrDetectedRegion>();
        foreach (OcrDetectedRegion component in components.OrderBy(static region => region.Polygon.Bounds.Top)
                     .ThenBy(static region => region.Polygon.Bounds.Left)
                     .ThenBy(static region => region.RegionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle bounds = component.Polygon.Bounds;
            if (!bounds.IsValid || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom) ||
                component.CoordinateSpace != OcrContract.CoordinateSpace)
            {
                throw new ArgumentException("Header components must have valid original geometry.", nameof(components));
            }
            if (bounds.Left < plot.Left || bounds.Right > plot.Right || bounds.Top < 0 || bounds.Bottom > plot.Top ||
                bounds.Height < height / 2 || bounds.Height > height * 2 ||
                bounds.Width < height * 0.3 || bounds.Width > height * 2 ||
                Math.Abs(bounds.Center.Y - centerY) > height / 2 ||
                detected.Any(region => Covered(bounds, region.Polygon.Bounds)) ||
                selected.Any(region => Covered(bounds, region.Polygon.Bounds)) ||
                headings.Any(heading => VerticalOverlap(bounds, heading) >= 0.35 * Math.Min(bounds.Height, heading.Height) &&
                    Math.Max(heading.Left - bounds.Right, bounds.Left - heading.Right) <= height))
            {
                continue;
            }
            // Adjacent suffixes belong to a word-crop repair, not a new label.
            // No role or character hint is inferred for the retained component.
            selected.Add(component with
            {
                RegionId = "header-glyph:" + component.RegionId,
                OrientationDegrees = 0,
                Context = null,
            });
        }
        return OcrCollections.Freeze(selected);
    }

    private static double VerticalOverlap(OcrRectangle a, OcrRectangle b) =>
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));

    private static bool Covered(OcrRectangle a, OcrRectangle b) =>
        VerticalOverlap(a, b) * Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) >=
        0.5 * Math.Min(a.Width * a.Height, b.Width * b.Height);

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}
