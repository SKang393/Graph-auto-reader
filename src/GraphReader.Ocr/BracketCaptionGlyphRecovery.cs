// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

/// <summary>Finds one detached glyph beside a caption supported by an original-pixel bracket.</summary>
public static class BracketCaptionGlyphRecovery
{
    public const string CompositionVersion = "original-pixel-bracket-caption-glyph-separate-batch-v1";

    public static async ValueTask<IReadOnlyList<OcrDetectedRegion>> FindAsync(
        OcrImage image, IReadOnlyList<OcrDetectedRegion> detected, IReadOnlyList<OcrRegion> recognized,
        OcrRectangle plot, CancellationToken cancellationToken = default)
    {
        var brackets = new HeaderBracketEvidence(image, cancellationToken);
        var detector = new ConnectedComponentTextRegionDetector(
            new ConnectedComponentTextRegionDetectorOptions { GroupComponentsIntoLines = false });
        IReadOnlyList<OcrDetectedRegion> components = await detector.DetectAsync(image, cancellationToken).ConfigureAwait(false);
        return SelectCandidates(image, brackets, components, detected, recognized, plot, cancellationToken);
    }

    public static IReadOnlyList<OcrDetectedRegion> SelectCandidates(
        OcrImage image, IReadOnlyList<OcrDetectedRegion> components, IReadOnlyList<OcrDetectedRegion> detected,
        IReadOnlyList<OcrRegion> recognized, OcrRectangle plot, CancellationToken cancellationToken = default) =>
        SelectCandidates(image, new HeaderBracketEvidence(image, cancellationToken), components,
            detected, recognized, plot, cancellationToken);

    private static IReadOnlyList<OcrDetectedRegion> SelectCandidates(
        OcrImage image, HeaderBracketEvidence brackets, IReadOnlyList<OcrDetectedRegion> components,
        IReadOnlyList<OcrDetectedRegion> detected, IReadOnlyList<OcrRegion> recognized, OcrRectangle plot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(recognized);
        cancellationToken.ThrowIfCancellationRequested();
        if (!InsideImage(plot, image))
            throw new ArgumentException("Bracket glyph recovery requires measured original-pixel plot geometry.", nameof(plot));
        foreach (OcrDetectedRegion component in components)
        {
            if (!InsideImage(component.Polygon.Bounds, image) || component.CoordinateSpace != OcrContract.CoordinateSpace)
                throw new ArgumentException("Bracket glyph components require valid original-pixel bounds.", nameof(components));
        }
        var byId = detected.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        var selected = new List<OcrDetectedRegion>();
        foreach (OcrRegion caption in recognized.OrderBy(static r => r.Polygon.Bounds.Top)
                     .ThenBy(static r => r.Polygon.Bounds.Left).ThenBy(static r => r.RegionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (caption.ReviewStatus != OcrReviewStatus.Unreviewed || caption.SourceImage != OcrSourceImage.Original ||
                caption.CoordinateSpace != OcrContract.CoordinateSpace ||
                caption.Role is not (OcrTextRole.Annotation or OcrTextRole.Other or OcrTextRole.PhaseHeading) ||
                string.IsNullOrWhiteSpace(caption.Text) || GraphNumericParser.IsLiteralGraphNumber(caption.Text) ||
                !byId.TryGetValue(caption.RegionId, out OcrDetectedRegion? source) ||
                source.CoordinateSpace != OcrContract.CoordinateSpace ||
                !source.Polygon.Points.SequenceEqual(caption.Polygon.Points) ||
                GraphTextRoleClassifier.GetOrientation(source.OrientationDegrees) != OcrOrientation.Horizontal ||
                source.Context?.ExplicitRoleHint is not null ||
                source.Context is { NumericExpected: true } or { AxisTitleExpected: true } or
                    { NearLegendGlyph: true } or { NearAnnotationArrow: true } or { InParticipantBand: true })
                continue;
            OcrRectangle cb = caption.Polygon.Bounds;
            if (!InsideImage(cb, image) || cb.Left < plot.Left || cb.Right > plot.Right || cb.Bottom > plot.Top ||
                !brackets.HasBracketBelow(cb, plot.Top, cancellationToken)) continue;
            OcrDetectedRegion[] eligible = components.Where(component =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                OcrRectangle glyph = component.Polygon.Bounds;
                double gap = Math.Max(glyph.Left - cb.Right, cb.Left - glyph.Right);
                if (glyph.Left < plot.Left || glyph.Right > plot.Right || glyph.Bottom > plot.Top ||
                    glyph.Height < cb.Height / 2 || glyph.Height > cb.Height * 2 ||
                    glyph.Width < cb.Height * .3 || glyph.Width > cb.Height * 2 ||
                    Math.Abs(glyph.Center.Y - cb.Center.Y) > cb.Height / 2 || gap <= Math.Min(cb.Height, glyph.Height) ||
                    detected.Any(other => Covered(glyph, other.Polygon.Bounds)) ||
                    recognized.Any(other => Covered(glyph, other.Polygon.Bounds))) return false;
                var union = new OcrRectangle(Math.Min(cb.Left, glyph.Left), Math.Min(cb.Top, glyph.Top),
                    Math.Max(cb.Right, glyph.Right) - Math.Min(cb.Left, glyph.Left),
                    Math.Max(cb.Bottom, glyph.Bottom) - Math.Min(cb.Top, glyph.Top));
                return brackets.HasBracketBelow(union, plot.Top, cancellationToken);
            }).ToArray();
            // Multiple components could be a word or unrelated ink. Do not choose a character by its appearance.
            if (eligible.Length != 1 || selected.Any(other => Covered(eligible[0].Polygon.Bounds, other.Polygon.Bounds)))
                continue;
            OcrDetectedRegion full = eligible[0];
            string material = $"{CompositionVersion}\n{source.RegionId}\n{full.RegionId}";
            selected.Add(full with
            {
                RegionId = "bracket-caption-glyph:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material))),
                OrientationDegrees = 0,
                DetectionConfidence = Math.Min(source.DetectionConfidence, full.DetectionConfidence),
                Context = null,
                Evidence = null,
            });
        }
        return OcrCollections.Freeze(selected);
    }

    private static bool InsideImage(OcrRectangle b, OcrImage image) => b.IsValid && b.Left >= 0 && b.Top >= 0 &&
        double.IsFinite(b.Right) && double.IsFinite(b.Bottom) && b.Right <= image.Width && b.Bottom <= image.Height;

    private static bool Covered(OcrRectangle a, OcrRectangle b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top)) >=
        .5 * Math.Min(a.Width * a.Height, b.Width * b.Height);
}
