// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Axis;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Binds printed labels to outward strokes on a corroborated original-pixel X axis.</summary>
internal sealed record ProductionPrintedXTickRow(
    IReadOnlyDictionary<string, double> Positions,
    IReadOnlySet<string> NestedTextIds,
    IReadOnlySet<string> SpineSegmentIds,
    IReadOnlySet<string> AmbiguousStrokeIds,
    int StrokeCount)
{
    private sealed record Stroke(double X, int Tip);
    private sealed record Anchor(OcrRegion Label, Stroke Stroke, double AxisY);

    internal static ProductionPrintedXTickRow? Find(AxisGeometryResult axis, OcrImage image,
        IReadOnlyList<OcrRegion> labels, IReadOnlyList<OcrRegion> regions, IReadOnlyList<OcrMask> masks, int threshold,
        AxisGeometryOptions options, CancellationToken token)
    {
        var line = axis.XAxis.Line;
        var xLabels = labels.Where(r => r.Role == OcrTextRole.XTick && r.SourceImage == OcrSourceImage.Original &&
            r.Confidence is > 0 and <= 1 && GraphNumericParser.IsLiteralGraphNumber(r.Text) &&
            r.Polygon.Bounds is { Left: >= 0, Top: >= 0 } box && box.Right <= image.Width && box.Bottom <= image.Height).ToArray();
        if (xLabels.Length < 3) return null;
        double AxisY(double x) => line.Start.Y + (x - line.Start.X) *
            (line.End.Y - line.Start.Y) / (line.End.X - line.Start.X);

        var strokes = new List<Stroke>();
        int first = (int)Math.Clamp(Math.Ceiling(Math.Min(line.Start.X, line.End.X)), 0, image.Width);
        int last = (int)Math.Clamp(Math.Floor(Math.Max(line.Start.X, line.End.X)), -1, image.Width - 1);
        int maxLength = (int)Math.Ceiling(axis.YAxis.Line.Length * options.TickMaximumLengthFraction);
        int start = -1, tip = -1, depth = 0;
        for (int x = first; x <= last + 1; x++)
        {
            token.ThrowIfCancellationRequested();
            int axisY = (int)Math.Round(AxisY(x));
            int end = axisY;
            if (x <= last && axisY >= 1 && axisY + 3 < image.Height &&
                image.Pixels.Span[axisY * image.Stride + x] <= threshold)
            {
                while (end + 1 < image.Height && end - axisY <= maxLength &&
                    image.Pixels.Span[(end + 1) * image.Stride + x] <= threshold) end++;
            }
            // Require continuous ink from the axis and a visible end before the label.
            if (end - axisY >= 3 && end - axisY <= maxLength && end + 1 < image.Height)
            {
                if (start < 0) start = x;
                tip = Math.Max(tip, end);
                depth = Math.Max(depth, end - axisY);
            }
            else if (start >= 0)
            {
                if (x - start <= depth && HasAxisFlanks(start, x - 1))
                    strokes.Add(new Stroke((start + x - 1) / 2d, tip));
                start = -1;
                depth = 0;
                tip = -1;
            }
        }
        // A detached row of short vertical marks can cross the fitted line numerically.
        // Check the axis ink on both available sides of each protrusion.
        bool HasAxisFlanks(int left, int right)
        {
            int samples = 0;
            for (int offset = 1; offset <= 2; offset++)
            {
                if (left - offset >= first && !AxisInk(left - offset)) return false;
                if (right + offset <= last && !AxisInk(right + offset)) return false;
            }
            return samples >= 2;

            bool AxisInk(int x)
            {
                int y = (int)Math.Round(AxisY(x));
                if (y < 1 || y >= image.Height - 1) return false;
                samples++;
                return image.Pixels.Span[(y - 1) * image.Stride + x] <= threshold ||
                    image.Pixels.Span[y * image.Stride + x] <= threshold ||
                    image.Pixels.Span[(y + 1) * image.Stride + x] <= threshold;
            }
        }
        if (strokes.Count < 3) return null;
        var anchors = new List<Anchor>();
        var ambiguous = new List<OcrRegion>();
        foreach (var label in xLabels)
        {
            token.ThrowIfCancellationRequested();
            var box = label.Polygon.Bounds;
            var under = strokes.Where(s => s.X >= box.Left && s.X <= box.Right &&
                s.Tip < box.Top && box.Top - s.Tip <= 2 * box.Height).Take(2).ToArray();
            if (under.Length == 1) anchors.Add(new Anchor(label, under[0], AxisY(box.Center.X)));
            if (under.Length == 2) ambiguous.Add(label);
        }
        if (anchors.Count < 3) return null;
        anchors.Sort((a, b) => a.Label.Polygon.Bounds.Left.CompareTo(b.Label.Polygon.Bounds.Left));
        for (int i = 1; i < anchors.Count; i++)
            if (anchors[i].Label.Polygon.Bounds.Left <= anchors[i - 1].Label.Polygon.Bounds.Right) return null;
        double top = anchors.Max(a => a.Label.Polygon.Bounds.Top - a.AxisY);
        double bottom = anchors.Min(a => a.Label.Polygon.Bounds.Bottom - a.AxisY);
        if (top > bottom) return null; // Multiple or staggered label rows need their existing review path.

        var positions = anchors.ToDictionary(a => a.Label.RegionId, a => a.Stroke.X, StringComparer.Ordinal);
        var ambiguousIds = ambiguous.Where(r => r.Polygon.Bounds.Top - AxisY(r.Polygon.Bounds.Center.X) <= bottom &&
            r.Polygon.Bounds.Bottom - AxisY(r.Polygon.Bounds.Center.X) >= top)
            .Select(r => r.RegionId).ToHashSet(StringComparer.Ordinal);
        var nested = new HashSet<string>(StringComparer.Ordinal);
        double rowFarEdge = anchors.Max(a => a.Label.Polygon.Bounds.Bottom - a.AxisY);
        var maskedIds = masks.Where(m => m.CoordinateSpace == OcrContract.CoordinateSpace)
            .Select(m => m.RegionId).ToHashSet(StringComparer.Ordinal);
        var words = regions.Where(r => maskedIds.Contains(r.RegionId) && r.SourceImage == OcrSourceImage.Original &&
            r.ReviewStatus != OcrReviewStatus.Rejected &&
            r.Role is OcrTextRole.AxisTitle or OcrTextRole.Annotation or OcrTextRole.LegendText &&
            r.Text.Count(char.IsLetter) >= 2 && !r.Text.Any(char.IsDigit) && !GraphNumericParser.Parse(r.Text).IsSuccess)
            .Select(r => (Box: r.Polygon.Bounds, Polygon: new MarkerPolygon(r.Polygon.Points.Select(p => new MarkerPoint(p.X, p.Y)).ToArray())))
            .ToArray();
        foreach (var label in xLabels.Where(r => r.ReviewStatus == OcrReviewStatus.Unreviewed))
        {
            token.ThrowIfCancellationRequested();
            var box = label.Polygon.Bounds;
            if (box.Left < anchors[0].Label.Polygon.Bounds.Left || box.Right > anchors[^1].Label.Polygon.Bounds.Right ||
                box.Top - AxisY(box.Center.X) <= rowFarEdge + box.Height) continue;
            if (words.Any(word => box.Width * 2 <= word.Box.Width && box.Height * 2 <= word.Box.Height &&
                label.Polygon.Points.All(p => word.Polygon.Contains(new MarkerPoint(p.X, p.Y))))) nested.Add(label.RegionId);
        }
        var spine = axis.YAxis.Line;
        bool OnSpine(PixelPoint p)
        {
            double f = (p.Y - spine.Start.Y) / (spine.End.Y - spine.Start.Y);
            return f is >= 0 and <= 1 && Math.Abs(p.X - (spine.Start.X + f * (spine.End.X - spine.Start.X))) <= 1;
        }
        var spineIds = axis.Ticks.Where(t => t.Axis == TickAxis.XAxis &&
            OnSpine(t.Line.Start) && OnSpine(t.Line.End) &&
            !strokes.Any(s => Math.Abs(s.X - t.Center.X) <= options.MergeDistancePixels))
            .Select(t => t.TickId).ToHashSet(StringComparer.Ordinal);
        return new(positions, nested, spineIds, ambiguousIds, strokes.Count);
    }
}
