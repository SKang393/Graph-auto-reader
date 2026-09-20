// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

public static class InsidePlotTextRegionAssembler
{
    public const string CompositionVersion = "inside-plot-and-header-word-assembly-v2";

    // Keep these thresholds aligned with ParticipantLaneTextRegionAssembler.
    private const double MinimumVerticalOverlapRatio = 0.35;
    private const double MaximumHorizontalGapHeightRatio = 2.5;
    private const double MaximumComponentHeightRatio = 2.0;
    private const double MaximumMergedHeightGrowthRatio = 1.6;
    private const double MaximumVerticalCenterOffsetHeightRatio = 0.10;

    public static IReadOnlyList<OcrDetectedRegion> Assemble(
        IReadOnlyList<OcrDetectedRegion> regions,
        OcrRectangle plotBounds,
        IReadOnlyList<double> phaseDividerXs,
        CancellationToken cancellationToken = default) =>
        OcrCollections.Freeze(AssembleWithMembership(
                regions,
                plotBounds,
                phaseDividerXs,
                cancellationToken)
            .Select(static group => group.Region));

    public static IReadOnlyList<InsidePlotTextRegionAssemblyGroup> AssembleWithMembership(
        IReadOnlyList<OcrDetectedRegion> regions,
        OcrRectangle plotBounds,
        IReadOnlyList<double> phaseDividerXs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(phaseDividerXs);
        if (!plotBounds.IsValid)
        {
            throw new ArgumentException(
                "Plot bounds must be finite and have positive dimensions.",
                nameof(plotBounds));
        }

        if (phaseDividerXs.Any(x =>
                !double.IsFinite(x) || x < plotBounds.Left || x > plotBounds.Right))
        {
            throw new ArgumentException(
                "Phase-divider X positions must be finite and inside the plot bounds.",
                nameof(phaseDividerXs));
        }

        double[] dividers = phaseDividerXs.Distinct().Order().ToArray();
        var remaining = regions
            .Select(static region => new InsidePlotTextRegionAssemblyGroup(
                region,
                OcrCollections.Freeze([region.RegionId])))
            .OrderBy(static group => group.Region.Polygon.Bounds.Top)
            .ThenBy(static group => group.Region.Polygon.Bounds.Left)
            .ThenBy(static group => group.Region.Polygon.Bounds.Bottom)
            .ThenBy(static group => group.Region.Polygon.Bounds.Right)
            .ThenBy(static group => group.Region.RegionId, StringComparer.Ordinal)
            .ToList();
        var assembled = new List<InsidePlotTextRegionAssemblyGroup>(remaining.Count);

        while (remaining.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InsidePlotTextRegionAssemblyGroup line = remaining[0];
            remaining.RemoveAt(0);
            bool changed;
            do
            {
                changed = false;
                for (int index = remaining.Count - 1; index >= 0; index--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    InsidePlotTextRegionAssemblyGroup candidate = remaining[index];
                    OcrRectangle mergedBounds = Union(
                        line.Region.Polygon.Bounds,
                        candidate.Region.Polygon.Bounds);
                    if (!CanMerge(line.Region.Polygon.Bounds, candidate.Region.Polygon.Bounds) ||
                        !IsPermittedTextRow(line.Region.Polygon.Bounds, candidate.Region.Polygon.Bounds,
                            mergedBounds, plotBounds) ||
                        SpansDivider(mergedBounds, dividers))
                    {
                        continue;
                    }

                    line = Merge(line, candidate, mergedBounds);
                    remaining.RemoveAt(index);
                    changed = true;
                }
            }
            while (changed);

            assembled.Add(line);
        }

        return OcrCollections.Freeze(assembled
            .OrderBy(static group => group.Region.Polygon.Bounds.Top)
            .ThenBy(static group => group.Region.Polygon.Bounds.Left)
            .ThenBy(static group => group.Region.Polygon.Bounds.Bottom)
            .ThenBy(static group => group.Region.Polygon.Bounds.Right)
            .ThenBy(static group => group.Region.RegionId, StringComparer.Ordinal));
    }

    private static InsidePlotTextRegionAssemblyGroup Merge(
        InsidePlotTextRegionAssemblyGroup left,
        InsidePlotTextRegionAssemblyGroup right,
        OcrRectangle bounds)
    {
        string[] memberIds = left.MemberRegionIds
            .Concat(right.MemberRegionIds)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string material = $"{CompositionVersion}\n{string.Join('\n', memberIds)}";
        string id = $"inside-plot:{Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";
        var region = new OcrDetectedRegion(
            id,
            OcrPolygon.FromRectangle(bounds),
            left.Region.OrientationDegrees,
            Math.Min(left.Region.DetectionConfidence, right.Region.DetectionConfidence),
            Equals(left.Region.Context, right.Region.Context) ? left.Region.Context : null,
            left.Region.CoordinateSpace,
            Evidence: null);
        return new InsidePlotTextRegionAssemblyGroup(region, OcrCollections.Freeze(memberIds));
    }

    private static bool CanMerge(OcrRectangle left, OcrRectangle right)
    {
        double minimumHeight = Math.Min(left.Height, right.Height);
        double maximumHeight = Math.Max(left.Height, right.Height);
        double verticalOverlap = Math.Max(
            0,
            Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top));
        double horizontalGap = left.Left > right.Right
            ? left.Left - right.Right
            : right.Left > left.Right
                ? right.Left - left.Right
                : 0;
        OcrRectangle merged = Union(left, right);
        double centerOffset = Math.Abs(left.Center.Y - right.Center.Y);

        return maximumHeight / minimumHeight <= MaximumComponentHeightRatio &&
            verticalOverlap / minimumHeight >= MinimumVerticalOverlapRatio &&
            horizontalGap / maximumHeight <= MaximumHorizontalGapHeightRatio &&
            merged.Height / maximumHeight <= MaximumMergedHeightGrowthRatio &&
            centerOffset / maximumHeight <= MaximumVerticalCenterOffsetHeightRatio;
    }

    private static bool IsFullyInsidePlot(OcrRectangle bounds, OcrRectangle plotBounds) =>
        bounds.Left >= plotBounds.Left &&
        bounds.Top >= plotBounds.Top &&
        bounds.Right <= plotBounds.Right &&
        bounds.Bottom <= plotBounds.Bottom;

    private static bool IsPermittedTextRow(
        OcrRectangle left, OcrRectangle right, OcrRectangle merged, OcrRectangle plot)
    {
        if (IsFullyInsidePlot(merged, plot))
        {
            return true;
        }
        // Header fragments need a word-sized positive gap. The wider in-plot
        // allowance would join separate condition codes and headings. The
        // caller also prevents crossing any measured phase boundary.
        double gap = Math.Max(left.Left - right.Right, right.Left - left.Right);
        return merged.Left >= plot.Left && merged.Right <= plot.Right &&
            merged.Top >= 0 && merged.Bottom <= plot.Top &&
            gap > 0 && gap <= Math.Min(left.Height, right.Height);
    }

    private static bool SpansDivider(OcrRectangle bounds, IReadOnlyList<double> phaseDividerXs) =>
        phaseDividerXs.Any(x => bounds.Left < x && x < bounds.Right);

    private static OcrRectangle Union(OcrRectangle left, OcrRectangle right)
    {
        double x = Math.Min(left.Left, right.Left);
        double y = Math.Min(left.Top, right.Top);
        return new OcrRectangle(
            x,
            y,
            Math.Max(left.Right, right.Right) - x,
            Math.Max(left.Bottom, right.Bottom) - y);
    }
}

public sealed record InsidePlotTextRegionAssemblyGroup(
    OcrDetectedRegion Region,
    IReadOnlyList<string> MemberRegionIds);
