// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

/// <summary>Read-only original-pixel evidence for a bracket beneath a detached header note.</summary>
public sealed class HeaderBracketEvidence
{
    private readonly OcrImage image;
    private readonly int threshold;

    public HeaderBracketEvidence(OcrImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (image.SourceImage != OcrSourceImage.Original || image.OriginalToImage != OcrFrameTransform.Identity ||
            image.CoordinateSpace != OcrContract.CoordinateSpace || image.Width <= 0 || image.Height <= 0 ||
            image.Stride < image.Width || image.Pixels.Length < (long)image.Stride * image.Height)
            throw new ArgumentException("Header bracket evidence requires aligned original Gray8 pixels.", nameof(image));
        this.image = image;
        long sum = 0;
        for (int y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++) sum += image.Pixels.Span[y * image.Stride + x];
        }
        // Same full-image foreground rule as the existing component detector.
        threshold = Math.Clamp((int)Math.Round(sum / ((double)image.Width * image.Height) * 0.80), 32, 224);
    }

    public bool HasBracketBelow(OcrRectangle label, double lowerBoundary,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!label.IsValid || label.Left < 0 || label.Top < 0 || label.Right > image.Width || label.Bottom > image.Height ||
            !double.IsFinite(lowerBoundary) || lowerBoundary < 0 || lowerBoundary > image.Height)
            throw new ArgumentException("Header bounds must be inside the original image.", nameof(label));
        if (label.Width < 2 * label.Height || label.Bottom >= lowerBoundary) return false;
        int depth = Math.Max(3, (int)Math.Round(label.Height * 0.2));
        int left = Math.Max(0, (int)Math.Floor(label.Left - label.Width));
        int right = Math.Min(image.Width, (int)Math.Ceiling(label.Right + label.Width));
        int bottom = Math.Min((int)Math.Ceiling(label.Bottom + label.Height),
            Math.Min(image.Height - depth - 2, (int)Math.Floor(lowerBoundary) - depth - 1));
        for (int y = (int)Math.Floor(label.Bottom); y < bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = -1, last = -1;
            for (int x = left; x <= right; x++)
            {
                bool ink = x < right && (Ink(x, Math.Max(0, y - 1)) || Ink(x, y) || Ink(x, y + 1));
                if (ink)
                {
                    if (start < 0) start = x;
                    last = x;
                }
                else if (start >= 0 && (x == right || x - last > 1))
                {
                    if (start < label.Left - 1 && last > label.Right + 1 && last - start <= 3 * label.Width &&
                        DownHook(start, y, depth) && DownHook(last, y, depth)) return true;
                    start = -1;
                }
            }
        }
        return false;
    }

    private bool Ink(int x, int y) => image.Pixels.Span[y * image.Stride + x] <= threshold;

    private bool DownHook(int x, int y, int depth)
    {
        for (int row = y + 2; row < y + 2 + depth; row++)
        {
            bool supported = false;
            for (int column = Math.Max(0, x - 1); column <= Math.Min(image.Width - 1, x + 1); column++)
                supported |= Ink(column, row);
            if (!supported) return false;
        }
        return true;
    }
}
