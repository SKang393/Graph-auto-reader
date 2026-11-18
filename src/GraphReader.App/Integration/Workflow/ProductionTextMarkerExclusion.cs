// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.Frozen;
using System.Text;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Preserves OCR's mask eligibility through marker input and final exclusion.</summary>
internal static class ProductionTextMarkerExclusion
{
    internal const string Version = "original-pixel-ocr-text-exclusion-v3-corroborated-disks";

    internal static TextMarkerExclusionBatch Find(
        IReadOnlyList<ClassifiedMarker> markers,
        IReadOnlyList<OcrRegion> regions,
        IReadOnlyList<OcrMask> masks,
        OcrImage original,
        MarkerPolygon plot,
        IReadOnlyList<OcrRectangle> legendFrames,
        double artifactThreshold,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (original.SourceImage != OcrSourceImage.Original || original.OriginalToImage != OcrFrameTransform.Identity ||
            original.CoordinateSpace != OcrContract.CoordinateSpace || original.Width <= 0 || original.Height <= 0 ||
            original.Stride < original.Width || original.Pixels.Length != checked(original.Stride * original.Height))
            throw new ArgumentException("Text exclusion requires aligned original grayscale pixels.", nameof(original));
        HashSet<string> maskRegionIds = SelectMasks(regions, masks, cancellationToken)
            .Select(static mask => mask.RegionId).ToHashSet(StringComparer.Ordinal);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        int[]? independentRadii = null;
        bool IsPlotDisk(ClassifiedMarker marker) => marker.Marker.SourceImage == MarkerSourceImage.Original &&
            marker.Shape == MarkerShape.Circle && marker.Fill == MarkerFill.Filled &&
            marker.ArtifactProbability < artifactThreshold && plot.Contains(marker.Marker.Center);

        int[] MeasureIndependentDisks()
        {
            var textBounds = masks.Where(m => maskRegionIds.Contains(m.RegionId)).Select(m => m.Polygon.Bounds)
                .Concat(regions.Where(r => maskRegionIds.Contains(r.RegionId)).Select(r => r.Polygon.Bounds))
                .Concat(legendFrames).ToArray();
            // A witness patch must be clear of text, padded masks and legend frames.
            bool ClearOfText(MarkerCenter marker)
            {
                double margin = Math.Ceiling(marker.Radius / 2) + Math.Ceiling(2 * marker.Radius) + 3;
                MarkerPoint center = marker.Center;
                return textBounds.All(box => center.X + margin < box.Left || center.X - margin > box.Right ||
                    center.Y + margin < box.Top || center.Y - margin > box.Bottom);
            }
            return markers.Where(m => IsPlotDisk(m) && ClearOfText(m.Marker))
                .Select(m => OriginalPixelDiskRadius.Measure(original, m.Marker, cancellationToken))
                .Where(radius => radius > 0).Distinct().ToArray();
        }
        foreach (OcrRegion region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (region.ReviewStatus == OcrReviewStatus.Rejected || string.IsNullOrWhiteSpace(region.Text)) continue;
            var polygon = new MarkerPolygon(region.Polygon.Points
                .Select(static point => new MarkerPoint(point.X, point.Y)).ToArray());
            bool ambiguousRun = IsMixedShapeRun(region);
            foreach (ClassifiedMarker marker in markers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!polygon.Contains(marker.Marker.Center)) continue;
                if (!maskRegionIds.Contains(region.RegionId))
                {
                    warnings.Add($"marker_ocr_overlap_needs_review:{marker.Marker.MarkerId}:{region.RegionId}");
                    continue;
                }
                if (ambiguousRun && IsPlotDisk(marker))
                {
                    int radius = OriginalPixelDiskRadius.Measure(original, marker.Marker, cancellationToken);
                    // A punctuation dot can be round too. Require a glyph-sized disk and
                    // independently measured ink of the same size outside the text masks.
                    if (radius > 0 && 2 * radius >= region.Polygon.Bounds.Height / 2 &&
                        (independentRadii ??= MeasureIndependentDisks()).Any(r => Math.Abs(r - radius) <= 1))
                    {
                        warnings.Add($"marker_ocr_symbol_run_disk_needs_review:{marker.Marker.MarkerId}:{region.RegionId}");
                        continue;
                    }
                }
                excluded.Add(marker.Marker.MarkerId);
                warnings.Add($"marker_excluded_by_ocr_text:{marker.Marker.MarkerId}:{region.RegionId}");
            }
        }
        return new(excluded.ToFrozenSet(StringComparer.Ordinal), Array.AsReadOnly(warnings.ToArray()));
    }

    private static bool IsMixedShapeRun(OcrRegion region)
    {
        if (region.ReviewStatus != OcrReviewStatus.Unreviewed || region.SourceImage != OcrSourceImage.Original ||
            region.Role is not (OcrTextRole.Annotation or OcrTextRole.Other)) return false;

        bool geometric = false, roundLetterOrDigit = false;
        foreach (Rune rune in region.Text.EnumerateRunes())
        {
            if (rune.Value is >= 0x25A0 and <= 0x25FF) geometric = true; // Unicode Geometric Shapes
            else if (rune.Value is 'O' or 'o' or '0') roundLetterOrDigit = true;
            else if (!Rune.IsWhiteSpace(rune) && rune.Value is not ('.' or '·' or '•' or '+' or '×' or '*')) return false;
        }
        return geometric && roundLetterOrDigit;
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
