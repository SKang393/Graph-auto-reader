// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>
/// Candidate-only adapter that keeps the existing DB tensor contract while
/// limiting its input to source-pixel windows. Recognition still uses the
/// immutable original image. No pixels are rescaled or synthesized as text.
/// </summary>
internal sealed class ProductionSourceScaleOcrDetector(ITextRegionDetector inner) : ITextRegionDetector
{
    internal const int WindowSize = 1200;
    internal const int WindowOverlap = 256;
    private readonly ITextRegionDetector inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public string ConfigurationFingerprint =>
        $"source-scale-windows-v1:size=1200:overlap=256:pad=white:cross-window-iou=0.5:{inner.ConfigurationFingerprint}";

    public async ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateImage(image);
        if (image.Width == WindowSize && image.Height <= WindowSize)
        {
            // Preserve the evaluated ordinary-size path, including ordering and IDs.
            return await inner.DetectAsync(image, cancellationToken).ConfigureAwait(false);
        }

        var candidates = new List<(int Window, OcrDetectedRegion Region)>();
        int ordinal = 0;
        foreach (int y in WindowStarts(image.Height))
        {
            foreach (int x in WindowStarts(image.Width))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int width = Math.Min(WindowSize, image.Width - x);
                int height = Math.Min(WindowSize, image.Height - y);
                OcrImage window = CopyWindow(image, x, y, width, height, cancellationToken);
                IReadOnlyList<OcrDetectedRegion> regions = await inner
                    .DetectAsync(window, cancellationToken).ConfigureAwait(false);
                foreach (OcrDetectedRegion region in regions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    OcrPolygon? clipped = ClipToContent(region.Polygon, width, height);
                    if (clipped is null) continue;
                    var mapped = new OcrPolygon(clipped.Points.Select(point =>
                        image.OriginalToImage.MapToOriginal(new OcrPoint(point.X + x, point.Y + y))).ToArray());
                    candidates.Add((ordinal, region with
                    {
                        RegionId = string.Create(CultureInfo.InvariantCulture, $"window-{x}-{y}:{region.RegionId}"),
                        Polygon = mapped,
                    }));
                }
                ordinal++;
            }
        }

        var selected = new List<(int Window, OcrDetectedRegion Region)>();
        foreach (var candidate in candidates.OrderByDescending(static candidate => candidate.Region.DetectionConfidence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Only remove duplicates introduced by overlapping windows. The
            // unchanged model owns all detections within an individual window.
            if (!selected.Any(previous => previous.Window != candidate.Window &&
                    IntersectionOverUnion(previous.Region.Polygon.Bounds, candidate.Region.Polygon.Bounds) >= 0.5))
            {
                selected.Add(candidate);
            }
        }
        return selected.Select(static candidate => candidate.Region).ToArray();
    }

    private static IEnumerable<int> WindowStarts(int length)
    {
        int offset = 0;
        yield return offset;
        while (offset < length - WindowSize)
        {
            offset = Math.Min(offset + WindowSize - WindowOverlap, length - WindowSize);
            yield return offset;
        }
    }

    private static OcrImage CopyWindow(OcrImage image, int x, int y, int width, int height,
        CancellationToken cancellationToken)
    {
        byte[] gray = new byte[checked(WindowSize * height)];
        byte[] bgr = new byte[checked(WindowSize * height * 3)];
        Array.Fill(gray, (byte)255);
        Array.Fill(bgr, (byte)255);
        for (int row = 0; row < height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image.Pixels.Span.Slice(checked((y + row) * image.Stride + x), width)
                .CopyTo(gray.AsSpan(row * WindowSize, width));
            image.BgrPixels!.Pixels.Span.Slice(checked((y + row) * image.BgrPixels.Stride + x * 3), width * 3)
                .CopyTo(bgr.AsSpan(row * WindowSize * 3, width * 3));
        }
        return new OcrImage(WindowSize, height, WindowSize, gray, OcrSourceImage.Original,
            OcrFrameTransform.Identity, CanonicalOriginalWidth: WindowSize, CanonicalOriginalHeight: height,
            BgrPixels: new OcrBgrBytePixels(WindowSize * 3, bgr));
    }

    private static OcrPolygon? ClipToContent(OcrPolygon polygon, int width, int height)
    {
        OcrRectangle bounds = polygon.Bounds;
        if (bounds.Right <= 0 || bounds.Bottom <= 0 || bounds.Left >= width || bounds.Top >= height) return null;
        if (bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= width && bounds.Bottom <= height) return polygon;
        IReadOnlyList<OcrPoint> points = polygon.Points;
        points = Clip(points, static point => point.X, 0, keepGreater: true);
        points = Clip(points, static point => point.X, width, keepGreater: false);
        points = Clip(points, static point => point.Y, 0, keepGreater: true);
        points = Clip(points, static point => point.Y, height, keepGreater: false);
        if (points.Count < 3) return null;
        var result = new OcrPolygon(points);
        return result.Bounds.IsValid ? result : null;
    }

    private static List<OcrPoint> Clip(IReadOnlyList<OcrPoint> points,
        Func<OcrPoint, double> coordinate, double edge, bool keepGreater)
    {
        var result = new List<OcrPoint>();
        if (points.Count == 0) return result;
        OcrPoint previous = points[^1];
        double previousDistance = coordinate(previous) - edge;
        bool previousInside = keepGreater ? previousDistance >= 0 : previousDistance <= 0;
        foreach (OcrPoint current in points)
        {
            double distance = coordinate(current) - edge;
            bool inside = keepGreater ? distance >= 0 : distance <= 0;
            if (inside != previousInside)
            {
                double fraction = previousDistance / (previousDistance - distance);
                result.Add(new OcrPoint(previous.X + fraction * (current.X - previous.X),
                    previous.Y + fraction * (current.Y - previous.Y)));
            }
            if (inside) result.Add(current);
            previous = current;
            previousDistance = distance;
            previousInside = inside;
        }
        return result;
    }

    private static double IntersectionOverUnion(OcrRectangle first, OcrRectangle second)
    {
        double intersection = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left)) *
            Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        return intersection / (first.Width * first.Height + second.Width * second.Height - intersection);
    }

    private static void ValidateImage(OcrImage image)
    {
        if (image.SourceImage != OcrSourceImage.Original || image.Width <= 0 || image.Height <= 0 ||
            image.Stride < image.Width || image.Pixels.Length < checked(image.Stride * image.Height) ||
            image.BgrPixels is not { } bgr || bgr.Stride < checked(image.Width * 3) ||
            bgr.Pixels.Length != checked(bgr.Stride * image.Height) ||
            !image.OriginalToImage.IsInvertible || image.CanonicalOriginalWidth is <= 0 ||
            image.CanonicalOriginalHeight is <= 0 || image.CoordinateSpace != OcrContract.CoordinateSpace)
        {
            throw new ArgumentException("Source-scale OCR requires valid immutable original Gray8 and BGR pixels.", nameof(image));
        }
    }
}
