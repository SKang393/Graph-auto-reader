// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Axis;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>A continuous outward stroke with a visible tip and ink on the fitted axis beside it.</summary>
internal sealed record ProductionAttachedTickStroke(int First, int Last, int Tip)
{
    internal double Position => First + (Last - First) / 2d;

    internal static List<ProductionAttachedTickStroke> Find(AxisGeometryResult axis, OcrImage image, bool horizontal,
        int threshold, AxisGeometryOptions options, CancellationToken token)
    {
        var line = horizontal ? axis.XAxis.Line : axis.YAxis.Line;
        double startAlong = horizontal ? line.Start.X : line.Start.Y, endAlong = horizontal ? line.End.X : line.End.Y;
        double startAcross = horizontal ? line.Start.Y : line.Start.X, endAcross = horizontal ? line.End.Y : line.End.X;
        var strokes = new List<ProductionAttachedTickStroke>();
        if (endAlong == startAlong) return strokes;
        int extent = horizontal ? image.Width : image.Height, acrossExtent = horizontal ? image.Height : image.Width;
        // Sample the source pixel containing each fitted endpoint, as for the rail across the axis.
        double lower = Math.Min(startAlong, endAlong), upper = Math.Max(startAlong, endAlong);
        int first = (int)Math.Clamp(Math.Round(lower), 0, extent);
        int last = (int)Math.Clamp(Math.Round(upper), -1, extent - 1);
        // Only pixels centered inside the fitted segment can provide or contradict flank support.
        double firstFlank = Math.Max(first, lower), lastFlank = Math.Min(last, upper);
        int outward = horizontal ? 1 : -1;
        int maxLength = (int)Math.Ceiling((horizontal ? axis.YAxis.Line : axis.XAxis.Line).Length * options.TickMaximumLengthFraction);
        int start = -1, tip = 0, depth = 0;
        for (int i = first; i <= last + 1; i++)
        {
            token.ThrowIfCancellationRequested();
            int rail = Rail(i), length = 0;
            if (i <= last && rail >= 1 && rail < acrossExtent - 1 && Ink(i, rail))
            {
                while (length <= maxLength && InBounds(rail + outward * (length + 1)) &&
                    Ink(i, rail + outward * (length + 1))) length++;
            }
            if (length >= 3 && length <= maxLength && InBounds(rail + outward * (length + 1)))
            {
                int end = rail + outward * length;
                tip = start < 0 ? end : horizontal ? Math.Max(tip, end) : Math.Min(tip, end);
                if (start < 0) start = i;
                depth = Math.Max(depth, length);
            }
            else if (start >= 0)
            {
                if (i - start <= depth && HasFlanks(start, i - 1)) strokes.Add(new(start, i - 1, tip));
                start = -1;
                depth = 0;
            }
        }
        return strokes;

        int Rail(int i) => (int)Math.Round(startAcross + (i - startAlong) * (endAcross - startAcross) / (endAlong - startAlong));
        bool InBounds(int across) => (uint)across < (uint)acrossExtent;
        bool Ink(int i, int across) => image.Pixels.Span[horizontal ? across * image.Stride + i : i * image.Stride + across] <= threshold;
        bool HasFlanks(int lo, int hi)
        {
            int samples = 0;
            for (int offset = 1; offset <= 2; offset++)
            {
                if (lo - offset >= firstFlank && !AxisInk(lo - offset)) return false;
                if (hi + offset <= lastFlank && !AxisInk(hi + offset)) return false;
            }
            return samples >= 2;
            bool AxisInk(int i)
            {
                int rail = Rail(i);
                if (rail < 1 || rail >= acrossExtent - 1) return false;
                samples++;
                return Ink(i, rail - 1) || Ink(i, rail) || Ink(i, rail + 1);
            }
        }
    }
}
