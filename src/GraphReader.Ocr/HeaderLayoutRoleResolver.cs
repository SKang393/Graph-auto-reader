// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

/// <summary>
/// Separates detached notes from a corroborated row of phase headings.
/// Text, numeric values, region geometry, and explicit role evidence are retained.
/// </summary>
public static class HeaderLayoutRoleResolver
{
    public const string CompositionVersion = "detached-header-note-context-v1";
    private const double MinimumVerticalOverlapRatio = 0.35;
    private const double DetachedNoteConfidence = 0.64;

    public static HeaderLayoutRoleResolution Resolve(
        IReadOnlyList<OcrRegion> regions,
        IReadOnlyList<OcrDetectedRegion> detectedRegions,
        OcrRectangle plotBounds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(detectedRegions);
        if (!plotBounds.IsValid)
        {
            throw new ArgumentException("Plot bounds must be finite and positive.", nameof(plotBounds));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var detectedById = detectedRegions.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        var headings = new List<OcrRegion>();
        foreach (OcrRegion region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle bounds = region.Polygon.Bounds;
            if (!bounds.IsValid || region.CoordinateSpace != OcrContract.CoordinateSpace ||
                !detectedById.TryGetValue(region.RegionId, out OcrDetectedRegion? detected))
            {
                throw new ArgumentException("Recognized regions must retain valid original detection geometry.", nameof(regions));
            }
            if (region.Role == OcrTextRole.PhaseHeading && region.ReviewStatus != OcrReviewStatus.Rejected &&
                bounds.Bottom <= plotBounds.Top &&
                bounds.Center.X >= plotBounds.Left && bounds.Center.X <= plotBounds.Right &&
                GraphTextRoleClassifier.GetOrientation(detected.OrientationDegrees) == OcrOrientation.Horizontal)
            {
                headings.Add(region);
            }
        }

        OcrRegion[] band = [];
        foreach (OcrRegion anchor in headings.OrderBy(static region => region.RegionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle anchorBounds = anchor.Polygon.Bounds;
            OcrRegion[] candidates = headings.Where(region =>
            {
                OcrRectangle bounds = region.Polygon.Bounds;
                double overlap = Math.Max(0, Math.Min(anchorBounds.Bottom, bounds.Bottom) -
                    Math.Max(anchorBounds.Top, bounds.Top));
                return overlap >= MinimumVerticalOverlapRatio * Math.Min(anchorBounds.Height, bounds.Height);
            }).ToArray();
            if (candidates.Length > band.Length || (candidates.Length == band.Length &&
                candidates.Max(static region => region.Polygon.Bounds.Bottom) >
                band.Max(static region => region.Polygon.Bounds.Bottom)))
            {
                band = candidates;
            }
        }
        if (band.Length < 2 || !band.Any(left => band.Any(right =>
                left.Polygon.Bounds.Right < right.Polygon.Bounds.Left)))
        {
            return new HeaderLayoutRoleResolution(OcrCollections.Freeze(regions), Array.Empty<string>());
        }

        double[] heights = band.Select(static region => region.Polygon.Bounds.Height).Order().ToArray();
        int middle = heights.Length / 2;
        double typicalHeight = heights.Length % 2 == 0
            ? (heights[middle - 1] + heights[middle]) / 2
            : heights[middle];
        double bandTop = band.Min(static region => region.Polygon.Bounds.Top);
        var detached = new HashSet<string>(StringComparer.Ordinal);
        foreach (OcrRegion region in headings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRegionContext? context = detectedById[region.RegionId].Context;
            if (region.ReviewStatus == OcrReviewStatus.Unreviewed &&
                context?.ExplicitRoleHint is null && context?.NearPhaseDivider is not true &&
                bandTop - region.Polygon.Bounds.Bottom >= typicalHeight)
            {
                detached.Add(region.RegionId);
            }
        }

        return new HeaderLayoutRoleResolution(
            OcrCollections.Freeze(regions.Select(region => detached.Contains(region.RegionId)
                ? region with { Role = OcrTextRole.Annotation, Confidence = Math.Min(region.Confidence, DetachedNoteConfidence) }
                : region)),
            OcrCollections.Freeze(detached.Order(StringComparer.Ordinal)));
    }
}

public sealed record HeaderLayoutRoleResolution(
    IReadOnlyList<OcrRegion> Regions,
    IReadOnlyList<string> DetachedRegionIds);
