// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.Frozen;
using GraphReader.Axis;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Checks original-pixel support for stroke gaps and isolated divider dashes.</summary>
internal static class ProductionMarkerStrokeGapExclusion
{
    internal const string Version = "original-pixel-marker-stroke-exclusion-v3-repeated-divider-ink";

    // Stay within the existing enclosed-outline support search. Larger or clipped
    // candidates lack enough local evidence for this conservative exclusion.
    private const int MaximumRadius = 12;
    private const int MaximumWindowPixels = (MaximumRadius * 2 + 2) * (MaximumRadius * 2 + 2);
    private const float ForegroundLuminance = 196f / 255f;

    internal static StrokeGapMarkerExclusionBatch Find(
        MarkerImageFrame frame,
        IReadOnlyList<ClassifiedMarker> markers,
        CancellationToken cancellationToken,
        IReadOnlyList<PhaseDividerGeometry>? dividers = null)
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
            if (dividers is { Count: > 0 } && IsDividerDash(frame, center, dividers, cancellationToken))
            {
                if (excluded.Add(center.MarkerId))
                    warnings.Add($"marker_excluded_by_original_pixel_divider_dash:{center.MarkerId}");
                continue;
            }
            if (marker.Fill != MarkerFill.Open || marker.Shape is not
                (MarkerShape.Circle or MarkerShape.Square or MarkerShape.Diamond or
                 MarkerShape.TriangleUp or MarkerShape.TriangleDown)) continue;
            if (!IsStrokeGap(frame, center, cancellationToken)) continue;
            if (excluded.Add(center.MarkerId))
                warnings.Add($"marker_excluded_by_original_pixel_stroke_gap:{center.MarkerId}");
        }
        return new(excluded.ToFrozenSet(StringComparer.Ordinal), warnings.AsReadOnly());
    }

    private static bool IsDividerDash(MarkerImageFrame frame, MarkerCenter marker,
        IReadOnlyList<PhaseDividerGeometry> dividers, CancellationToken cancellationToken)
    {
        // Only a complete ink-centered stroke can contradict the classifier. Keep
        // clipped, oversized and hollow candidates eligible for the usual checks.
        double radius = marker.Radius;
        if (radius > MaximumRadius) return false;
        int reach = (int)Math.Ceiling(2 * radius) + 1;
        double x = marker.Center.X, y = marker.Center.Y;
        if (x < reach || y < reach || x >= frame.Width - reach - 1 || y >= frame.Height - reach - 1)
            return false;
        int cx = (int)Math.Round(x), cy = (int)Math.Round(y);
        ReadOnlySpan<float> pixels = frame.ChannelsFirstPixels.Span;
        if (pixels[cy * frame.Width + cx] > ForegroundLuminance) return false;

        GeometryLineSegment? nearest = null;
        double bestDistance = radius;
        foreach (PhaseDividerGeometry divider in dividers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (divider.Style is not (DividerStyle.Dashed or DividerStyle.Dotted)) continue;
            GeometryLineSegment line = divider.Line;
            double dx = line.End.X - line.Start.X, dy = line.End.Y - line.Start.Y;
            double length = line.Length;
            if (!double.IsFinite(length) || length <= 0) continue;
            double along = ((x - line.Start.X) * dx + (y - line.Start.Y) * dy) / length;
            double distance = Math.Abs((x - line.Start.X) * dy - (y - line.Start.Y) * dx) / length;
            if (along < 0 || along > length || distance >= bestDistance) continue;
            nearest = line;
            bestDistance = distance;
        }
        if (nearest is not { } axis) return false;

        // Find the blank scanlines bounding the fragment. A joined connector or
        // stroke continuing outside this window cannot establish an isolated dash.
        int left = cx - reach, right = cx + reach;
        int top = cy, bottom = cy;
        while (top > cy - reach && HasInk(pixels.Slice((top - 1) * frame.Width + left, 2 * reach + 1))) top--;
        while (bottom < cy + reach && HasInk(pixels.Slice((bottom + 1) * frame.Width + left, 2 * reach + 1))) bottom++;
        if (top == cy - reach || bottom == cy + reach) return false;
        double ux = (axis.End.X - axis.Start.X) / axis.Length, uy = (axis.End.Y - axis.Start.Y) / axis.Length;
        double minAlong = double.PositiveInfinity, maxAlong = double.NegativeInfinity;
        double minAcross = double.PositiveInfinity, maxAcross = double.NegativeInfinity;
        Span<int> widths = stackalloc int[bottom - top + 1];
        int lastLeft = left, lastRight = right;
        for (int row = Math.Min(top, (int)Math.Floor(y - radius)); row <= Math.Max(bottom, (int)Math.Ceiling(y + radius)); row++)
        {
            int rowLeft = -1, rowRight = -1;
            for (int col = left; col <= right; col++)
            {
                if (pixels[row * frame.Width + col] > ForegroundLuminance) continue;
                if (col == left || col == right || row < top || row > bottom) return false;
                if (rowRight >= 0 && col != rowRight + 1) return false;
                if (rowLeft < 0) rowLeft = col;
                rowRight = col;
                double dx = col - axis.Start.X, dy = row - axis.Start.Y;
                double along = dx * ux + dy * uy, across = dx * uy - dy * ux;
                minAlong = Math.Min(minAlong, along);
                maxAlong = Math.Max(maxAlong, along);
                minAcross = Math.Min(minAcross, across);
                maxAcross = Math.Max(maxAcross, across);
            }
            if (rowLeft < 0) continue;
            if (rowLeft > lastRight + 1 || rowRight < lastLeft - 1) return false;
            widths[row - top] = rowRight - rowLeft + 1;
            lastLeft = rowLeft;
            lastRight = rowRight;
        }

        // A lateral arm on a thin shaft can still be elongated overall. Preserve
        // visible widening beyond one pixel of scan-conversion variation per edge.
        widths.Sort();
        if (widths[^1] > widths[widths.Length / 2] + 2) return false;

        // Use the full projection of a unit pixel square in each direction.
        double unit = Math.Abs(ux) + Math.Abs(uy);
        double lengthAlong = maxAlong - minAlong + unit, widthAcross = maxAcross - minAcross + unit;
        if (minAlong < 0 || maxAlong > axis.Length || minAcross > unit / 2 || maxAcross < -unit / 2 ||
            lengthAlong < 2 * radius) return false;
        if (widthAcross <= radius && lengthAlong >= 3 * widthAcross) return true;

        // The projected bounding box includes raster stair-steps and isolated edge
        // pixels. Use the typical cross section only if the adjacent broken ink
        // independently confirms a repeating stroke on this same measured line.
        int thickness = widths[widths.Length / 2];
        if (Math.Abs(uy) < Math.Abs(ux) || thickness > radius || lengthAlong < 3 * thickness ||
            widths[^1] > thickness + 1) return false;
        int height = bottom - top + 1;
        Span<DividerRun> central = stackalloc DividerRun[height];
        for (int row = top; row <= bottom; row++)
            if (!TryReadRun(pixels, frame.Width, frame.Height, axis, row, reach, out central[row - top])) return false;
        if (!HasStrokeProfile(central, thickness)) return false;
        var above = ReadNeighbor(pixels, frame.Width, frame.Height, axis, top, -1, reach, height, thickness);
        if (above.Length == 0) return false;
        var below = ReadNeighbor(pixels, frame.Width, frame.Height, axis, bottom, 1, reach, height, thickness);
        // Two quantized endpoints can shift a measured interval by up to two pixels.
        return below.Length > 0 && Math.Abs(above.Length - below.Length) <= 2 &&
            Math.Abs(above.Gap - below.Gap) <= 2;
    }

    private static (int Gap, int Length) ReadNeighbor(ReadOnlySpan<float> pixels, int width, int height,
        GeometryLineSegment axis, int endRow, int step, int reach, int dashHeight, int thickness)
    {
        int gap = 0, length = 0;
        Span<DividerRun> runs = stackalloc DividerRun[2 * dashHeight + 2];
        for (int offset = 1; offset <= runs.Length; offset++)
        {
            if (!TryReadRun(pixels, width, height, axis, endRow + step * offset, reach, out var run)) break;
            if (run.Width == 0)
            {
                if (length > 0) return HasStrokeProfile(runs[..length], thickness) ? (gap, length) : default;
                if (++gap > dashHeight) break;
                continue;
            }
            if (gap == 0 || run.Width > thickness + 1) break;
            runs[length++] = run;
        }
        return default;
    }

    private readonly record struct DividerRun(int Left, int Width, bool Aligned)
    {
        internal int Right => Left + Width - 1;

        internal bool IsTaperedEndOf(DividerRun body) =>
            body.Aligned && Width < body.Width && Left >= body.Left - 1 && Right <= body.Right + 1;
    }

    private static bool HasStrokeProfile(ReadOnlySpan<DividerRun> runs, int thickness)
    {
        if (runs.Length < thickness - 1) return false;
        bool matchesWidth = false;
        for (int i = 0; i < runs.Length; i++)
        {
            DividerRun run = runs[i];
            if (run.Width <= 0 || run.Width > thickness + 1) return false;
            if (i > 0 && (run.Left > runs[i - 1].Right + 1 || run.Right < runs[i - 1].Left - 1)) return false;
            if (run.Aligned && run.Width >= thickness - 1)
            {
                matchesWidth |= run.Width == thickness;
                continue;
            }
            // A fractional end may rasterize as a narrower row on one side of
            // the centerline. It must taper directly into the adjacent ink.
            bool tapered = runs.Length > 1 && ((i == 0 && run.IsTaperedEndOf(runs[1])) ||
                (i == runs.Length - 1 && run.IsTaperedEndOf(runs[^2])));
            if (!tapered) return false;
        }
        return matchesWidth;
    }

    // Blank rows establish the gaps. Clipped windows and split runs are
    // inconclusive. Follow the measured slope on each scanline.
    private static bool TryReadRun(ReadOnlySpan<float> pixels, int width, int height,
        GeometryLineSegment axis, int row, int reach, out DividerRun run)
    {
        run = default;
        if ((uint)row >= (uint)height || row < Math.Min(axis.Start.Y, axis.End.Y) ||
            row > Math.Max(axis.Start.Y, axis.End.Y)) return false;
        double axisX = axis.Start.X + (row - axis.Start.Y) * (axis.End.X - axis.Start.X) /
            (axis.End.Y - axis.Start.Y);
        if (axisX < reach || axisX >= width - reach - 1) return false;
        int middle = (int)Math.Round(axisX), left = middle - reach, right = middle + reach;
        int first = -1, last = -1;
        for (int col = left; col <= right; col++)
        {
            if (pixels[row * width + col] > ForegroundLuminance) continue;
            if (col == left || col == right || (last >= 0 && col != last + 1)) return false;
            if (first < 0) first = col;
            last = col;
        }
        if (first >= 0) run = new(first, last - first + 1, axisX >= first - 0.5 && axisX <= last + 0.5);
        return true;
    }

    private static bool HasInk(ReadOnlySpan<float> row)
    {
        foreach (float value in row)
            if (value <= ForegroundLuminance) return true;
        return false;
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
