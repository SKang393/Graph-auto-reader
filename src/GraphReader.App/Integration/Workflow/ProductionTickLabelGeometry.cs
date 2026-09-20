// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.Globalization;
using GraphReader.Axis;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Associates OCR labels with unique outward tick strokes in original pixels.</summary>
internal static class ProductionTickLabelGeometry
{
    internal const string Version = "original-pixel-tick-label-association-v1";
    private static readonly AxisGeometryOptions Options = new();

    internal static TickLabelGeometryResult Resolve(
        AxisGeometryResult axis, OcrImage image, IReadOnlyList<OcrRegion> regions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (image.SourceImage != OcrSourceImage.Original || image.OriginalToImage != OcrFrameTransform.Identity ||
            image.CoordinateSpace != OcrContract.CoordinateSpace || axis.CoordinateSpace != OcrContract.CoordinateSpace ||
            image.Width <= 0 || image.Height <= 0 || image.Stride < image.Width ||
            image.Pixels.Length < checked(image.Stride * image.Height) ||
            !axis.XAxis.Line.Start.IsFinite || !axis.XAxis.Line.End.IsFinite ||
            !axis.YAxis.Line.Start.IsFinite || !axis.YAxis.Line.End.IsFinite ||
            axis.XAxis.Line.Start.X == axis.XAxis.Line.End.X || axis.YAxis.Line.Start.Y == axis.YAxis.Line.End.Y ||
            regions.Any(static region => region.CoordinateSpace != OcrContract.CoordinateSpace || !region.Polygon.Bounds.IsValid))
            throw new ArgumentException("Tick association requires aligned original-pixel evidence.", nameof(image));
        OcrRegion[] labels = regions.Where(static region =>
            region.Role is OcrTextRole.XTick or OcrTextRole.YTick &&
            region.ReviewStatus != OcrReviewStatus.Rejected && !string.IsNullOrWhiteSpace(region.Text)).ToArray();
        if (labels.Select(static label => label.RegionId).Distinct(StringComparer.Ordinal).Count() != labels.Length)
            throw new ArgumentException("Tick labels must have unique identities.", nameof(regions));
        var measured = new Dictionary<string, (OcrTextRole Role, double Center, double Original)>(StringComparer.Ordinal);
        var warnings = new List<string>();
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        long sum = 0;
        for (int y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++) sum += pixels[y * image.Stride + x];
        }
        int threshold = Math.Clamp((int)Math.Round(sum / ((double)image.Width * image.Height) * 0.80), 32, 224);
        foreach (OcrRegion label in labels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool vertical = label.Role == OcrTextRole.YTick;
            OcrRectangle box = label.Polygon.Bounds;
            GeometryLineSegment line = vertical ? axis.YAxis.Line : axis.XAxis.Line;
            double center = vertical ? box.Center.Y : box.Center.X;
            double radius = vertical ? box.Height : Math.Max(box.Width / 2, box.Height);
            int extent = vertical ? image.Height : image.Width;
            int first = (int)Math.Clamp(Math.Floor(center - radius), 0, extent - 1);
            int last = (int)Math.Clamp(Math.Ceiling(center + radius), 0, extent - 1);
            var candidates = new List<double>();
            int groupStart = -1;
            for (int coordinate = first; coordinate <= last + 1; coordinate++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int support = 0;
                if (coordinate <= last)
                {
                    double start = vertical ? line.Start.Y : line.Start.X;
                    double end = vertical ? line.End.Y : line.End.X;
                    double crossStart = vertical ? line.Start.X : line.Start.Y;
                    double crossEnd = vertical ? line.End.X : line.End.Y;
                    double cross = crossStart + (coordinate - start) / (end - start) * (crossEnd - crossStart);
                    if (!double.IsFinite(cross))
                        throw new ArgumentException("Tick association axis interpolation must be finite.", nameof(axis));
                    // Inspect two or more outward pixels, clear of the axis and label glyphs.
                    double outer = Options.TickAxisDistancePixels;
                    double inner = outer / 2;
                    int low = (int)Math.Clamp(Math.Ceiling(vertical ? Math.Max(cross - outer, box.Right + inner) : cross + inner),
                        0, vertical ? image.Width : image.Height);
                    int high = (int)Math.Clamp(Math.Floor(vertical ? cross - inner : Math.Min(cross + outer, box.Top - inner)),
                        -1, vertical ? image.Width - 1 : image.Height - 1);
                    for (int offset = low; offset <= high; offset++)
                    {
                        int x = vertical ? offset : coordinate;
                        int y = vertical ? coordinate : offset;
                        if (pixels[y * image.Stride + x] <= threshold) support++;
                    }
                }
                if (support >= 2)
                {
                    if (groupStart < 0) groupStart = coordinate;
                }
                else if (groupStart >= 0)
                {
                    int width = coordinate - groupStart;
                    if (width <= Math.Max(2, box.Height / 2)) candidates.Add((groupStart + coordinate - 1) / 2d);
                    groupStart = -1;
                }
            }
            if (candidates.Count == 1) measured.Add(label.RegionId, (label.Role, candidates[0], center));
            else if (candidates.Count > 1) warnings.Add($"tick_label_multiple_visible_ticks:{label.RegionId}");
        }
        // Several labels must not independently claim the same stroke.
        string[] conflicts = measured.Where(left => measured.Any(right => left.Key != right.Key &&
            left.Value.Role == right.Value.Role && Math.Abs(left.Value.Center - right.Value.Center) <= Options.MergeDistancePixels))
            .Select(static item => item.Key).ToArray();
        foreach (string id in conflicts)
        {
            measured.Remove(id);
            warnings.Add($"tick_label_shared_visible_tick:{id}");
        }
        foreach (var (id, value) in measured)
            warnings.Add($"tick_label_original_pixel_association:{id}:" +
                value.Original.ToString("R", CultureInfo.InvariantCulture) + ":" +
                value.Center.ToString("R", CultureInfo.InvariantCulture));
        return new(new ReadOnlyDictionary<string, double>(measured.ToDictionary(
            static item => item.Key, static item => item.Value.Center, StringComparer.Ordinal)),
            Array.AsReadOnly(warnings.ToArray()));
    }
}

internal sealed record TickLabelGeometryResult(
    IReadOnlyDictionary<string, double> Positions, IReadOnlyList<string> Warnings);
