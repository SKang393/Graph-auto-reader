// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

/// <summary>Supplies post-OCR legend context from an original-pixel frame and separate symbol.</summary>
public static class FramedLegendRoleResolver
{
    public const string CompositionVersion = "original-pixel-framed-legend-context-v1";

    public static FramedLegendRoleResolution Resolve(
        OcrImage image,
        IReadOnlyList<OcrRegion> regions,
        IReadOnlyList<OcrDetectedRegion> detectedRegions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(detectedRegions);
        cancellationToken.ThrowIfCancellationRequested();
        if (image.SourceImage != OcrSourceImage.Original || image.OriginalToImage != OcrFrameTransform.Identity ||
            image.CoordinateSpace != OcrContract.CoordinateSpace || image.Width <= 0 || image.Height <= 0 ||
            image.Stride < image.Width || image.Pixels.Length < (long)image.Stride * image.Height)
        {
            throw new ArgumentException("Legend context requires aligned original Gray8 pixels.", nameof(image));
        }
        var detections = detectedRegions.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        var eligible = new List<OcrRegion>();
        foreach (OcrRegion region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle box = region.Polygon.Bounds;
            if (!box.IsValid || box.Left < 0 || box.Top < 0 || box.Right > image.Width || box.Bottom > image.Height ||
                region.CoordinateSpace != OcrContract.CoordinateSpace || !detections.TryGetValue(region.RegionId, out OcrDetectedRegion? detected))
            {
                throw new ArgumentException("Recognized regions must retain valid original detection geometry.", nameof(regions));
            }
            OcrRegionContext? context = detected.Context;
            if (region.Role is not (OcrTextRole.Annotation or OcrTextRole.Other) ||
                region.ReviewStatus != OcrReviewStatus.Unreviewed || string.IsNullOrWhiteSpace(region.Text) ||
                GraphNumericParser.IsLiteralGraphNumber(region.Text) ||
                GraphTextRoleClassifier.GetOrientation(detected.OrientationDegrees) != OcrOrientation.Horizontal ||
                context?.ExplicitRoleHint is not null || context?.NearAnnotationArrow is true ||
                context?.NearPhaseDivider is true || context?.NumericExpected is true ||
                context?.AxisTitleExpected is true || context?.InParticipantBand is true)
            {
                continue;
            }
            eligible.Add(region);
        }
        if (eligible.Count == 0)
        {
            return new(OcrCollections.Freeze(regions), Array.Empty<FramedLegendRoleEvidence>());
        }

        bool[] ink = CreateInkMask(image, cancellationToken);
        List<HorizontalRun> runs = FindRuns(ink, image.Width, image.Height, cancellationToken);
        var evidence = new List<FramedLegendRoleEvidence>();
        foreach (OcrRegion region in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FramedLegendRoleEvidence? item = FindContext(ink, image.Width, runs, region, cancellationToken);
            if (item is not null)
            {
                evidence.Add(item);
            }
        }
        HashSet<string> changed = evidence.Select(static item => item.RegionId).ToHashSet(StringComparer.Ordinal);
        return new(
            OcrCollections.Freeze(regions.Select(region => changed.Contains(region.RegionId)
                ? region with { Role = OcrTextRole.LegendText, Confidence = Math.Min(region.Confidence, 0.70) }
                : region)),
            OcrCollections.Freeze(evidence));
    }

    private static bool[] CreateInkMask(OcrImage image, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        long sum = 0;
        for (int y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++) sum += pixels[y * image.Stride + x];
        }
        // Same full-image foreground rule as the existing component detector.
        int threshold = Math.Clamp((int)Math.Round(sum / ((double)image.Width * image.Height) * 0.80), 32, 224);
        var ink = new bool[checked(image.Width * image.Height)];
        for (int y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++) ink[y * image.Width + x] = pixels[y * image.Stride + x] <= threshold;
        }
        return ink;
    }

    private static List<HorizontalRun> FindRuns(bool[] ink, int width, int height, CancellationToken cancellationToken)
    {
        var runs = new List<HorizontalRun>();
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = -1, last = -1, count = 0;
            for (int x = 0; x <= width; x++)
            {
                bool foreground = x < width && ink[y * width + x];
                if (start >= 0 && (x == width || (foreground && x - last > 3)))
                {
                    if (count >= 8) runs.Add(new(start, y, last + 1, count / (double)(last - start + 1)));
                    start = -1;
                    count = 0;
                }
                if (!foreground) continue;
                if (start < 0) start = x;
                last = x;
                count++;
            }
        }
        return runs;
    }

    private static FramedLegendRoleEvidence? FindContext(
        bool[] ink, int width, List<HorizontalRun> runs, OcrRegion region, CancellationToken cancellationToken)
    {
        OcrRectangle box = region.Polygon.Bounds;
        double height = box.Height;
        HorizontalRun[] candidates = runs.Where(run =>
            run.Left >= box.Left - 5 * height && run.Left < box.Left &&
            run.Right >= box.Right && run.Right <= box.Right + 2 * height &&
            run.Right - run.Left >= box.Width + height && run.Density >= 0.9).ToArray();
        HorizontalRun[] above = candidates.Where(run => run.Y >= box.Top - 4 * height && run.Y < box.Top)
            .OrderByDescending(static run => run.Y).ThenBy(static run => run.Left).ToArray();
        HorizontalRun[] below = candidates.Where(run => run.Y >= box.Bottom && run.Y <= box.Bottom + 4 * height)
            .OrderBy(static run => run.Y).ThenBy(static run => run.Left).ToArray();
        foreach (HorizontalRun upper in above)
        foreach (HorizontalRun lower in below)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Math.Abs(upper.Left - lower.Left) > 2 || Math.Abs(upper.Right - lower.Right) > 2) continue;
            int left = Math.Min(upper.Left, lower.Left), right = Math.Max(upper.Right, lower.Right) - 1;
            if (!VerticalEdge(left, upper.Y, lower.Y) || !VerticalEdge(right, upper.Y, lower.Y)) continue;
            var frame = new OcrRectangle(left, upper.Y, right - left + 1, lower.Y - upper.Y + 1);
            OcrRectangle? glyph = FindSymbol(ink, width, frame, box, cancellationToken);
            if (glyph.HasValue) return new(region.RegionId, frame, glyph.Value);
        }
        return null;

        bool VerticalEdge(int x, int top, int bottom)
        {
            int supported = 0;
            for (int y = top; y <= bottom; y++)
            {
                bool found = false;
                for (int xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++) found |= ink[y * width + xx];
                if (found) supported++;
            }
            return supported / (double)(bottom - top + 1) >= 0.9;
        }
    }

    private static OcrRectangle? FindSymbol(
        bool[] ink, int width, OcrRectangle frame, OcrRectangle text, CancellationToken cancellationToken)
    {
        int left = Math.Max((int)frame.Left + 2, (int)Math.Floor(text.Left - 4 * text.Height));
        int right = (int)Math.Floor(text.Left - Math.Max(2, 0.15 * text.Height));
        int top = Math.Max((int)frame.Top + 2, (int)Math.Floor(text.Top - text.Height));
        int bottom = Math.Min((int)frame.Bottom - 1, (int)Math.Ceiling(text.Bottom + text.Height));
        if (right <= left || bottom <= top) return null;
        int localWidth = right - left, localHeight = bottom - top;
        var visited = new bool[checked(localWidth * localHeight)];
        var queue = new Queue<int>();
        OcrRectangle? found = null;
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = (y - top) * localWidth + x - left;
            if (visited[index] || !ink[y * width + x]) continue;
            visited[index] = true;
            queue.Enqueue(index);
            int x0 = x, x1 = x + 1, y0 = y, y1 = y + 1;
            while (queue.TryDequeue(out int current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int cx = current % localWidth, cy = current / localWidth;
                x0 = Math.Min(x0, cx + left); x1 = Math.Max(x1, cx + left + 1);
                y0 = Math.Min(y0, cy + top); y1 = Math.Max(y1, cy + top + 1);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= localWidth || ny >= localHeight) continue;
                    int next = ny * localWidth + nx;
                    if (visited[next] || !ink[(ny + top) * width + nx + left]) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }
            if (x0 <= left || x1 >= right || y0 <= top || y1 >= bottom) continue;
            double w = x1 - x0, h = y1 - y0;
            if (w < 0.35 * text.Height || w > 2 * text.Height || h < 0.35 * text.Height || h > 2 * text.Height ||
                w / h < 0.5 || w / h > 2 || Math.Abs((y0 + y1 - text.Top - text.Bottom) / 2) > 0.75 * text.Height ||
                text.Left - x1 < 0.15 * text.Height || text.Left - x1 > 2 * text.Height) continue;
            if (found.HasValue) return null;
            found = new(x0, y0, w, h);
        }
        return found;
    }

    private readonly record struct HorizontalRun(int Left, int Y, int Right, double Density);
}

public sealed record FramedLegendRoleEvidence(string RegionId, OcrRectangle FrameBounds, OcrRectangle GlyphBounds);

public sealed record FramedLegendRoleResolution(
    IReadOnlyList<OcrRegion> Regions,
    IReadOnlyList<FramedLegendRoleEvidence> Evidence);
