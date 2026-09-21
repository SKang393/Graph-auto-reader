// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Measures enclosed original-pixel interiors for classifier review.</summary>
internal static class ProductionMarkerEnclosedCenterRecovery
{
    internal const string Version = "original-pixel-enclosed-center-v1";
    private const float InkThreshold = 0.12f;
    private const int MaximumInteriorDimension = 23;

    internal static IReadOnlyList<MarkerCenter> Find(
        OcrImage image, MarkerPolygon plot, IReadOnlyList<OcrRegion> text,
        IReadOnlyList<ClassifiedMarker> accepted, IReadOnlyList<OcrRectangle> legendFrames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(plot);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(legendFrames);
        cancellationToken.ThrowIfCancellationRequested();
        if (image.SourceImage != OcrSourceImage.Original || image.OriginalToImage != OcrFrameTransform.Identity ||
            image.CoordinateSpace != OcrContract.CoordinateSpace || image.Width <= 0 || image.Height <= 0 ||
            image.Stride < image.Width || image.Pixels.Length != checked(image.Stride * image.Height))
            throw new ArgumentException("Enclosed-center recovery requires aligned original grayscale pixels.", nameof(image));

        int width = image.Width, height = image.Height;
        int count = checked(width * height);
        var visited = new bool[count];
        var queue = new int[count];
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        var result = new List<MarkerCenter>();
        for (int start = 0; start < count; start++)
        {
            if (start % width == 0) cancellationToken.ThrowIfCancellationRequested();
            if (visited[start] || 1 - pixels[start / width * image.Stride + start % width] / 255f >= InkThreshold) continue;
            int head = 0, tail = 1;
            queue[0] = start;
            visited[start] = true;
            int minX = start % width, maxX = minX, minY = start / width, maxY = minY;
            bool border = false;
            while (head < tail)
            {
                if ((head & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                int point = queue[head++], x = point % width, y = point / width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                border |= x == 0 || x == width - 1 || y == 0 || y == height - 1;
                // Four-connected background complements the existing eight-connected ink rule.
                for (int direction = 0; direction < 4; direction++)
                {
                    int xx = direction switch { 0 => x - 1, 1 => x + 1, _ => x };
                    int yy = direction switch { 2 => y - 1, 3 => y + 1, _ => y };
                    if (xx < 0 || xx >= width || yy < 0 || yy >= height) continue;
                    int next = yy * width + xx;
                    if (!visited[next] && 1 - pixels[yy * image.Stride + xx] / 255f < InkThreshold)
                    {
                        visited[next] = true;
                        queue[tail++] = next;
                    }
                }
            }
            int interiorWidth = maxX - minX + 1, interiorHeight = maxY - minY + 1;
            if (border || interiorWidth < 2 || interiorHeight < 2 ||
                interiorWidth > MaximumInteriorDimension || interiorHeight > MaximumInteriorDimension) continue;
            var center = new MarkerPoint((minX + maxX) / 2.0, (minY + maxY) / 2.0);
            if (!plot.Contains(center) || legendFrames.Any(box => Contains(box, center)) ||
                text.Any(region => Contains(region.Polygon.Bounds, center))) continue;
            double radius = (Math.Max(interiorWidth, interiorHeight) + 2) / 2.0;
            if (accepted.Any(marker => Distance(marker.Marker.Center, center) <= Math.Max(2, radius))) continue;
            // This is measured geometric support, not a calibrated detector probability.
            result.Add(new MarkerCenter($"{Version}:{start}", center, radius, 0, 0.5,
                MarkerSourceImage.Original, ReviewState: MarkerReviewState.NeedsReview));
        }
        return result.AsReadOnly();
    }

    private static double Distance(MarkerPoint a, MarkerPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static bool Contains(OcrRectangle box, MarkerPoint point) =>
        point.X >= box.Left && point.X <= box.Right && point.Y >= box.Top && point.Y <= box.Bottom;
}
