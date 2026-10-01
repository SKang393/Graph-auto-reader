// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

public sealed record RecoveredHeaderWord(IReadOnlyList<string> SourceRegionIds, OcrDetectedRegion Region);

/// <summary>Reads adjacent recovered header fragments together from their measured original ink.</summary>
public static class HeaderFragmentWordRecovery
{
    public const string CompositionVersion = "original-pixel-header-fragment-word-separate-batch-v1";

    public static async ValueTask<IReadOnlyList<RecoveredHeaderWord>> FindAsync(
        OcrImage image, IReadOnlyList<OcrDetectedRegion> detected, IReadOnlyList<OcrRegion> recognized,
        OcrRectangle plot, IReadOnlyList<double> phaseDividerXs, CancellationToken cancellationToken = default)
    {
        _ = new HeaderBracketEvidence(image, cancellationToken);
        if (!recognized.Any(static region => IsRecoveredFragment(region.RegionId))) return [];
        var detector = new ConnectedComponentTextRegionDetector(
            new ConnectedComponentTextRegionDetectorOptions { GroupComponentsIntoLines = false });
        IReadOnlyList<OcrDetectedRegion> components = await detector.DetectAsync(image, cancellationToken).ConfigureAwait(false);
        return SelectCandidates(components, detected, recognized, plot, phaseDividerXs, cancellationToken);
    }

    public static IReadOnlyList<RecoveredHeaderWord> SelectCandidates(
        IReadOnlyList<OcrDetectedRegion> components, IReadOnlyList<OcrDetectedRegion> detected,
        IReadOnlyList<OcrRegion> recognized, OcrRectangle plot, IReadOnlyList<double> phaseDividerXs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(recognized);
        ArgumentNullException.ThrowIfNull(phaseDividerXs);
        cancellationToken.ThrowIfCancellationRequested();
        if (!plot.IsValid || plot.Left < 0 || plot.Top < 0 || !double.IsFinite(plot.Right) || !double.IsFinite(plot.Bottom) ||
            phaseDividerXs.Any(x => !double.IsFinite(x) || x < plot.Left || x > plot.Right))
            throw new ArgumentException("Header word recovery requires measured original-pixel plot and divider geometry.", nameof(plot));
        foreach (OcrDetectedRegion component in components)
        {
            OcrRectangle bounds = component.Polygon.Bounds;
            if (!bounds.IsValid || bounds.Left < 0 || bounds.Top < 0 || !double.IsFinite(bounds.Right) ||
                !double.IsFinite(bounds.Bottom) || component.CoordinateSpace != OcrContract.CoordinateSpace)
                throw new ArgumentException("Header word components require original-pixel bounds.", nameof(components));
        }
        OcrRectangle[] band = HeaderGlyphTextRegionRecovery.FindCorroboratedHeadingBand(detected, recognized, plot, cancellationToken);
        if (band.Length == 0) return [];
        double height = HeaderGlyphTextRegionRecovery.Median(band.Select(static b => b.Height));
        double centerY = HeaderGlyphTextRegionRecovery.Median(band.Select(static b => b.Center.Y));
        var byId = detected.ToDictionary(static r => r.RegionId, StringComparer.Ordinal);
        var remaining = components.Where(component =>
        {
            OcrRectangle b = component.Polygon.Bounds;
            return b.Left >= plot.Left && b.Right <= plot.Right && b.Bottom <= plot.Top &&
                b.Height >= height / 2 && b.Height <= height * 2 && b.Width <= height * 2 &&
                Math.Abs(b.Center.Y - centerY) <= height / 2;
        }).OrderBy(static c => c.Polygon.Bounds.Left).ThenBy(static c => c.Polygon.Bounds.Top)
            .ThenBy(static c => c.Polygon.Bounds.Right).ThenBy(static c => c.Polygon.Bounds.Bottom)
            .ThenBy(static c => c.RegionId, StringComparer.Ordinal).ToList();
        var result = new List<RecoveredHeaderWord>();
        while (remaining.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle word = remaining[0].Polygon.Bounds;
            var members = new List<OcrDetectedRegion> { remaining[0] };
            remaining.RemoveAt(0);
            bool changed;
            do
            {
                changed = false;
                foreach (OcrDetectedRegion component in remaining.ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    OcrRectangle glyph = component.Polygon.Bounds;
                    OcrRectangle union = Union(word, glyph);
                    double gap = Math.Max(0, Math.Max(glyph.Left - word.Right, word.Left - glyph.Right));
                    // Reuse the existing word-completion overlap, gap and height envelope.
                    // Thin stems are valid word members even when not standalone glyph crops.
                    if (union.Height > 1.6 * height || VerticalOverlap(word, glyph) < .35 * Math.Min(word.Height, glyph.Height) ||
                        gap > Math.Min(word.Height, glyph.Height)) continue;
                    word = union;
                    members.Add(component);
                    remaining.Remove(component);
                    changed = true;
                }
            } while (changed);
            if (word.Width < 2 * word.Height || phaseDividerXs.Any(x => word.Left < x && x < word.Right)) continue;
            OcrRegion[] covered = recognized.Where(r => Overlap(word, r.Polygon.Bounds) > 0).ToArray();
            if (covered.Count(static r => IsRecoveredFragment(r.RegionId)) < 2 ||
                covered.Any(r => !CanReplace(r, word, byId))) continue;
            HashSet<string> sourceIds = covered.Select(static r => r.RegionId).ToHashSet(StringComparer.Ordinal);
            // A detection without a bound successful reading is still evidence of ambiguity.
            if (detected.Any(r => Overlap(word, r.Polygon.Bounds) > 0 && !sourceIds.Contains(r.RegionId))) continue;

            string[] ids = sourceIds.Order(StringComparer.Ordinal).ToArray();
            string material = $"{CompositionVersion}\n{string.Join('\n', ids)}\n" +
                string.Join('\n', members.Select(static c => c.RegionId).Order(StringComparer.Ordinal));
            result.Add(new(OcrCollections.Freeze(ids), new OcrDetectedRegion(
                "header-fragment-word:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material))),
                OcrPolygon.FromRectangle(word), 0, Math.Min(members.Min(static c => c.DetectionConfidence),
                    ids.Min(id => byId[id].DetectionConfidence)),
                Context: null, Evidence: null)));
        }
        return OcrCollections.Freeze(result);
    }

    private static bool CanReplace(OcrRegion region, OcrRectangle word, Dictionary<string, OcrDetectedRegion> detected) =>
        region.ReviewStatus == OcrReviewStatus.Unreviewed && region.CoordinateSpace == OcrContract.CoordinateSpace &&
        region.Role is OcrTextRole.PhaseHeading or OcrTextRole.Other &&
        Contains(word, region.Polygon.Bounds) && detected.TryGetValue(region.RegionId, out OcrDetectedRegion? source) &&
        source.CoordinateSpace == OcrContract.CoordinateSpace && source.Polygon.Points.SequenceEqual(region.Polygon.Points) &&
        GraphTextRoleClassifier.GetOrientation(source.OrientationDegrees) == OcrOrientation.Horizontal &&
        source.Context?.ExplicitRoleHint is null && source.Context is not ({ NumericExpected: true } or { AxisTitleExpected: true } or
            { NearLegendGlyph: true } or { NearAnnotationArrow: true } or { InParticipantBand: true });

    private static bool IsRecoveredFragment(string id) => id.StartsWith("header-glyph:", StringComparison.Ordinal);
    private static bool Contains(OcrRectangle a, OcrRectangle b) => a.Left <= b.Left && a.Top <= b.Top && a.Right >= b.Right && a.Bottom >= b.Bottom;
    private static double VerticalOverlap(OcrRectangle a, OcrRectangle b) => Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    private static double Overlap(OcrRectangle a, OcrRectangle b) => VerticalOverlap(a, b) * Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
    private static OcrRectangle Union(OcrRectangle a, OcrRectangle b) => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top),
        Math.Max(a.Right, b.Right) - Math.Min(a.Left, b.Left), Math.Max(a.Bottom, b.Bottom) - Math.Min(a.Top, b.Top));
}
