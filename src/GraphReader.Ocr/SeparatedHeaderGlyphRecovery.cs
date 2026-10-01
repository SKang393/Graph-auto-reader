// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

public sealed record SeparatedHeaderGlyph(
    string SourceRegionId, OcrDetectedRegion Glyph, OcrDetectedRegion Word);

/// <summary>Separates a boundary glyph from a composite header crop using measured ink gaps.</summary>
public static class SeparatedHeaderGlyphRecovery
{
    public const string CompositionVersion = "original-pixel-separated-header-phase-word-spacing-v2";

    public static async ValueTask<IReadOnlyList<SeparatedHeaderGlyph>> FindAsync(
        OcrImage image, IReadOnlyList<OcrDetectedRegion> detected, IReadOnlyList<OcrRegion> recognized,
        OcrRectangle plot, CancellationToken cancellationToken = default)
    {
        _ = new HeaderBracketEvidence(image, cancellationToken);
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(recognized);
        if (!recognized.Any(static r => HasBoundaryLetter(r.Text))) return [];
        var detector = new ConnectedComponentTextRegionDetector(
            new ConnectedComponentTextRegionDetectorOptions { GroupComponentsIntoLines = false });
        var components = await detector.DetectAsync(image, cancellationToken).ConfigureAwait(false);
        return SelectCandidates(image, components, detected, recognized, plot, cancellationToken);
    }

    public static IReadOnlyList<SeparatedHeaderGlyph> SelectCandidates(
        OcrImage image, IReadOnlyList<OcrDetectedRegion> components,
        IReadOnlyList<OcrDetectedRegion> detected, IReadOnlyList<OcrRegion> recognized,
        OcrRectangle plot, CancellationToken cancellationToken = default)
    {
        _ = new HeaderBracketEvidence(image, cancellationToken);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(recognized);
        if (!InsideImage(plot, image))
            throw new ArgumentException("Header separation requires measured original-pixel plot bounds.", nameof(plot));
        foreach (var component in components)
            if (component.CoordinateSpace != OcrContract.CoordinateSpace || !InsideImage(component.Polygon.Bounds, image))
                throw new ArgumentException("Header components must remain in original pixels.", nameof(components));
        var byId = detected.ToDictionary(static r => r.RegionId, StringComparer.Ordinal);
        var result = new List<SeparatedHeaderGlyph>();
        foreach (OcrRegion reading in recognized.OrderBy(static r => r.RegionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasBoundaryLetter(reading.Text) || reading.ReviewStatus != OcrReviewStatus.Unreviewed ||
                reading.SourceImage != OcrSourceImage.Original || reading.CoordinateSpace != OcrContract.CoordinateSpace ||
                reading.Role is not (OcrTextRole.PhaseHeading or OcrTextRole.Participant or OcrTextRole.Other) ||
                !byId.TryGetValue(reading.RegionId, out OcrDetectedRegion? source) ||
                source.CoordinateSpace != OcrContract.CoordinateSpace ||
                !source.Polygon.Points.SequenceEqual(reading.Polygon.Points) ||
                GraphTextRoleClassifier.GetOrientation(source.OrientationDegrees) != OcrOrientation.Horizontal ||
                source.Context?.ExplicitRoleHint is not null ||
                source.Context is { NumericExpected: true } or { AxisTitleExpected: true } or
                    { NearLegendGlyph: true } or { NearAnnotationArrow: true } or { InParticipantBand: true }) continue;
            OcrRectangle box = source.Polygon.Bounds;
            if (!InsideImage(box, image) || box.Bottom > plot.Top || box.Width < 2 * box.Height) continue;
            OcrDetectedRegion[] members = components.Where(c =>
                    Overlap(box, c.Polygon.Bounds) >= .5 * Area(c.Polygon.Bounds))
                .OrderBy(static c => c.Polygon.Bounds.Left).ThenBy(static c => c.Polygon.Bounds.Top)
                .ThenBy(static c => c.Polygon.Bounds.Right).ThenBy(static c => c.Polygon.Bounds.Bottom)
                .ThenBy(static c => c.RegionId, StringComparer.Ordinal).ToArray();
            if (members.Length < 2) continue;
            string[] tokens = Tokens(reading.Text);
            var candidates = new List<SeparatedHeaderGlyph>();
            for (int side = 0; side < 2; side++)
            {
                if (!IsLetter(tokens[side == 0 ? 0 : tokens.Length - 1])) continue;
                int index = side == 0 ? 0 : members.Length - 1;
                OcrDetectedRegion glyph = members[index];
                OcrDetectedRegion[] wordMembers = members.Where((_, i) => i != index).ToArray();
                OcrRectangle gb = glyph.Polygon.Bounds;
                OcrRectangle word = Union(wordMembers.Select(static c => c.Polygon.Bounds).ToArray());
                double gap = side == 0 ? word.Left - gb.Right : gb.Left - word.Right;
                bool measuredPhaseGap = HasMeasuredPhaseWordGap(source, tokens, side, wordMembers, word, plot, gap);
                bool outsidePlot = measuredPhaseGap
                    ? gb.Center.X < plot.Left || gb.Center.X > plot.Right
                    : gb.Left < plot.Left || gb.Right > plot.Right;
                if (outsidePlot || gb.Bottom > plot.Top || word.Bottom > plot.Top ||
                    gb.Height < .5 * box.Height || gb.Height > 2 * box.Height ||
                    gb.Width < .3 * box.Height || gb.Width > 1.5 * box.Height ||
                    (gap <= Math.Min(gb.Height, word.Height) && !measuredPhaseGap) || word.Width < 2 * word.Height ||
                    Math.Abs(gb.Center.Y - word.Center.Y) > .5 * Math.Max(gb.Height, word.Height) ||
                    detected.Any(r => r.RegionId != source.RegionId &&
                        (Overlap(gb, r.Polygon.Bounds) >= .5 * Area(r.Polygon.Bounds) ||
                         Overlap(word, r.Polygon.Bounds) >= .5 * Area(r.Polygon.Bounds))) ||
                    recognized.Any(r => r.RegionId != source.RegionId &&
                        (Overlap(gb, r.Polygon.Bounds) >= .5 * Area(r.Polygon.Bounds) ||
                         Overlap(word, r.Polygon.Bounds) >= .5 * Area(r.Polygon.Bounds)))) continue;
                string material = $"{CompositionVersion}\n{source.RegionId}\n{glyph.RegionId}\n{string.Join('\n', wordMembers.Select(static c => c.RegionId))}";
                string identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
                OcrDetectedRegion separatedGlyph = source with
                {
                    RegionId = "header-separated-glyph:" + identity, Polygon = glyph.Polygon,
                    OrientationDegrees = 0, Context = null, Evidence = null,
                    DetectionConfidence = Math.Min(source.DetectionConfidence, glyph.DetectionConfidence),
                };
                OcrDetectedRegion separatedWord = source with
                {
                    RegionId = "header-separated-word:" + identity, Polygon = OcrPolygon.FromRectangle(word),
                    Context = null, Evidence = null,
                    DetectionConfidence = Math.Min(source.DetectionConfidence, wordMembers.Min(static c => c.DetectionConfidence)),
                };
                candidates.Add(new(source.RegionId, separatedGlyph, separatedWord));
            }
            if (candidates.Count == 1) result.Add(candidates[0]);
        }
        return OcrCollections.Freeze(result);
    }

    private static bool HasMeasuredPhaseWordGap(OcrDetectedRegion source, string[] tokens, int side,
        OcrDetectedRegion[] wordMembers, OcrRectangle word, OcrRectangle plot, double gap)
    {
        if (gap <= 0) return false;
        string text = string.Join(' ', side == 0 ? tokens.Skip(1) : tokens.Take(tokens.Length - 1));
        OcrDetectedRegion measuredWord = source with { Polygon = OcrPolygon.FromRectangle(word), Context = null };
        if (GraphTextRoleClassifier.Classify(measuredWord, text, plot).Role != OcrTextRole.PhaseHeading)
            return false;
        // A known phase word supplies a role cue only. Both crops are still
        // recognized independently; no character is copied from the old text.
        double right = double.NegativeInfinity, maximumGap = 0;
        foreach (OcrRectangle bounds in wordMembers.Select(static region => region.Polygon.Bounds)
            .OrderBy(static bounds => bounds.Left).ThenBy(static bounds => bounds.Right))
        {
            if (double.IsFinite(right)) maximumGap = Math.Max(maximumGap, bounds.Left - right);
            right = Math.Max(right, bounds.Right);
        }
        return gap > maximumGap;
    }

    private static string[] Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static bool IsLetter(string text)
    {
        Rune[] letters = text.EnumerateRunes().ToArray();
        return letters.Length == 1 && Rune.IsLetter(letters[0]);
    }
    private static bool HasBoundaryLetter(string text)
    {
        string[] tokens = Tokens(text);
        return tokens.Length > 1 && (IsLetter(tokens[0]) || IsLetter(tokens[^1]));
    }
    private static bool InsideImage(OcrRectangle b, OcrImage image) => b.IsValid && b.Left >= 0 && b.Top >= 0 &&
        double.IsFinite(b.Right) && double.IsFinite(b.Bottom) && b.Right <= image.Width && b.Bottom <= image.Height;
    private static double Area(OcrRectangle b) => b.Width * b.Height;
    private static double Overlap(OcrRectangle a, OcrRectangle b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    private static OcrRectangle Union(OcrRectangle[] boxes)
    {
        double left = boxes.Min(static b => b.Left), top = boxes.Min(static b => b.Top);
        return new(left, top, boxes.Max(static b => b.Right) - left, boxes.Max(static b => b.Bottom) - top);
    }
}
