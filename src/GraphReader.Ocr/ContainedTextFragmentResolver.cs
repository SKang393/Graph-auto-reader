// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

public sealed record ContainedTextFragmentResolution(
    IReadOnlyList<OcrRegion> Regions,
    IReadOnlyList<string> RemovedRegionIds);

/// <summary>Removes redundant recognition fragments within one panel.</summary>
public static class ContainedTextFragmentResolver
{
    public const string CompositionVersion = "contained-exact-text-fragments-v1";

    public static ContainedTextFragmentResolution Resolve(
        IReadOnlyList<OcrRegion> regions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regions);
        cancellationToken.ThrowIfCancellationRequested();
        var text = regions.Select(static region => Compact(region.Text)).ToArray();
        var eligible = regions.Select(static region =>
            region.CoordinateSpace == OcrContract.CoordinateSpace &&
            region.ReviewStatus != OcrReviewStatus.Rejected &&
            region.Role is not (OcrTextRole.XTick or OcrTextRole.YTick) &&
            !GraphNumericParser.IsLiteralGraphNumber(region.Text) &&
            IsRectangle(region.Polygon)).ToArray();
        var kept = new List<OcrRegion>(regions.Count);
        var removed = new List<string>();
        for (int i = 0; i < regions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRegion child = regions[i];
            bool covered = false;
            if (eligible[i] && text[i].Length > 0 && child.ReviewStatus == OcrReviewStatus.Unreviewed)
            {
                for (int j = 0; j < regions.Count; j++)
                {
                    if (i == j || !eligible[j] || text[j].Length <= text[i].Length ||
                        child.SourceImage != regions[j].SourceImage ||
                        !text[j].Contains(text[i], StringComparison.Ordinal))
                        continue;

                    OcrRectangle outer = regions[j].Polygon.Bounds;
                    OcrRectangle inner = child.Polygon.Bounds;
                    if (outer.Left <= inner.Left && outer.Top <= inner.Top &&
                        outer.Right >= inner.Right && outer.Bottom >= inner.Bottom)
                    {
                        covered = true;
                        break;
                    }
                }
            }
            if (covered) removed.Add(child.RegionId);
            else kept.Add(child);
        }
        return new ContainedTextFragmentResolution(OcrCollections.Freeze(kept), OcrCollections.Freeze(removed));
    }

    private static string Compact(string text) =>
        string.Concat(text.Where(static character => !char.IsWhiteSpace(character)));

    private static bool IsRectangle(OcrPolygon polygon)
    {
        // Bounds containment alone does not establish containment for a rotated
        // or irregular polygon. Leave those regions visible for review.
        OcrRectangle bounds = polygon.Bounds;
        return bounds.IsValid && polygon.Points.Count == 4 && polygon.Points.Distinct().Count() == 4 &&
            polygon.Points.All(point =>
                (point.X == bounds.Left || point.X == bounds.Right) &&
                (point.Y == bounds.Top || point.Y == bounds.Bottom));
    }
}
