// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

/// <summary>Completes a clipped heading crop from adjacent original-pixel glyphs.</summary>
public static class HeaderWordSuffixRecovery
{
    public const string CompositionVersion = "original-pixel-heading-word-suffix-separate-batch-v2";

    public static async ValueTask<IReadOnlyList<OcrDetectedRegion>> FindAsync(
        OcrImage image, IReadOnlyList<OcrDetectedRegion> detected,
        IReadOnlyList<OcrRegion> recognized, OcrRectangle plot,
        IReadOnlyList<double> phaseDividerXs, CancellationToken cancellationToken = default)
    {
        // Validate the immutable image before deriving any components.
        _ = new HeaderBracketEvidence(image, cancellationToken);
        var detector = new ConnectedComponentTextRegionDetector(
            new ConnectedComponentTextRegionDetectorOptions { GroupComponentsIntoLines = false });
        var components = await detector.DetectAsync(image, cancellationToken).ConfigureAwait(false);
        return SelectCandidates(image, components, detected, recognized, plot, phaseDividerXs, cancellationToken);
    }

    public static IReadOnlyList<OcrDetectedRegion> SelectCandidates(
        OcrImage image, IReadOnlyList<OcrDetectedRegion> components,
        IReadOnlyList<OcrDetectedRegion> detected, IReadOnlyList<OcrRegion> recognized,
        OcrRectangle plot, IReadOnlyList<double> phaseDividerXs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(recognized);
        ArgumentNullException.ThrowIfNull(phaseDividerXs);
        var brackets = new HeaderBracketEvidence(image, cancellationToken);
        if (!plot.IsValid || !double.IsFinite(plot.Right) || !double.IsFinite(plot.Bottom) ||
            plot.Left < 0 || plot.Top < 0 || plot.Right > image.Width || plot.Bottom > image.Height ||
            phaseDividerXs.Any(x => !double.IsFinite(x) || x < plot.Left || x > plot.Right))
            throw new ArgumentException("Heading completion requires measured plot and phase geometry.", nameof(plot));

        var byId = detected.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        var ordered = components.OrderBy(static region => region.Polygon.Bounds.Left)
            .ThenBy(static region => region.Polygon.Bounds.Top)
            .ThenBy(static region => region.RegionId, StringComparer.Ordinal).ToArray();
        foreach (var component in ordered)
        {
            OcrRectangle bounds = component.Polygon.Bounds;
            if (!bounds.IsValid || component.CoordinateSpace != OcrContract.CoordinateSpace ||
                bounds.Left < 0 || bounds.Top < 0 || bounds.Right > image.Width || bounds.Bottom > image.Height)
                throw new ArgumentException("Heading components must stay in original pixels.", nameof(components));
        }
        var result = new List<OcrDetectedRegion>();
        foreach (var heading in recognized.OrderBy(static region => region.Polygon.Bounds.Top)
                     .ThenBy(static region => region.Polygon.Bounds.Left)
                     .ThenBy(static region => region.RegionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (heading.Role != OcrTextRole.PhaseHeading || heading.ReviewStatus != OcrReviewStatus.Unreviewed ||
                GraphNumericParser.IsLiteralGraphNumber(heading.Text) ||
                !byId.TryGetValue(heading.RegionId, out var source) ||
                !source.Polygon.Points.SequenceEqual(heading.Polygon.Points) ||
                GraphTextRoleClassifier.GetOrientation(source.OrientationDegrees) != OcrOrientation.Horizontal)
                continue;
            OcrRectangle anchor = heading.Polygon.Bounds;
            if (!anchor.IsValid || anchor.Left < plot.Left || anchor.Right > plot.Right ||
                anchor.Top < 0 || anchor.Bottom > plot.Top || anchor.Width < 2 * anchor.Height)
                continue;
            OcrRectangle completed = anchor;
            var members = new List<OcrDetectedRegion>();
            foreach (var component in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OcrRectangle glyph = component.Polygon.Bounds;
                double gap = glyph.Left - completed.Right;
                double height = Math.Max(anchor.Height, glyph.Height);
                var union = new OcrRectangle(anchor.Left, Math.Min(completed.Top, glyph.Top),
                    glyph.Right - anchor.Left, Math.Max(completed.Bottom, glyph.Bottom) - Math.Min(completed.Top, glyph.Top));
                // Use the existing header-glyph size and word-assembly alignment limits.
                if (gap <= 0 || gap > Math.Min(anchor.Height, glyph.Height) ||
                    glyph.Height < anchor.Height / 2 || glyph.Height > anchor.Height * 2 ||
                    glyph.Width < anchor.Height * .3 || glyph.Width > anchor.Height * 2 ||
                    Math.Abs(anchor.Center.Y - glyph.Center.Y) > .1 * height ||
                    glyph.Right > plot.Right || glyph.Bottom > plot.Top ||
                    phaseDividerXs.Any(x => union.Left < x && x < union.Right) ||
                    detected.Any(region => region.RegionId != source.RegionId &&
                        Overlap(union, region.Polygon.Bounds) >= .5 * Area(region.Polygon.Bounds)) ||
                    (brackets.HasBracketBelow(anchor, plot.Top, cancellationToken) &&
                        !brackets.HasBracketBelow(union, plot.Top, cancellationToken)))
                    continue;
                completed = union;
                members.Add(component);
            }
            if (members.Count == 0) continue;
            string material = $"{CompositionVersion}\n{source.RegionId}\n{string.Join('\n', members.Select(static item => item.RegionId))}";
            result.Add(source with
            {
                RegionId = "header-word-suffix:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material))),
                Polygon = OcrPolygon.FromRectangle(completed),
                DetectionConfidence = Math.Min(source.DetectionConfidence, members.Min(static item => item.DetectionConfidence)),
                Evidence = null,
            });
        }
        return OcrCollections.Freeze(result);
    }

    private static double Area(OcrRectangle bounds) => bounds.Width * bounds.Height;

    private static double Overlap(OcrRectangle a, OcrRectangle b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
}
