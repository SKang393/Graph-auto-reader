// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Resolves already ambiguous OCR glyphs using accepted plotted marker evidence.</summary>
internal static class ProductionMarkerGlyphDisambiguation
{
    internal const string Version = "accepted-marker-ocr-glyph-disambiguation-v1";

    internal static MarkerGlyphDisambiguationBatch Resolve(
        OcrResult ocr, OcrRectangle plot, IReadOnlyList<ClassifiedMarker> acceptedMarkers,
        double artifactRejectionThreshold, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(acceptedMarkers);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ocr.Succeeded || ocr.CoordinateSpace != OcrContract.CoordinateSpace ||
            !plot.IsValid || plot.Left < 0 || plot.Top < 0 || !double.IsFinite(plot.Right) || !double.IsFinite(plot.Bottom) ||
            !double.IsFinite(artifactRejectionThreshold) || artifactRejectionThreshold <= 0 || artifactRejectionThreshold > 1)
            throw new ArgumentException("Marker glyph interpretation requires successful original-pixel OCR, plot and classifier policy.", nameof(ocr));
        HashSet<string> masked = ProductionTextMarkerExclusion.SelectMasks(ocr.Regions, ocr.Masks, cancellationToken)
            .Select(static mask => mask.RegionId).ToHashSet(StringComparer.Ordinal);
        var eligibleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string warning in ocr.Warnings)
        {
            const string glyphPrefix = "ocr_single_glyph_annotation_needs_review:";
            const string runPrefix = "ocr_symbol_run_annotation_needs_review:";
            if (warning.StartsWith(glyphPrefix, StringComparison.Ordinal)) eligibleIds.Add(warning[glyphPrefix.Length..]);
            else if (warning.StartsWith(runPrefix, StringComparison.Ordinal)) eligibleIds.Add(warning[runPrefix.Length..]);
        }
        var markerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ClassifiedMarker marker in acceptedMarkers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkerCenter center = marker.Marker;
            if (center.CoordinateSpace != MarkerContract.CoordinateSpace || !center.Center.IsFinite ||
                !double.IsFinite(center.Radius * 2) || center.Radius <= 0 || string.IsNullOrWhiteSpace(center.MarkerId) ||
                !double.IsFinite(marker.ArtifactProbability) || marker.ArtifactProbability is < 0 or > 1 ||
                !markerIds.Add(center.MarkerId))
                throw new ArgumentException("Accepted markers must have unique original-pixel geometry.", nameof(acceptedMarkers));
        }
        var aliases = new List<OcrRegion>();
        var warnings = new List<string>();
        foreach (OcrRegion region in ocr.Regions.OrderBy(static r => r.RegionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle box = region.Polygon.Bounds;
            if (!eligibleIds.Contains(region.RegionId) || masked.Contains(region.RegionId) ||
                region.ReviewStatus != OcrReviewStatus.Unreviewed || region.SourceImage != OcrSourceImage.Original ||
                region.Role is not (OcrTextRole.Annotation or OcrTextRole.Other) || string.IsNullOrWhiteSpace(region.Text) ||
                box.Center.X < plot.Left || box.Center.X > plot.Right || box.Center.Y < plot.Top || box.Center.Y > plot.Bottom)
                continue;
            var polygon = new MarkerPolygon(region.Polygon.Points.Select(static p => new MarkerPoint(p.X, p.Y)).ToArray());
            ClassifiedMarker[] supports = acceptedMarkers.Where(marker =>
            {
                MarkerCenter center = marker.Marker;
                if (marker.Shape == MarkerShape.Other || marker.ArtifactProbability >= artifactRejectionThreshold ||
                    center.SourceImage != MarkerSourceImage.Original ||
                    !polygon.Contains(center.Center)) return false;
                double left = center.Center.X - center.Radius, right = center.Center.X + center.Radius;
                double top = center.Center.Y - center.Radius, bottom = center.Center.Y + center.Radius;
                double overlap = Math.Max(0, Math.Min(box.Right, right) - Math.Max(box.Left, left)) *
                    Math.Max(0, Math.Min(box.Bottom, bottom) - Math.Max(box.Top, top));
                // A small false point inside a wider word is insufficient evidence.
                return overlap >= .5 * box.Width * box.Height;
            }).OrderBy(static m => m.Marker.MarkerId, StringComparer.Ordinal).ToArray();
            if (supports.Length == 0) continue;
            aliases.Add(region);
            warnings.AddRange(supports.Select(marker =>
                $"ocr_glyph_interpreted_as_marker:{region.RegionId}:{marker.Marker.MarkerId}"));
        }
        if (aliases.Count == 0) return new(ocr, [], []);
        HashSet<string> removed = aliases.Select(static r => r.RegionId).ToHashSet(StringComparer.Ordinal);
        string material = JsonSerializer.Serialize(new { Version, SourceCacheKey = ocr.Cache.CacheKey, Associations = warnings });
        OcrResult resolved = ocr with
        {
            StageVersion = ocr.StageVersion + ":" + Version,
            Regions = Array.AsReadOnly(ocr.Regions.Where(r => !removed.Contains(r.RegionId)).ToArray()),
            Warnings = Array.AsReadOnly(ocr.Warnings.Concat(warnings).ToArray()),
            Cache = ocr.Cache with
            {
                CacheHit = false,
                CacheKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material))),
            },
        };
        return new(resolved, aliases.AsReadOnly(), warnings.AsReadOnly());
    }
}

internal sealed record MarkerGlyphDisambiguationBatch(
    OcrResult Result,
    IReadOnlyList<OcrRegion> MarkerGlyphs,
    IReadOnlyList<string> Warnings);
