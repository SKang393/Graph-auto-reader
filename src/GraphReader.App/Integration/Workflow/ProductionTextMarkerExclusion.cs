// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.Frozen;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Preserves OCR's mask eligibility through marker input and final exclusion.</summary>
internal static class ProductionTextMarkerExclusion
{
    internal const string Version = "original-pixel-ocr-text-exclusion-v2";

    internal static TextMarkerExclusionBatch Find(
        IReadOnlyList<ClassifiedMarker> markers,
        IReadOnlyList<OcrRegion> regions,
        IReadOnlyList<OcrMask> masks,
        CancellationToken cancellationToken)
    {
        HashSet<string> maskRegionIds = SelectMasks(regions, masks, cancellationToken)
            .Select(static mask => mask.RegionId).ToHashSet(StringComparer.Ordinal);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (OcrRegion region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (region.ReviewStatus == OcrReviewStatus.Rejected || string.IsNullOrWhiteSpace(region.Text)) continue;
            var polygon = new MarkerPolygon(region.Polygon.Points
                .Select(static point => new MarkerPoint(point.X, point.Y)).ToArray());
            foreach (ClassifiedMarker marker in markers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!polygon.Contains(marker.Marker.Center)) continue;
                if (!maskRegionIds.Contains(region.RegionId))
                {
                    warnings.Add($"marker_ocr_overlap_needs_review:{marker.Marker.MarkerId}:{region.RegionId}");
                    continue;
                }
                excluded.Add(marker.Marker.MarkerId);
                warnings.Add($"marker_excluded_by_ocr_text:{marker.Marker.MarkerId}:{region.RegionId}");
            }
        }
        return new(excluded.ToFrozenSet(StringComparer.Ordinal), Array.AsReadOnly(warnings.ToArray()));
    }

    internal static IReadOnlyList<OcrMask> SelectMasks(
        IReadOnlyList<OcrRegion> regions,
        IReadOnlyList<OcrMask> masks,
        CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, OcrRegion>(StringComparer.Ordinal);
        foreach (OcrRegion region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (region.CoordinateSpace != OcrContract.CoordinateSpace || !byId.TryAdd(region.RegionId, region))
                throw new ArgumentException("Text exclusion requires unique original-pixel OCR regions.", nameof(regions));
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<OcrMask>();
        foreach (OcrMask mask in masks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mask.CoordinateSpace != OcrContract.CoordinateSpace ||
                !byId.TryGetValue(mask.RegionId, out OcrRegion? region) || !seen.Add(mask.RegionId))
                throw new ArgumentException("Each original-pixel OCR mask must bind one unique recognized region.", nameof(masks));
            if (region.ReviewStatus != OcrReviewStatus.Rejected && !string.IsNullOrWhiteSpace(region.Text))
                selected.Add(mask);
        }
        return selected.AsReadOnly();
    }
}

internal sealed record TextMarkerExclusionBatch(
    IReadOnlySet<string> ExcludedMarkerIds,
    IReadOnlyList<string> Warnings);
