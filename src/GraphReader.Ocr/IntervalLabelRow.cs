// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

internal sealed record IntervalLabelRow(OcrRectangle Plot, OcrRectangle[] Anchors, IReadOnlyList<OcrDetectedRegion> Detected)
{
    internal List<OcrDetectedRegion> Proposals { get; } = [];
}

public sealed partial class ConnectedComponentTextRegionDetector
{
    // Two independently detected numeric crops can locate a printed row. Their values
    // are not used here. Measure its physical cell boundaries on the immutable raster.
    private void RecoverIntervalLabels(OcrImage image, byte threshold, List<ComponentBounds> components,
        IntervalLabelRow row, CancellationToken token)
    {
        var plot = row.Plot;
        var anchors = row.Anchors;
        if (!plot.IsValid || plot.Left < 0 || plot.Bottom < 0 || plot.Right > image.Width ||
            plot.Bottom >= image.Height || anchors.Length < 2 || anchors.Any(box => !box.IsValid ||
            box.Top <= plot.Bottom || box.Bottom > image.Height || box.Left < plot.Left || box.Right > plot.Right))
            return;
        double commonTop = anchors.Max(box => box.Top), commonBottom = anchors.Min(box => box.Bottom);
        double minHeight = anchors.Min(box => box.Height), maxHeight = anchors.Max(box => box.Height);
        if (commonTop >= commonBottom || maxHeight > 2 * minHeight) return;

        double top = anchors.Min(box => box.Top), bottom = anchors.Max(box => box.Bottom);
        var strokes = AttachedTickStroke.Find(image, true, threshold, plot.Left, plot.Right, plot.Bottom,
            plot.Bottom, (int)Math.Ceiling(top - plot.Bottom), token);
        if (strokes.Count is < 3 or > 128) return;
        var cells = strokes.Zip(strokes.Skip(1)).ToArray();
        bool InCell(OcrRectangle box, (AttachedTickStroke First, AttachedTickStroke Second) cell)
        {
            double midpoint = (cell.First.Position + cell.Second.Position) / 2;
            return cell.First.Last < box.Left && box.Right < cell.Second.First &&
                box.Left <= midpoint && midpoint <= box.Right &&
                box.Top > Math.Max(cell.First.Tip, cell.Second.Tip) &&
                box.Top - Math.Min(cell.First.Tip, cell.Second.Tip) <= 2 * box.Height;
        }

        var occupied = new HashSet<int>();
        foreach (var anchor in anchors)
        {
            token.ThrowIfCancellationRequested();
            int index = Array.FindIndex(cells, cell => InCell(anchor, cell));
            if (index < 0 || !occupied.Add(index)) return;
        }

        var rowComponents = components.Where(part => part.Top < commonBottom && part.Bottom + 1 > commonTop &&
            part.Top >= top - minHeight / 2 && part.Bottom < bottom + minHeight / 2).ToArray();
        var candidates = new List<OcrDetectedRegion>();
        for (int i = 0; i < cells.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var cell = cells[i];
            var members = rowComponents.Where(part => part.Left > cell.First.Last && part.Right < cell.Second.First)
                .OrderBy(part => part.Left).ToArray();
            ComponentBounds? group = JoinGlyphs(members);
            if (group is not { } ink || ink.Height < minHeight / 2 || ink.Height > 2 * maxHeight)
            {
                if (occupied.Contains(i)) return;
                continue;
            }
            var region = ToRegion(image, ink);
            var box = region.Polygon.Bounds;
            int y0 = Math.Max(Math.Max(cell.First.Tip, cell.Second.Tip) + 1, (int)Math.Floor(top - minHeight / 2));
            int y1 = Math.Min(image.Height - 1, (int)Math.Ceiling(bottom + minHeight / 2));
            if (!InCell(box, cell) || ink.Top <= y0 || ink.Bottom >= y1 || !ContainsOnlyGroupInk())
            {
                if (occupied.Contains(i)) return;
                continue;
            }
            // Existing crops (including unreadable fragments and composite text) retain
            // ownership of their pixels. Never replace one reading with a second vote.
            if (!occupied.Contains(i) && !row.Detected.Any(other => Overlaps(box, other.Polygon.Bounds)))
                candidates.Add(region with { RegionId = TickLaneTextRegionRecovery.XIntervalPrefix + region.RegionId, OrientationDegrees = 0 });

            bool ContainsOnlyGroupInk()
            {
                // Count everything including both cell edges. Ink from a crossed boundary,
                // clipped glyph, nearby line or filtered structural component vetoes the crop.
                int count = 0;
                for (int y = y0; y <= y1; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = cell.First.Last; x <= cell.Second.First; x++)
                        if (image.Pixels.Span[y * image.Stride + x] <= threshold) count++;
                }
                return count == ink.Area;
            }
        }
        row.Proposals.AddRange(candidates);
    }

    private static ComponentBounds? JoinGlyphs(ComponentBounds[] members)
    {
        if (members.Length == 0) return null;
        var group = members[0];
        for (int i = 1; i < members.Length; i++)
        {
            var next = members[i];
            // A gap wider than an adjacent glyph may separate words or labels.
            if (next.Left - group.Right - 1 > Math.Max(members[i - 1].Width, next.Width)) return null;
            group = group.Merge(next);
        }
        return group;
    }

    private static bool Overlaps(OcrRectangle first, OcrRectangle second) =>
        first.Left < second.Right && second.Left < first.Right &&
        first.Top < second.Bottom && second.Top < first.Bottom;
}
