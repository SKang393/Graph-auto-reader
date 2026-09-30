// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.Collections.Frozen;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using OpenCvSharp;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Finds repeated original-pixel glyphs for the existing classifier to review.</summary>
internal static class ProductionMarkerTemplateRecovery
{
    internal const string Version = "original-pixel-marker-template-v3-eligible-text-masks";
    internal const double MinimumSimilarity = 0.9;
    private static readonly double[] Scales = [0.5, 0.625, 0.75, 0.875, 1, 1.125, 1.25, 1.375, 1.5];

    internal static IReadOnlyList<MarkerCenter> Find(
        OcrImage image,
        MarkerPolygon plot,
        IReadOnlyList<OcrMask> textMasks,
        IReadOnlyList<ClassifiedMarker> accepted,
        IReadOnlyDictionary<string, MarkerRectangle> legendGlyphs,
        IReadOnlyList<OcrRectangle> legendFrames,
        CancellationToken cancellationToken,
        IReadOnlyList<OcrRectangle>? verifiedSeedTemplates = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<OcrRectangle> seeds = verifiedSeedTemplates ??
            SelectTemplates(image, plot, accepted, legendGlyphs, cancellationToken);
        ValidateTemplates(image, seeds, nameof(verifiedSeedTemplates));
        if (seeds.Count == 0) return [];
        byte[] pixels = PackedPixels(image);
        using var original = Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC1, pixels);
        var matches = new List<(MarkerCenter Marker, double Scale)>();
        foreach (OcrRectangle glyph in seeds)
        {
            int left = Math.Max(0, (int)Math.Floor(glyph.Left) - 2);
            int top = Math.Max(0, (int)Math.Floor(glyph.Top) - 2);
            int right = Math.Min(image.Width, (int)Math.Ceiling(glyph.Right) + 2);
            int bottom = Math.Min(image.Height, (int)Math.Ceiling(glyph.Bottom) + 2);
            using var template = new Mat(original, new Rect(left, top, right - left, bottom - top));
            foreach (double scale in Scales)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int width = Math.Max(3, (int)Math.Round(template.Width * scale));
                int height = Math.Max(3, (int)Math.Round(template.Height * scale));
                if (width >= image.Width || height >= image.Height) continue;
                using var resized = new Mat();
                Cv2.Resize(template, resized, new Size(width, height), 0, 0,
                    scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Cubic);
                using var correlation = new Mat();
                Cv2.MatchTemplate(original, resized, correlation, TemplateMatchModes.CCoeffNormed);
                correlation.GetArray(out float[] scores);
                int correlationWidth = correlation.Width, correlationHeight = correlation.Height;
                for (int y = 1; y < correlationHeight - 1; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int x = 1; x < correlationWidth - 1; x++)
                    {
                        int index = y * correlationWidth + x;
                        float similarity = scores[index];
                        if (!float.IsFinite(similarity) || similarity < MinimumSimilarity) continue;
                        bool peak = true;
                        for (int dy = -1; dy <= 1 && peak; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int neighbor = (y + dy) * correlationWidth + x + dx;
                                if (scores[neighbor] > similarity || (scores[neighbor] == similarity && neighbor < index))
                                { peak = false; break; }
                            }
                        if (!peak) continue;
                        var center = new MarkerPoint(x + width / 2.0, y + height / 2.0);
                        if (!plot.Contains(center) || legendFrames.Any(box => ContainsInclusive(box, center)) ||
                            textMasks.Any(mask => ContainsInclusive(mask.Polygon.Bounds, center))) continue;
                        double radius = Math.Max(glyph.Width, glyph.Height) * scale / 2;
                        string identity = string.Create(CultureInfo.InvariantCulture,
                            $"{Version}:{center.X:R},{center.Y:R},{radius:R}");
                        // Correlation is image-match support, not a calibrated model probability.
                        matches.Add((new MarkerCenter(identity, center, radius, 0,
                            Math.Clamp(similarity, 0, 1), MarkerSourceImage.Original), scale));
                    }
                }
            }
        }
        var retained = new List<MarkerCenter>();
        foreach (MarkerCenter match in matches.OrderByDescending(static item => item.Marker.CenterConfidence)
            .ThenBy(static item => item.Marker.Center.Y).ThenBy(static item => item.Marker.Center.X)
            .ThenBy(static item => item.Scale).Select(static item => item.Marker))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (retained.Any(previous => Distance(previous.Center, match.Center) <=
                Math.Max(2, Math.Min(previous.Radius, match.Radius)))) continue;
            retained.Add(match);
        }
        // Do this after peak suppression, matching the measured diagnostic. Existing centers are immutable.
        return retained.Where(match => !accepted.Any(previous =>
            Distance(previous.Marker.Center, match.Center) <= Math.Max(2, match.Radius))).ToArray();
    }

    internal static IReadOnlyList<ClassifiedMarker> SelectNew(
        IReadOnlyList<ClassifiedMarker> existing, IReadOnlyList<ClassifiedMarker> classified,
        double artifactThreshold, CancellationToken cancellationToken)
    {
        var combined = existing.ToList();
        var added = new List<ClassifiedMarker>();
        foreach (ClassifiedMarker marker in classified)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (marker.ArtifactProbability >= artifactThreshold || combined.Any(previous =>
                Distance(previous.Marker.Center, marker.Marker.Center) <= Math.Max(2, marker.Marker.Radius))) continue;
            combined.Add(marker);
            added.Add(marker);
        }
        return added.AsReadOnly();
    }

    internal static IReadOnlyList<OcrRectangle> SelectTemplates(
        OcrImage image, MarkerPolygon plot, IReadOnlyList<ClassifiedMarker> accepted,
        IReadOnlyDictionary<string, MarkerRectangle> legendGlyphs, CancellationToken cancellationToken,
        bool selectAll = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] pixels = PackedPixels(image);
        var seeds = legendGlyphs.Values.Select(static box =>
            new OcrRectangle(box.X, box.Y, box.Width, box.Height)).Distinct().ToList();
        ValidateTemplates(image, seeds, nameof(legendGlyphs));
        if (accepted.Count == 0) return seeds.AsReadOnly();
        IReadOnlyList<OcrRectangle> components = CompleteInkComponents(pixels, image.Width, image.Height, cancellationToken);
        foreach (var group in accepted.GroupBy(static item => (item.Shape, item.Fill)))
        {
            int selected = 0;
            foreach (ClassifiedMarker item in group.OrderByDescending(static item =>
                item.Marker.CenterConfidence * item.ShapeConfidence * (1 - item.ArtifactProbability))
                .ThenBy(static item => item.Marker.MarkerId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                MarkerCenter marker = item.Marker;
                if (!plot.Contains(marker.Center) || !double.IsFinite(marker.Radius) || marker.Radius <= 0) continue;
                OcrRectangle[] eligible = components.Where(box => Contains(box, marker.Center) &&
                    box.Width <= marker.Radius * 4 && box.Height <= marker.Radius * 4).ToArray();
                if (eligible.Length != 1) continue;
                OcrRectangle box = eligible[0];
                if (accepted.Count(other => Contains(box, other.Marker.Center)) != 1 || seeds.Contains(box)) continue;
                seeds.Add(box);
                if (++selected == 2 && !selectAll) break;
            }
        }
        return seeds.AsReadOnly();
    }

    internal static MarkerTemplateSeedInputs PrepareSeedInputs(
        OcrImage image, MarkerPolygon plot, IReadOnlyList<ClassifiedMarker> candidates,
        IReadOnlyDictionary<string, MarkerRectangle> legendGlyphs, CancellationToken cancellationToken)
    {
        OcrRectangle[] legendTemplates = legendGlyphs.Values.Select(static box =>
            new OcrRectangle(box.X, box.Y, box.Width, box.Height)).Distinct().ToArray();
        OcrRectangle[] components = SelectTemplates(image, plot, candidates, legendGlyphs, cancellationToken, selectAll: true)
            .Except(legendTemplates).ToArray();
        var markers = new List<MarkerCenter>();
        var bounds = new Dictionary<string, MarkerRectangle>(StringComparer.Ordinal);
        foreach (OcrRectangle box in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = "isolated-seed-" + markers.Count.ToString(CultureInfo.InvariantCulture);
            double confidence = candidates.Where(item => Contains(box, item.Marker.Center))
                .Max(static item => item.Marker.CenterConfidence);
            markers.Add(new MarkerCenter(id, new MarkerPoint(box.Center.X, box.Center.Y),
                Math.Max(box.Width, box.Height) / 2, 0, confidence, MarkerSourceImage.Original));
            bounds.Add(id, new MarkerRectangle(box.X, box.Y, box.Width, box.Height));
        }
        // These are search inputs only. A rejected off-center crop can still contain a complete glyph.
        return new(markers.AsReadOnly(), bounds.ToFrozenDictionary(StringComparer.Ordinal));
    }

    internal static IReadOnlyList<OcrRectangle> SelectVerifiedTemplates(
        IReadOnlyList<ClassifiedMarker> classified, IReadOnlyDictionary<string, MarkerRectangle> contentBounds,
        IReadOnlyDictionary<string, MarkerRectangle> legendGlyphs, double artifactThreshold,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (classified.Count != contentBounds.Count ||
            classified.Select(static item => item.Marker.MarkerId).Distinct(StringComparer.Ordinal).Count() != classified.Count ||
            classified.Any(item => !contentBounds.ContainsKey(item.Marker.MarkerId)))
            throw new ArgumentException("Every isolated template must have exactly one classifier result.", nameof(classified));
        return classified.Where(item => item.ArtifactProbability < artifactThreshold)
            .GroupBy(static item => (item.Shape, item.Fill))
            .SelectMany(static group => group.OrderByDescending(static item =>
                item.Marker.CenterConfidence * item.ShapeConfidence * (1 - item.ArtifactProbability))
                .ThenBy(static item => item.Marker.MarkerId, StringComparer.Ordinal).Take(2))
            .Select(item => contentBounds[item.Marker.MarkerId])
            .Select(static box => new OcrRectangle(box.X, box.Y, box.Width, box.Height))
            .Concat(legendGlyphs.Values.Select(static box => new OcrRectangle(box.X, box.Y, box.Width, box.Height)))
            .Distinct().ToArray();
    }

    private static void ValidateTemplates(OcrImage image, IReadOnlyList<OcrRectangle> templates, string parameterName)
    {
        if (templates.Any(box => box.Left < 0 || box.Top < 0 || box.Right > image.Width || box.Bottom > image.Height ||
            !double.IsFinite(box.X) || !double.IsFinite(box.Y) ||
            !double.IsFinite(box.Width) || !double.IsFinite(box.Height) || box.Width <= 0 || box.Height <= 0))
            throw new ArgumentException("Marker templates must be finite original-pixel rectangles inside the image.", parameterName);
    }

    private static byte[] PackedPixels(OcrImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.SourceImage != OcrSourceImage.Original || image.OriginalToImage != OcrFrameTransform.Identity ||
            image.CoordinateSpace != OcrContract.CoordinateSpace || image.Width <= 0 || image.Height <= 0 ||
            image.Stride < image.Width || image.Pixels.Length != checked(image.Stride * image.Height))
            throw new ArgumentException("Marker recovery requires aligned original grayscale pixels.", nameof(image));
        var pixels = new byte[checked(image.Width * image.Height)];
        for (int y = 0; y < image.Height; y++)
            image.Pixels.Span.Slice(y * image.Stride, image.Width).CopyTo(pixels.AsSpan(y * image.Width, image.Width));
        return pixels;
    }

    private static List<OcrRectangle> CompleteInkComponents(
        byte[] pixels, int width, int height, CancellationToken cancellationToken)
    {
        var visited = new bool[pixels.Length];
        var queue = new int[pixels.Length];
        var result = new List<OcrRectangle>();
        for (int start = 0; start < pixels.Length; start++)
        {
            if (start % width == 0) cancellationToken.ThrowIfCancellationRequested();
            if (visited[start] || pixels[start] >= 128) continue;
            int read = 0, write = 1;
            queue[0] = start;
            visited[start] = true;
            int minX = start % width, maxX = minX, minY = start / width, maxY = minY;
            while (read < write)
            {
                if ((read & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                int point = queue[read++], x = point % width, y = point / width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                for (int yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1); yy++)
                    for (int xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++)
                    {
                        int other = yy * width + xx;
                        if (!visited[other] && pixels[other] < 128)
                        { visited[other] = true; queue[write++] = other; }
                    }
            }
            if (minX > 0 && minY > 0 && maxX < width - 1 && maxY < height - 1 && maxX - minX >= 2 && maxY - minY >= 2)
                result.Add(new OcrRectangle(minX, minY, maxX - minX + 1, maxY - minY + 1));
        }
        return result;
    }

    private static double Distance(MarkerPoint a, MarkerPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static bool Contains(OcrRectangle box, MarkerPoint point) =>
        point.X >= box.Left && point.X < box.Right && point.Y >= box.Top && point.Y < box.Bottom;
    private static bool ContainsInclusive(OcrRectangle box, MarkerPoint point) =>
        point.X >= box.Left && point.X <= box.Right && point.Y >= box.Top && point.Y <= box.Bottom;
}

internal sealed record MarkerTemplateSeedInputs(
    IReadOnlyList<MarkerCenter> Markers,
    IReadOnlyDictionary<string, MarkerRectangle> ContentBounds);
