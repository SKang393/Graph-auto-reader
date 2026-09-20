// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

public static class ParticipantLaneTextRegionAssembler
{
    public const string CompositionVersion = "participant-lane-aligned-word-assembly-v2";

    private const double MinimumVerticalOverlapRatio = 0.35;
    private const double MaximumHorizontalGapHeightRatio = 2.5;
    private const double MaximumComponentHeightRatio = 2.0;
    private const double MaximumMergedHeightGrowthRatio = 1.6;
    private const double MaximumVerticalCenterOffsetHeightRatio = 0.10;

    public static IReadOnlyList<OcrDetectedRegion> Assemble(
        IReadOnlyList<OcrDetectedRegion> regions,
        OcrRectangle plotBounds) =>
        OcrCollections.Freeze(AssembleWithMembership(regions, plotBounds)
            .Select(static group => group.Region));

    public static IReadOnlyList<ParticipantLaneTextRegionAssemblyGroup> AssembleWithMembership(
        IReadOnlyList<OcrDetectedRegion> regions,
        OcrRectangle plotBounds)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (!plotBounds.IsValid)
        {
            throw new ArgumentException(
                "Plot bounds must be finite and have positive dimensions.",
                nameof(plotBounds));
        }

        var remaining = regions
            .Select(static region => new ParticipantLaneTextRegionAssemblyGroup(
                region,
                OcrCollections.Freeze([region.RegionId])))
            .OrderBy(static group => group.Region.Polygon.Bounds.Top)
            .ThenBy(static group => group.Region.Polygon.Bounds.Left)
            .ThenBy(static group => group.Region.Polygon.Bounds.Bottom)
            .ThenBy(static group => group.Region.Polygon.Bounds.Right)
            .ThenBy(static group => group.Region.RegionId, StringComparer.Ordinal)
            .ToList();
        var assembled = new List<ParticipantLaneTextRegionAssemblyGroup>(remaining.Count);

        while (remaining.Count > 0)
        {
            ParticipantLaneTextRegionAssemblyGroup line = remaining[0];
            remaining.RemoveAt(0);
            bool changed;
            do
            {
                changed = false;
                for (int index = remaining.Count - 1; index >= 0; index--)
                {
                    ParticipantLaneTextRegionAssemblyGroup candidate = remaining[index];
                    OcrRectangle mergedBounds = Union(
                        line.Region.Polygon.Bounds,
                        candidate.Region.Polygon.Bounds);
                    if (!CanMerge(line.Region.Polygon.Bounds, candidate.Region.Polygon.Bounds) ||
                        !IsInsideParticipantLane(mergedBounds, plotBounds))
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

    private static ParticipantLaneTextRegionAssemblyGroup Merge(
        ParticipantLaneTextRegionAssemblyGroup left,
        ParticipantLaneTextRegionAssemblyGroup right,
        OcrRectangle bounds)
    {
        string[] memberIds = left.MemberRegionIds
            .Concat(right.MemberRegionIds)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string material = $"{CompositionVersion}\n{string.Join('\n', memberIds)}";
        string id = $"participant-lane:{Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";
        var region = new OcrDetectedRegion(
            id,
            OcrPolygon.FromRectangle(bounds),
            left.Region.OrientationDegrees,
            Math.Min(left.Region.DetectionConfidence, right.Region.DetectionConfidence),
            Equals(left.Region.Context, right.Region.Context) ? left.Region.Context : null,
            left.Region.CoordinateSpace,
            Evidence: null);
        return new ParticipantLaneTextRegionAssemblyGroup(region, OcrCollections.Freeze(memberIds));
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

    private static bool IsInsideParticipantLane(OcrRectangle bounds, OcrRectangle plotBounds) =>
        bounds.Center.X < plotBounds.Left &&
        (bounds.Center.Y >= plotBounds.Top || bounds.Bottom <= plotBounds.Top) &&
        bounds.Center.Y <= plotBounds.Bottom;

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

public sealed record ParticipantLaneTextRegionAssemblyGroup(
    OcrDetectedRegion Region,
    IReadOnlyList<string> MemberRegionIds);
