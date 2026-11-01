// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Axis;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Finds positive local evidence that a tick reading shares ink with the Y axis.</summary>
internal static class ProductionAxisConnectedTextReview
{
    private const byte InkThreshold = 200;
    private const int MaximumLocalPixels = 1_000_000;

    internal static bool HasConnectedInk(OcrImage image, OcrRectangle box, GeometryLineSegment axis,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (!box.IsValid || !double.IsFinite(box.Right) || !double.IsFinite(box.Bottom) ||
            box.Left < 0 || box.Top < 0 || box.Right > image.Width || box.Bottom > image.Height ||
            !axis.Start.IsFinite || !axis.End.IsFinite || axis.Start.Y == axis.End.Y)
            return false;
        int left = (int)Math.Floor(box.Left), top = (int)Math.Floor(box.Top);
        int right = (int)Math.Ceiling(box.Right), bottom = (int)Math.Ceiling(box.Bottom);
        long margin = Math.Max(8L, ((long)bottom - top) * 2L);
        int x0 = (int)Math.Max(0L, left - margin), y0 = (int)Math.Max(0L, top - margin);
        int x1 = (int)Math.Min(image.Width, right + margin), y1 = (int)Math.Min(image.Height, bottom + margin);
        int width = x1 - x0, height = y1 - y0;
        long size = (long)width * height;
        if (size <= 0 || size > MaximumLocalPixels) return false;

        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        bool hasInk = false, hasBackground = false;
        for (int y = y0; y < y1 && !(hasInk && hasBackground); y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = x0; x < x1; x++)
            {
                if (pixels[(y * image.Stride) + x] <= InkThreshold) hasInk = true;
                else hasBackground = true;
            }
        }
        // A uniform crop provides no visible ink/background boundary.
        if (!hasInk || !hasBackground) return false;

        int[] queue = new int[(int)size];
        bool[] visited = new bool[(int)size];
        int head = 0, tail = 0;
        for (int y = y0; y < y1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double axisX = axis.Start.X + ((y - axis.Start.Y) / (axis.End.Y - axis.Start.Y) * (axis.End.X - axis.Start.X));
            if (!double.IsFinite(axisX) || axisX < x0 - 2d || axisX > x1 + 1d) continue;
            int center = (int)Math.Round(axisX);
            for (int offset = -1; offset <= 1; offset++)
                Enqueue(center + offset, y, x0, y0, x1, y1, width, image.Stride, pixels, visited, queue, ref tail);
        }
        while (head < tail)
        {
            if ((head & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            int position = queue[head++];
            int x = x0 + (position % width), y = y0 + (position / width);
            if (x >= left && x < right && y >= top && y < bottom) return true;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx != 0 || dy != 0)
                    Enqueue(x + dx, y + dy, x0, y0, x1, y1, width, image.Stride, pixels, visited, queue, ref tail);
            }
        }
        return false;
    }

    private static void Enqueue(int x, int y, int x0, int y0, int x1, int y1, int width, int stride,
        ReadOnlySpan<byte> pixels, bool[] visited, int[] queue, ref int tail)
    {
        if (x < x0 || x >= x1 || y < y0 || y >= y1 || pixels[(y * stride) + x] > InkThreshold) return;
        int position = ((y - y0) * width) + (x - x0);
        if (visited[position]) return;
        visited[position] = true;
        queue[tail++] = position;
    }
}
