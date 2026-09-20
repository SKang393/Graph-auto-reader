// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.Frozen;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Retains recognized text geometry as an exclusion after marker classification.</summary>
internal static class ProductionTextMarkerExclusion
{
    internal const string Version = "original-pixel-ocr-text-exclusion-v1";

    internal static TextMarkerExclusionBatch Find(
        IReadOnlyList<ClassifiedMarker> markers,
        IReadOnlyList<OcrRegion> regions,
        CancellationToken cancellationToken)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (OcrRegion region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (region.ReviewStatus == OcrReviewStatus.Rejected || string.IsNullOrWhiteSpace(region.Text)) continue;
            if (region.CoordinateSpace != OcrContract.CoordinateSpace)
                throw new ArgumentException("Text exclusion requires original-pixel OCR evidence.", nameof(regions));
            var polygon = new MarkerPolygon(region.Polygon.Points
                .Select(static point => new MarkerPoint(point.X, point.Y)).ToArray());
            foreach (ClassifiedMarker marker in markers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!polygon.Contains(marker.Marker.Center)) continue;
                excluded.Add(marker.Marker.MarkerId);
                warnings.Add($"marker_excluded_by_ocr_text:{marker.Marker.MarkerId}:{region.RegionId}");
            }
        }
        return new(excluded.ToFrozenSet(StringComparer.Ordinal), Array.AsReadOnly(warnings.ToArray()));
    }
}

internal sealed record TextMarkerExclusionBatch(
    IReadOnlySet<string> ExcludedMarkerIds,
    IReadOnlyList<string> Warnings);
