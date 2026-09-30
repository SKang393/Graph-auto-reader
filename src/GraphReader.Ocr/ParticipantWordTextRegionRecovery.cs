// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

/// <summary>Completes clipped peripheral words from connected original-pixel ink.</summary>
public static class ParticipantWordTextRegionRecovery
{
    public const string CompositionVersion = "original-pixel-participant-word-completion-nonnumeric-v2";

    public static async ValueTask<IReadOnlyList<OcrDetectedRegion>> FindAsync(
        OcrImage image, IReadOnlyList<OcrDetectedRegion> detected,
        IReadOnlyList<OcrRegion> recognized, OcrRectangle plot,
        CancellationToken cancellationToken = default)
    {
        _ = new HeaderBracketEvidence(image, cancellationToken);
        var detector = new ConnectedComponentTextRegionDetector(
            new ConnectedComponentTextRegionDetectorOptions { GroupComponentsIntoLines = false });
        var components = await detector.DetectAsync(image, cancellationToken).ConfigureAwait(false);
        return SelectCandidates(image, components, detected, recognized, plot, cancellationToken);
    }

    public static IReadOnlyList<OcrDetectedRegion> SelectCandidates(
        OcrImage image, IReadOnlyList<OcrDetectedRegion> components,
        IReadOnlyList<OcrDetectedRegion> detected, IReadOnlyList<OcrRegion> recognized,
        OcrRectangle plot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(recognized);
        _ = new HeaderBracketEvidence(image, cancellationToken);
        if (!plot.IsValid || !double.IsFinite(plot.Right) || !double.IsFinite(plot.Bottom) ||
            plot.Left < 0 || plot.Top < 0 || plot.Right > image.Width || plot.Bottom > image.Height)
            throw new ArgumentException("Word completion requires measured plot geometry.", nameof(plot));

        var byId = detected.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        var ordered = components.OrderBy(static region => region.Polygon.Bounds.Left)
            .ThenBy(static region => region.Polygon.Bounds.Top)
            .ThenBy(static region => region.RegionId, StringComparer.Ordinal).ToArray();
        foreach (var component in ordered)
        {
            OcrRectangle bounds = component.Polygon.Bounds;
            if (!bounds.IsValid || component.CoordinateSpace != OcrContract.CoordinateSpace ||
                bounds.Left < 0 || bounds.Top < 0 || bounds.Right > image.Width || bounds.Bottom > image.Height)
                throw new ArgumentException("Word components must stay in original pixels.", nameof(components));
        }

        var result = new List<OcrDetectedRegion>();
        foreach (var word in recognized.OrderBy(static region => region.RegionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (word.Role is not (OcrTextRole.Participant or OcrTextRole.Other) ||
                word.ReviewStatus != OcrReviewStatus.Unreviewed || word.SourceImage != OcrSourceImage.Original ||
                word.CoordinateSpace != OcrContract.CoordinateSpace ||
                word.Text.Count(char.IsLetter) < 2 || GraphNumericParser.IsLiteralGraphNumber(word.Text) ||
                !byId.TryGetValue(word.RegionId, out var source) ||
                !source.Polygon.Points.SequenceEqual(word.Polygon.Points) ||
                GraphTextRoleClassifier.GetOrientation(source.OrientationDegrees) != OcrOrientation.Horizontal)
                continue;
            OcrRectangle anchor = word.Polygon.Bounds;
            if (!anchor.IsValid || anchor.Left < 0 || anchor.Top < 0 || anchor.Width < 2 * anchor.Height ||
                anchor.Center.X >= plot.Left || anchor.Bottom > plot.Top)
                continue;

            OcrRectangle completed = anchor;
            var members = new HashSet<OcrDetectedRegion>();
            bool changed;
            do
            {
                changed = false;
                foreach (var component in ordered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    OcrRectangle glyph = component.Polygon.Bounds;
                    OcrRectangle union = Union(completed, glyph);
                    double overlap = Math.Min(anchor.Bottom, glyph.Bottom) - Math.Max(anchor.Top, glyph.Top);
                    double gap = Math.Max(0, Math.Max(glyph.Left - completed.Right, completed.Left - glyph.Right));
                    // Keep the existing word-assembly size/overlap envelope, but
                    // allow a clipped edge glyph to overlap the detector crop.
                    if (union == completed || glyph.Height < anchor.Height / 2 || glyph.Height > 2 * anchor.Height ||
                        glyph.Width > 2 * anchor.Height || overlap < .35 * Math.Min(anchor.Height, glyph.Height) ||
                        gap > Math.Min(anchor.Height, glyph.Height) || !CanComplete(union, source, anchor, plot, detected))
                        continue;
                    completed = union;
                    members.Add(component);
                    changed = true;
                }
            } while (changed);

            // A word must gain at least half a glyph-height horizontally. Mere
            // edge padding must not add a second reading of a complete label.
            if (anchor.Left - completed.Left + completed.Right - anchor.Right < anchor.Height / 2)
                continue;
            foreach (var component in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OcrRectangle glyph = component.Polygon.Bounds;
                OcrRectangle union = Union(completed, glyph);
                double gap = Math.Max(0, Math.Max(glyph.Top - completed.Bottom, completed.Top - glyph.Bottom));
                // Include detached dots/accents only within the completed word.
                if (glyph.Left < completed.Left || glyph.Right > completed.Right ||
                    glyph.Height >= anchor.Height / 2 || glyph.Width > anchor.Height / 2 ||
                    gap > anchor.Height / 2 || union == completed || !CanComplete(union, source, anchor, plot, detected))
                    continue;
                completed = union;
                members.Add(component);
            }

            string material = $"{CompositionVersion}\n{source.RegionId}\n" +
                string.Join('\n', members.Select(static item => item.RegionId).Order(StringComparer.Ordinal));
            result.Add(source with
            {
                RegionId = "participant-word:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material))),
                Polygon = OcrPolygon.FromRectangle(completed),
                DetectionConfidence = Math.Min(source.DetectionConfidence, members.Min(static item => item.DetectionConfidence)),
                Evidence = null,
            });
        }
        return OcrCollections.Freeze(result);
    }

    private static bool CanComplete(OcrRectangle union, OcrDetectedRegion source, OcrRectangle anchor,
        OcrRectangle plot, IReadOnlyList<OcrDetectedRegion> detected) =>
        union.Center.X < plot.Left && union.Bottom <= plot.Top && union.Height <= 1.6 * anchor.Height &&
        !detected.Any(region => region.RegionId != source.RegionId &&
            Overlap(union, region.Polygon.Bounds) >= .5 * region.Polygon.Bounds.Width * region.Polygon.Bounds.Height);

    private static OcrRectangle Union(OcrRectangle a, OcrRectangle b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top),
            Math.Max(a.Right, b.Right) - Math.Min(a.Left, b.Left),
            Math.Max(a.Bottom, b.Bottom) - Math.Min(a.Top, b.Top));

    private static double Overlap(OcrRectangle a, OcrRectangle b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
}
