// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Finds nearby original-pixel ink that may belong to a cropped tick reading.</summary>
internal static class ProductionAdjacentTickInkReview
{
    private const byte InkThreshold = 128;
    private const int MaximumStripPixels = 1_000_000;

    internal static bool HasAdjacentInk(OcrImage image, OcrRectangle box, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (!box.IsValid || !double.IsFinite(box.Right) || !double.IsFinite(box.Bottom) ||
            box.Left < 0 || box.Top < 0 || box.Right > image.Width || box.Bottom > image.Height)
            return false;
        int left = (int)Math.Floor(box.Left), top = (int)Math.Floor(box.Top);
        int bottom = (int)Math.Ceiling(box.Bottom);
        long height = (long)bottom - top;
        long margin = Math.Max(3L, height * 3L / 2L);
        int x0 = (int)Math.Max(0L, left - margin);
        int y0 = (int)Math.Max(0L, top - (margin / 2L));
        int y1 = (int)Math.Min(image.Height, bottom + (margin / 2L));
        int width = left - x0, stripHeight = y1 - y0;
        long size = (long)width * stripHeight;
        if (size <= 0 || size > MaximumStripPixels) return false;

        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        bool hasInk = false, hasBackground = false;
        for (int y = y0; y < y1 && !(hasInk && hasBackground); y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = x0; x < left; x++)
            {
                if (pixels[(y * image.Stride) + x] <= InkThreshold) hasInk = true;
                else hasBackground = true;
            }
        }
        if (!hasInk || !hasBackground) return false;

        var visited = new bool[(int)size];
        var queue = new int[(int)size];
        long requiredArea = Math.Max(3L, ((3L * height * height) + 99L) / 100L);
        for (int y = y0; y < y1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = x0; x < left; x++)
            {
                int position = ((y - y0) * width) + x - x0;
                if (visited[position] || pixels[(y * image.Stride) + x] > InkThreshold) continue;
                int head = 0, tail = 0, maxX = x, minY = y, maxY = y;
                visited[position] = true;
                queue[tail++] = position;
                while (head < tail)
                {
                    if ((head & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int current = queue[head++];
                    int px = x0 + (current % width), py = y0 + (current / width);
                    maxX = Math.Max(maxX, px);
                    minY = Math.Min(minY, py);
                    maxY = Math.Max(maxY, py);
                    long overlap = Math.Max(0L, Math.Min(bottom, (long)maxY + 1L) - Math.Max(top, minY));
                    if ((long)head >= requiredArea && ((long)maxY - minY + 1L) * 2L >= height &&
                        overlap * 2L >= height && ((long)left - maxX - 1L) * 2L <= height)
                        return true;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = px + dx, ny = py + dy;
                        if (nx < x0 || nx >= left || ny < y0 || ny >= y1) continue;
                        int next = ((ny - y0) * width) + nx - x0;
                        if (visited[next] || pixels[(ny * image.Stride) + nx] > InkThreshold) continue;
                        visited[next] = true;
                        queue[tail++] = next;
                    }
                }
            }
        }
        return false;
    }
}
