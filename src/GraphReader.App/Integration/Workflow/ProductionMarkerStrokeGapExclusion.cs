// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.Frozen;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Rejects white-gap proposals supported only by thin original-pixel strokes.</summary>
internal static class ProductionMarkerStrokeGapExclusion
{
    internal const string Version = "original-pixel-marker-stroke-gap-exclusion-v1";

    // Stay within the existing enclosed-outline support search. Larger or clipped
    // candidates lack enough local evidence for this conservative exclusion.
    private const int MaximumRadius = 12;
    private const int MaximumWindowPixels = (MaximumRadius * 2 + 2) * (MaximumRadius * 2 + 2);
    private const float ForegroundLuminance = 196f / 255f;

    internal static StrokeGapMarkerExclusionBatch Find(
        MarkerImageFrame frame,
        IReadOnlyList<ClassifiedMarker> markers,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.Width <= 0 || frame.Height <= 0 || frame.ChannelCount != 1 ||
            frame.ChannelsFirstPixels.Length != (long)frame.Width * frame.Height ||
            frame.SourceImage != MarkerSourceImage.Original ||
            frame.OriginalToFrame != MarkerAffineTransform.Identity)
            throw new ArgumentException("Stroke-gap exclusion requires an original-pixel luminance frame.", nameof(frame));

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (ClassifiedMarker marker in markers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkerCenter center = marker.Marker;
            if (!center.Center.IsFinite || !double.IsFinite(center.Radius) || center.Radius <= 0 ||
                center.CoordinateSpace != MarkerContract.CoordinateSpace)
                throw new ArgumentException("Stroke-gap exclusion requires finite original-pixel candidates.", nameof(markers));
            if (marker.Fill != MarkerFill.Open || marker.Shape is not
                (MarkerShape.Circle or MarkerShape.Square or MarkerShape.Diamond or
                 MarkerShape.TriangleUp or MarkerShape.TriangleDown)) continue;
            if (!IsStrokeGap(frame, center, cancellationToken)) continue;
            if (excluded.Add(center.MarkerId))
                warnings.Add($"marker_excluded_by_original_pixel_stroke_gap:{center.MarkerId}");
        }
        return new(excluded.ToFrozenSet(StringComparer.Ordinal), warnings.AsReadOnly());
    }

    private static bool IsStrokeGap(MarkerImageFrame frame, MarkerCenter marker, CancellationToken cancellationToken)
    {
        double x = marker.Center.X, y = marker.Center.Y, radius = marker.Radius;
        if (radius > MaximumRadius || x - radius < 0 || y - radius < 0 ||
            Math.Ceiling(x + radius) >= frame.Width || Math.Ceiling(y + radius) >= frame.Height) return false;
        int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
        ReadOnlySpan<float> pixels = frame.ChannelsFirstPixels.Span;
        // An ink-centered cross can be mislabeled as an open diamond. The existing
        // enclosure test returns false on ink, so protect ink centers separately.
        if (1 - pixels[iy * frame.Width + ix] >= 0.12f ||
            ProductionMarkerEnclosedSupport.IsSupported(frame, x, y)) return false;

        int left = (int)Math.Floor(x - radius), right = (int)Math.Ceiling(x + radius);
        int top = (int)Math.Floor(y - radius), bottom = (int)Math.Ceiling(y + radius);
        int width = right - left + 1, height = bottom - top + 1;
        Span<bool> visited = stackalloc bool[MaximumWindowPixels];
        visited.Clear();
        Span<int> component = stackalloc int[MaximumWindowPixels];
        bool spansDiameter = false;
        for (int start = 0; start < width * height; start++)
        {
            if (visited[start] || pixels[(top + start / width) * frame.Width + left + start % width] > ForegroundLuminance)
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            component[0] = start;
            visited[start] = true;
            int head = 0, count = 1;
            double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0, sumYY = 0;
            while (head < count)
            {
                int point = component[head++], px = point % width, py = point / width;
                sumX += px; sumY += py; sumXX += px * px; sumXY += px * py; sumYY += py * py;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = px + dx, ny = py + dy;
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                    int next = ny * width + nx;
                    if (visited[next] || pixels[(top + ny) * frame.Width + left + nx] > ForegroundLuminance) continue;
                    visited[next] = true;
                    component[count++] = next;
                }
            }

            double meanX = sumX / count, meanY = sumY / count;
            double xx = sumXX / count - meanX * meanX;
            double xy = sumXY / count - meanX * meanY;
            double yy = sumYY / count - meanY * meanY;
            double discriminant = Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy);
            double minorVariance = (xx + yy - discriminant) / 2;
            // Even one genuinely two-dimensional component protects the candidate.
            if (minorVariance > 1) return false;
            double angle = 0.5 * Math.Atan2(2 * xy, xx - yy);
            double ux = Math.Cos(angle), uy = Math.Sin(angle);
            double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            for (int index = 0; index < count; index++)
            {
                int point = component[index];
                double projection = (point % width - meanX) * ux + (point / width - meanY) * uy;
                minimum = Math.Min(minimum, projection);
                maximum = Math.Max(maximum, projection);
            }
            spansDiameter |= maximum - minimum + 1 >= 2 * radius;
        }
        return spansDiameter;
    }
}

internal sealed record StrokeGapMarkerExclusionBatch(
    IReadOnlySet<string> ExcludedMarkerIds,
    IReadOnlyList<string> Warnings);
