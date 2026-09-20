// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

/// <summary>Trims proposal margins using only immutable original Gray8 pixels.</summary>
public static class OriginalPixelTextRegionRefiner
{
    public const string CompositionVersion = "original-pixel-text-bounds-v1";

    public static IReadOnlyList<OcrDetectedRegion> Refine(
        OcrImage image,
        IReadOnlyList<OcrDetectedRegion> regions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(regions);
        cancellationToken.ThrowIfCancellationRequested();
        if (image.SourceImage != OcrSourceImage.Original ||
            image.OriginalToImage != OcrFrameTransform.Identity ||
            image.CoordinateSpace != OcrContract.CoordinateSpace ||
            image.Width <= 0 || image.Height <= 0 || image.Stride < image.Width ||
            image.Pixels.Length < (long)image.Stride * image.Height)
        {
            throw new ArgumentException("Refinement requires aligned original Gray8 pixels.", nameof(image));
        }

        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        long sum = 0;
        for (int y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++)
            {
                sum += pixels[y * image.Stride + x];
            }
        }
        // Same full-image rule as ConnectedComponentTextRegionDetector.
        int threshold = Math.Clamp((int)Math.Round(sum / ((double)image.Width * image.Height) * 0.80), 32, 224);
        var refined = new List<OcrDetectedRegion>(regions.Count);
        foreach (OcrDetectedRegion region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle bounds = region.Polygon.Bounds;
            if (region.CoordinateSpace != OcrContract.CoordinateSpace || !bounds.IsValid ||
                bounds.Left < 0 || bounds.Top < 0 || bounds.Right > image.Width || bounds.Bottom > image.Height)
            {
                throw new ArgumentException("Region must be inside the original panel.", nameof(regions));
            }

            int left = image.Width;
            int top = image.Height;
            int right = 0;
            int bottom = 0;
            for (int y = (int)Math.Floor(bounds.Top); y < (int)Math.Ceiling(bounds.Bottom); y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = (int)Math.Floor(bounds.Left); x < (int)Math.Ceiling(bounds.Right); x++)
                {
                    if (pixels[y * image.Stride + x] <= threshold)
                    {
                        left = Math.Min(left, x);
                        top = Math.Min(top, y);
                        right = Math.Max(right, x + 1);
                        bottom = Math.Max(bottom, y + 1);
                    }
                }
            }
            if (right == 0)
            {
                refined.Add(region);
                continue;
            }
            double x0 = Math.Max(bounds.Left, left);
            double y0 = Math.Max(bounds.Top, top);
            var tight = new OcrRectangle(x0, y0,
                Math.Min(bounds.Right, right) - x0, Math.Min(bounds.Bottom, bottom) - y0);
            refined.Add(tight == bounds ? region : region with
            {
                Polygon = OcrPolygon.FromRectangle(tight),
                Evidence = null,
            });
        }
        return OcrCollections.Freeze(refined);
    }
}
