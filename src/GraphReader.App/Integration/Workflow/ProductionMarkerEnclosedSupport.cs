// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Markers.Detection;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Candidate-only support for a thin outline enclosing the decoded pixel.</summary>
internal static class ProductionMarkerEnclosedSupport
{
    private const int MaximumRadius = 12;
    private const int MaximumPixels = (MaximumRadius * 2 + 1) * (MaximumRadius * 2 + 1);
    private const float InkThreshold = 0.12f;

    internal static bool IsSupported(MarkerImageFrame frame, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x), "Decoded coordinates must be finite.");
        double roundedX = Math.Round(x), roundedY = Math.Round(y);
        if (roundedX < 0 || roundedY < 0 || roundedX >= frame.Width || roundedY >= frame.Height)
            return false;
        int ix = (int)roundedX, iy = (int)roundedY;
        int left = Math.Max(0, ix - MaximumRadius), right = Math.Min(frame.Width - 1, ix + MaximumRadius);
        int top = Math.Max(0, iy - MaximumRadius), bottom = Math.Min(frame.Height - 1, iy + MaximumRadius);
        int width = right - left + 1;
        ReadOnlySpan<float> luminance = frame.ChannelsFirstPixels.Span;
        if (1 - luminance[iy * frame.Width + ix] >= InkThreshold) return false;

        Span<bool> visited = stackalloc bool[MaximumPixels];
        visited.Clear();
        Span<int> pending = stackalloc int[MaximumPixels];
        int center = (iy - top) * width + ix - left;
        pending[0] = center;
        visited[center] = true;
        int head = 0, tail = 1;
        ReadOnlySpan<int> offsets = [-1, 1, -width, width];
        while (head < tail)
        {
            int current = pending[head++];
            int px = left + current % width, py = top + current / width;
            // The image/window edge cannot stand in for a visible ink wall.
            if (px == left || px == right || py == top || py == bottom) return false;
            // Four-connected background complements eight-connected ink, so a
            // one-pixel diagonal outline can enclose its original white pixels.
            foreach (int offset in offsets)
            {
                int next = current + offset;
                int nx = left + next % width, ny = top + next / width;
                if (!visited[next] && 1 - luminance[ny * frame.Width + nx] < InkThreshold)
                {
                    visited[next] = true;
                    pending[tail++] = next;
                }
            }
        }
        return true;
    }
}
