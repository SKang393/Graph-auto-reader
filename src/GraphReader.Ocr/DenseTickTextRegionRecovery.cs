// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

internal sealed record DenseTickCrop(string SourceRegionId, OcrDetectedRegion Region);

/// <summary>
/// Experimental crop geometry for a single merged numeric X-axis region.
/// The strokes locate crops only; they never supply a printed value.
/// </summary>
internal static class DenseTickTextRegionRecovery
{
    internal const string CompositionVersion = "dense-tick-observed-stroke-crops-v1";

    internal static IReadOnlyList<DenseTickCrop> Select(
        IReadOnlyList<OcrRegion> regions,
        IReadOnlyList<OcrXAxisTickStroke>? strokes,
        OcrRectangle plot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(regions);
        cancellationToken.ThrowIfCancellationRequested();
        if (!plot.IsValid || !double.IsFinite(plot.Right) || !double.IsFinite(plot.Bottom) ||
            strokes is null || strokes.Count is < 4 or > 512)
            return [];

        OcrRegion[] wide = regions.Where(region =>
            region.SourceImage == OcrSourceImage.Original &&
            region.ReviewStatus == OcrReviewStatus.Unreviewed && region.Role == OcrTextRole.XTick &&
            region.Text.Length >= 10 && GraphNumericParser.IsLiteralGraphNumber(region.Text) &&
            region.Polygon.Bounds.Width > region.Polygon.Bounds.Height * 4).ToArray();
        if (wide.Length != 1) return [];
        OcrRegion source = wide[0];
        OcrRectangle bounds = source.Polygon.Bounds;
        if (!bounds.IsValid || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom) ||
            bounds.Left < 0 || bounds.Top < 0 || bounds.Center.Y <= plot.Bottom)
            return [];

        double[] sorted = strokes.Where(stroke => stroke is not null &&
                stroke.LineStart != stroke.LineEnd &&
                TickLaneTextRegionRecovery.IsUsableXAxisTickStroke(stroke, plot))
            .Select(static stroke => stroke.Center.X).Order().ToArray();
        var groups = new List<List<double>>();
        foreach (double value in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (groups.Count > 0 && value - groups[^1][0] <= 5d) groups[^1].Add(value);
            else groups.Add([value]);
        }
        double[] positions = groups.Select(static group => group.Average()).ToArray();
        double[] gaps = positions.Zip(positions.Skip(1), static (left, right) => right - left)
            .Where(static gap => gap is >= 8d and <= 80d).ToArray();
        if (gaps.Length < 3) return [];
        (int Count, double Gap) best = gaps.Select(candidate =>
                (Count: gaps.Count(gap => Math.Abs(gap - candidate) <= Math.Max(2d, candidate * .12d)), Gap: candidate))
            .OrderByDescending(static item => item.Count).ThenBy(static item => item.Gap).First();
        if (best.Count < 3) return [];
        double[] consistent = gaps.Where(gap => Math.Abs(gap - best.Gap) <= Math.Max(2d, best.Gap * .12d))
            .Order().ToArray();
        double pitch = consistent.Length % 2 == 0
            ? (consistent[(consistent.Length / 2) - 1] + consistent[consistent.Length / 2]) / 2d
            : consistent[consistent.Length / 2];

        var result = new List<DenseTickCrop>();
        foreach (double center in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (center < bounds.Left || center > bounds.Right) continue;
            double left = Math.Floor(Math.Max(bounds.Left, center - (pitch * .45d)));
            double right = Math.Ceiling(Math.Min(bounds.Right, center + (pitch * .45d)));
            double top = Math.Floor(bounds.Top), bottom = Math.Ceiling(bounds.Bottom);
            if (result.Count > 0) left = Math.Max(left, result[^1].Region.Polygon.Bounds.Right);
            if (right - left < pitch * .65d || bottom - top < 3d) continue;
            var rectangle = new OcrRectangle(left, top, right - left, bottom - top);
            var detected = new OcrDetectedRegion($"dense-tick:{source.RegionId}:{result.Count:D3}",
                OcrPolygon.FromRectangle(rectangle), 0, source.Confidence,
                new OcrRegionContext(ExplicitRoleHint: OcrTextRole.XTick));
            result.Add(new DenseTickCrop(source.RegionId, detected));
        }
        return result.Count >= 2 ? OcrCollections.Freeze(result) : [];
    }
}
