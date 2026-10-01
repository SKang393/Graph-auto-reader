// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

public sealed record CompletedHeaderGlyph(string SourceRegionId, OcrDetectedRegion Region);

/// <summary>Completes a truncated single-glyph crop from one measured ink component.</summary>
public static class HeaderGlyphCropCompletion
{
    public const string CompositionVersion = "original-pixel-header-glyph-completion-separate-batch-v1";

    public static async ValueTask<IReadOnlyList<CompletedHeaderGlyph>> FindAsync(
        OcrImage image, IReadOnlyList<OcrDetectedRegion> detected,
        IReadOnlyList<OcrRegion> recognized, OcrRectangle plot,
        CancellationToken cancellationToken = default)
    {
        _ = new HeaderBracketEvidence(image, cancellationToken);
        var detector = new ConnectedComponentTextRegionDetector(
            new ConnectedComponentTextRegionDetectorOptions { GroupComponentsIntoLines = false });
        IReadOnlyList<OcrDetectedRegion> components = await detector.DetectAsync(image, cancellationToken).ConfigureAwait(false);
        return SelectCandidates(components, detected, recognized, plot, cancellationToken);
    }

    public static IReadOnlyList<CompletedHeaderGlyph> SelectCandidates(
        IReadOnlyList<OcrDetectedRegion> components, IReadOnlyList<OcrDetectedRegion> detected,
        IReadOnlyList<OcrRegion> recognized, OcrRectangle plot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(recognized);
        cancellationToken.ThrowIfCancellationRequested();
        if (!plot.IsValid || plot.Left < 0 || plot.Top < 0 ||
            !double.IsFinite(plot.Right) || !double.IsFinite(plot.Bottom))
            throw new ArgumentException("Glyph completion requires measured original-pixel plot geometry.", nameof(plot));
        foreach (OcrDetectedRegion component in components)
        {
            OcrRectangle bounds = component.Polygon.Bounds;
            if (!bounds.IsValid || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom) ||
                bounds.Left < 0 || bounds.Top < 0 || component.CoordinateSpace != OcrContract.CoordinateSpace)
                throw new ArgumentException("Glyph components require valid original-pixel bounds.", nameof(components));
        }
        OcrRectangle[] band = HeaderGlyphTextRegionRecovery.FindCorroboratedHeadingBand(
            detected, recognized, plot, cancellationToken);
        if (band.Length == 0) return [];
        double height = HeaderGlyphTextRegionRecovery.Median(band.Select(static bounds => bounds.Height));
        double centerY = HeaderGlyphTextRegionRecovery.Median(band.Select(static bounds => bounds.Center.Y));
        var byId = detected.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        var result = new List<CompletedHeaderGlyph>();
        foreach (OcrRegion reading in recognized.OrderBy(static region => region.RegionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reading.ReviewStatus != OcrReviewStatus.Unreviewed ||
                reading.CoordinateSpace != OcrContract.CoordinateSpace ||
                reading.Role is not (OcrTextRole.PhaseHeading or OcrTextRole.Other) ||
                string.IsNullOrWhiteSpace(reading.Text) || reading.Text.EnumerateRunes().Count() != 1 ||
                !byId.TryGetValue(reading.RegionId, out OcrDetectedRegion? source) ||
                source.Context?.ExplicitRoleHint is not null ||
                source.Context is { NumericExpected: true } or { AxisTitleExpected: true } or
                    { NearLegendGlyph: true } or { NearAnnotationArrow: true } or { InParticipantBand: true } ||
                source.CoordinateSpace != OcrContract.CoordinateSpace ||
                !source.Polygon.Points.SequenceEqual(reading.Polygon.Points) ||
                GraphTextRoleClassifier.GetOrientation(source.OrientationDegrees) != OcrOrientation.Horizontal)
                continue;
            OcrRectangle fragment = source.Polygon.Bounds;
            if (!fragment.IsValid || fragment.Left < plot.Left || fragment.Right > plot.Right ||
                fragment.Top < 0 || fragment.Bottom > plot.Top || fragment.Width > 2 * height || fragment.Height > 2 * height)
                continue;
            OcrDetectedRegion[] containing = components.Where(component =>
            {
                OcrRectangle glyph = component.Polygon.Bounds;
                return Contains(glyph, fragment) && glyph != fragment &&
                    glyph.Left >= plot.Left && glyph.Right <= plot.Right && glyph.Bottom <= plot.Top &&
                    glyph.Height >= height / 2 && glyph.Height <= height * 2 &&
                    glyph.Width >= height * .3 && glyph.Width <= height * 2 &&
                    Math.Abs(glyph.Center.Y - centerY) <= height / 2 &&
                    !detected.Any(other => other.RegionId != source.RegionId &&
                        Overlap(glyph, other.Polygon.Bounds) >= .5 * Area(other.Polygon.Bounds));
            }).ToArray();
            if (containing.Length != 1) continue;
            OcrDetectedRegion full = containing[0];
            string material = $"{CompositionVersion}\n{source.RegionId}\n{full.RegionId}";
            result.Add(new(source.RegionId, source with
            {
                RegionId = "header-glyph-complete:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material))),
                Polygon = full.Polygon,
                DetectionConfidence = Math.Min(source.DetectionConfidence, full.DetectionConfidence),
                Context = null,
                Evidence = null,
            }));
        }
        return OcrCollections.Freeze(result);
    }

    private static bool Contains(OcrRectangle outer, OcrRectangle inner) =>
        outer.Left <= inner.Left && outer.Top <= inner.Top && outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;

    private static double Area(OcrRectangle bounds) => bounds.Width * bounds.Height;

    private static double Overlap(OcrRectangle a, OcrRectangle b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
}
