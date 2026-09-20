// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace GraphReader.Ocr;

public sealed record ConnectedComponentTextRegionDetectorOptions
{
    public int MinimumComponentArea { get; init; } = 2;

    public double MaximumComponentWidthFraction { get; init; } = 0.15;

    public double MaximumComponentHeightFraction { get; init; } = 0.20;

    public double MaximumLineGapHeightRatio { get; init; } = 2.5;

    public double MinimumVerticalOverlapFraction { get; init; } = 0.35;

    public byte? ForegroundThreshold { get; init; }

    public bool GroupComponentsIntoLines { get; init; } = true;
}

/// <summary>
/// Finds bounded dark connected components and groups nearby glyphs into text
/// lines. Recognition remains a separate region-first stage.
/// </summary>
public sealed class ConnectedComponentTextRegionDetector : ITextRegionDetector
{
    private readonly ConnectedComponentTextRegionDetectorOptions _options;

    public ConnectedComponentTextRegionDetector(ConnectedComponentTextRegionDetectorOptions? options = null)
    {
        _options = options ?? new ConnectedComponentTextRegionDetectorOptions();
        ValidateOptions(_options);
    }

    public string ConfigurationFingerprint => string.Create(
        CultureInfo.InvariantCulture,
        $"cc-v3:{_options.MinimumComponentArea}:{_options.MaximumComponentWidthFraction:R}:{_options.MaximumComponentHeightFraction:R}:{_options.MaximumLineGapHeightRatio:R}:{_options.MinimumVerticalOverlapFraction:R}:{_options.ForegroundThreshold?.ToString(CultureInfo.InvariantCulture) ?? "auto"}") +
        (_options.GroupComponentsIntoLines ? string.Empty : ":ungrouped");

    public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateImage(image);
        cancellationToken.ThrowIfCancellationRequested();

        var threshold = _options.ForegroundThreshold ?? EstimateThreshold(image, cancellationToken);
        var components = FindComponents(image, threshold, cancellationToken);
        var lines = (_options.GroupComponentsIntoLines ? GroupIntoLines(components) : components).ToArray();
        var regions = lines
            .OrderBy(static line => line.Top)
            .ThenBy(static line => line.Left)
            .Select(line => ToRegion(image, line))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<OcrDetectedRegion>>(Array.AsReadOnly(regions));
    }

    private List<ComponentBounds> FindComponents(
        OcrImage image,
        byte threshold,
        CancellationToken cancellationToken)
    {
        var visited = new bool[checked(image.Width * image.Height)];
        var components = new List<ComponentBounds>();
        var maximumWidth = Math.Max(2, image.Width * _options.MaximumComponentWidthFraction);
        var maximumHeight = Math.Max(2, image.Height * _options.MaximumComponentHeightFraction);
        var queue = new Queue<int>();

        for (var y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < image.Width; x++)
            {
                var visitIndex = (y * image.Width) + x;
                if (visited[visitIndex])
                {
                    continue;
                }

                visited[visitIndex] = true;
                if (!IsForeground(image, x, y, threshold))
                {
                    continue;
                }

                queue.Enqueue(visitIndex);
                var componentPixels = new List<int>();
                var bounds = ComponentBounds.Start(x, y);
                var traversed = 0;
                while (queue.Count > 0)
                {
                    if ((traversed++ & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var current = queue.Dequeue();
                    componentPixels.Add(current);
                    var currentX = current % image.Width;
                    var currentY = current / image.Width;
                    bounds = bounds.Include(currentX, currentY);
                    VisitNeighbor(currentX - 1, currentY);
                    VisitNeighbor(currentX + 1, currentY);
                    VisitNeighbor(currentX, currentY - 1);
                    VisitNeighbor(currentX, currentY + 1);
                }

                bounds = AddMorphology(image, bounds, componentPixels);

                if (bounds.Area >= _options.MinimumComponentArea &&
                    bounds.Width <= maximumWidth &&
                    bounds.Height <= maximumHeight)
                {
                    components.Add(bounds);
                }

                void VisitNeighbor(int neighborX, int neighborY)
                {
                    if (neighborX < 0 || neighborY < 0 || neighborX >= image.Width || neighborY >= image.Height)
                    {
                        return;
                    }

                    var neighborIndex = (neighborY * image.Width) + neighborX;
                    if (visited[neighborIndex])
                    {
                        return;
                    }

                    visited[neighborIndex] = true;
                    if (IsForeground(image, neighborX, neighborY, threshold))
                    {
                        queue.Enqueue(neighborIndex);
                    }
                }
            }
        }

        return components;
    }

    private List<ComponentBounds> GroupIntoLines(List<ComponentBounds> components)
    {
        var remaining = components
            .OrderBy(static component => component.Top)
            .ThenBy(static component => component.Left)
            .ToList();
        var lines = new List<ComponentBounds>();
        while (remaining.Count > 0)
        {
            var line = remaining[0];
            remaining.RemoveAt(0);
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var index = remaining.Count - 1; index >= 0; index--)
                {
                    var candidate = remaining[index];
                    var verticalOverlap = Math.Max(0, Math.Min(line.Bottom, candidate.Bottom) - Math.Max(line.Top, candidate.Top) + 1);
                    var overlapFraction = verticalOverlap / (double)Math.Max(1, Math.Min(line.Height, candidate.Height));
                    var horizontalGap = candidate.Left > line.Right
                        ? candidate.Left - line.Right - 1
                        : line.Left > candidate.Right
                            ? line.Left - candidate.Right - 1
                            : 0;
                    var maximumGap = Math.Max(line.Height, candidate.Height) * _options.MaximumLineGapHeightRatio;
                    if (overlapFraction >= _options.MinimumVerticalOverlapFraction && horizontalGap <= maximumGap)
                    {
                        line = line.Merge(candidate);
                        remaining.RemoveAt(index);
                        changed = true;
                    }
                }
            }

            lines.Add(line);
        }

        return GroupVerticalGlyphs(lines);
    }

    private List<ComponentBounds> GroupVerticalGlyphs(List<ComponentBounds> horizontalLines)
    {
        var remaining = horizontalLines
            .OrderBy(static component => component.Left)
            .ThenBy(static component => component.Top)
            .ToList();
        var lines = new List<ComponentBounds>();
        while (remaining.Count > 0)
        {
            var line = remaining[0];
            remaining.RemoveAt(0);
            if (!IsRotatedGlyphCandidate(line))
            {
                lines.Add(line);
                continue;
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var index = remaining.Count - 1; index >= 0; index--)
                {
                    var candidate = remaining[index];
                    if (!IsRotatedGlyphCandidate(candidate))
                    {
                        continue;
                    }

                    var horizontalOverlap = Math.Max(
                        0,
                        Math.Min(line.Right, candidate.Right) - Math.Max(line.Left, candidate.Left) + 1);
                    var overlapFraction = horizontalOverlap /
                        (double)Math.Max(1, Math.Min(line.Width, candidate.Width));
                    var verticalGap = candidate.Top > line.Bottom
                        ? candidate.Top - line.Bottom - 1
                        : line.Top > candidate.Bottom
                            ? line.Top - candidate.Bottom - 1
                            : 0;
                    var maximumGap = Math.Max(line.Width, candidate.Width) * _options.MaximumLineGapHeightRatio;
                    if (overlapFraction >= _options.MinimumVerticalOverlapFraction && verticalGap <= maximumGap)
                    {
                        line = line.Merge(candidate);
                        remaining.RemoveAt(index);
                        changed = true;
                    }
                }
            }

            lines.Add(line);
        }

        return lines;

        static bool IsRotatedGlyphCandidate(ComponentBounds component) =>
            component.ComponentCount == 1 &&
            component.Width >= component.Height * 1.2 &&
            component.MarkerLikeComponentCount == 0;
    }

    private static OcrDetectedRegion ToRegion(OcrImage image, ComponentBounds line)
    {
        var topLeft = image.OriginalToImage.MapToOriginal(new OcrPoint(line.Left, line.Top));
        var bottomRight = image.OriginalToImage.MapToOriginal(new OcrPoint(line.Right + 1, line.Bottom + 1));
        var rectangle = new OcrRectangle(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Abs(bottomRight.X - topLeft.X),
            Math.Abs(bottomRight.Y - topLeft.Y));
        var density = line.Area / (double)Math.Max(1, line.Width * line.Height);
        var textLikelihood = line.MarkerLikeComponentCount == line.ComponentCount
            ? 0.12
            : line.ComponentCount > 1
            ? Math.Clamp(0.68 + (line.ComponentCount * 0.06), 0, 0.94)
            : Math.Clamp(0.58 - (line.StructureLikelihood * 0.30), 0.15, 0.65);
        var confidence = Math.Clamp(0.45 + (0.35 * textLikelihood) + (0.10 * Math.Min(1, density * 3)), 0, 0.92);
        var orientation = line.Height > line.Width * 1.4 ? -90d : 0d;
        var structureReasons = StructureReasons(line);
        return new OcrDetectedRegion(
            DeterministicRegionId(rectangle),
            OcrPolygon.FromRectangle(rectangle),
            orientation,
            confidence,
            Evidence: new OcrRegionEvidence(
                line.ComponentCount,
                density,
                textLikelihood,
                line.StructureLikelihood,
                IsLikelyGraphStructure(line),
                OcrCollections.Freeze(structureReasons)));
    }

    private static ComponentBounds AddMorphology(
        OcrImage image,
        ComponentBounds bounds,
        IReadOnlyList<int> componentPixels)
    {
        var rows = new int[bounds.Height];
        var columns = new int[bounds.Width];
        foreach (var pixel in componentPixels)
        {
            var x = pixel % image.Width;
            var y = pixel / image.Width;
            rows[y - bounds.Top]++;
            columns[x - bounds.Left]++;
        }

        var maximumRowFraction = rows.Max() / (double)Math.Max(1, bounds.Width);
        var maximumColumnFraction = columns.Max() / (double)Math.Max(1, bounds.Height);
        var density = bounds.Area / (double)Math.Max(1, bounds.Width * bounds.Height);
        return bounds with
        {
            MaximumRowFillFraction = maximumRowFraction,
            MaximumColumnFillFraction = maximumColumnFraction,
            Density = density,
            MarkerLikeComponentCount = IsCompactMarkerLike(
                bounds.Width,
                bounds.Height,
                density,
                maximumRowFraction,
                maximumColumnFraction) ? 1 : 0,
        };
    }

    private static bool IsLikelyGraphStructure(ComponentBounds component)
    {
        if (component.ComponentCount > 1)
        {
            return component.MarkerLikeComponentCount == component.ComponentCount;
        }

        var aspect = component.Width / (double)Math.Max(1, component.Height);
        var longThin = (aspect >= 2.5 || aspect <= 0.4) && component.Density >= 0.60;
        var compact = aspect is >= 0.65 and <= 1.55;
        var compactStroke = compact &&
            component.MaximumRowFillFraction >= 0.80 &&
            component.MaximumColumnFillFraction >= 0.80 &&
            component.Width <= 20 && component.Height <= 20;
        var lineIntersection = compact && component.Density <= 0.60 &&
            component.MaximumRowFillFraction >= 0.80 &&
            component.MaximumColumnFillFraction >= 0.80;
        return longThin || compactStroke || lineIntersection;
    }

    private static bool IsCompactMarkerLike(
        int width,
        int height,
        double density,
        double maximumRowFillFraction,
        double maximumColumnFillFraction)
    {
        var aspect = width / (double)Math.Max(1, height);
        return aspect is >= 0.65 and <= 1.55 &&
            width is >= 3 and <= 20 && height is >= 3 and <= 20 &&
            density >= 0.25 &&
            maximumRowFillFraction >= 0.60 &&
            maximumColumnFillFraction >= 0.60;
    }

    private static List<string> StructureReasons(ComponentBounds component)
    {
        var reasons = new List<string>();
        var aspect = component.Width / (double)Math.Max(1, component.Height);
        if (component.ComponentCount > 1 &&
            component.MarkerLikeComponentCount == component.ComponentCount)
        {
            reasons.Add("repeated_compact_marker_components");
        }

        if (component.ComponentCount == 1 && (aspect >= 2.5 || aspect <= 0.4))
        {
            reasons.Add("single_component_line_or_tick_like");
        }

        if (component.ComponentCount == 1 && aspect is >= 0.65 and <= 1.55 &&
            component.MaximumRowFillFraction >= 0.80 && component.MaximumColumnFillFraction >= 0.80)
        {
            reasons.Add("single_component_marker_arrow_or_intersection_like");
        }

        if (reasons.Count == 0)
        {
            reasons.Add(component.ComponentCount > 1 ? "multi_glyph_line_evidence" : "single_glyph_candidate");
        }

        return reasons;
    }

    private static byte EstimateThreshold(OcrImage image, CancellationToken cancellationToken)
    {
        long sum = 0;
        for (var y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < image.Width; x++)
            {
                sum += image.Pixels.Span[(y * image.Stride) + x];
            }
        }

        var mean = sum / (double)checked(image.Width * image.Height);
        return (byte)Math.Clamp(Math.Round(mean * 0.80), 32, 224);
    }

    private static bool IsForeground(OcrImage image, int x, int y, byte threshold) =>
        image.Pixels.Span[(y * image.Stride) + x] <= threshold;

    private static string DeterministicRegionId(OcrRectangle rectangle)
    {
        var text = FormattableString.Invariant($"{rectangle.X:R},{rectangle.Y:R},{rectangle.Width:R},{rectangle.Height:R}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }

    private static void ValidateImage(OcrImage image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Stride < image.Width ||
            image.Pixels.Length < checked(image.Stride * image.Height) ||
            !image.OriginalToImage.IsInvertible)
        {
            throw new ArgumentException("OCR detector image is invalid.", nameof(image));
        }
    }

    private static void ValidateOptions(ConnectedComponentTextRegionDetectorOptions options)
    {
        if (options.MinimumComponentArea <= 0 ||
            options.MaximumComponentWidthFraction is <= 0 or > 1 ||
            options.MaximumComponentHeightFraction is <= 0 or > 1 ||
            options.MaximumLineGapHeightRatio < 0 ||
            options.MinimumVerticalOverlapFraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private readonly record struct ComponentBounds(
        int Left,
        int Top,
        int Right,
        int Bottom,
        int Area,
        int ComponentCount,
        double Density,
        double MaximumRowFillFraction,
        double MaximumColumnFillFraction,
        int MarkerLikeComponentCount)
    {
        public int Width => Right - Left + 1;

        public int Height => Bottom - Top + 1;

        public double StructureLikelihood => ComponentCount > 1
            ? MarkerLikeComponentCount == ComponentCount ? 0.98 : 0.08
            : IsLikelyGraphStructure(this) ? 0.95 : 0.35;

        public static ComponentBounds Start(int x, int y) =>
            new(x, y, x, y, 0, 1, 0, 0, 0, 0);

        public ComponentBounds Include(int x, int y) =>
            this with
            {
                Left = Math.Min(Left, x),
                Top = Math.Min(Top, y),
                Right = Math.Max(Right, x),
                Bottom = Math.Max(Bottom, y),
                Area = Area + 1,
            };

        public ComponentBounds Merge(ComponentBounds other) =>
            new(
                Math.Min(Left, other.Left),
                Math.Min(Top, other.Top),
                Math.Max(Right, other.Right),
                Math.Max(Bottom, other.Bottom),
                Area + other.Area,
                ComponentCount + other.ComponentCount,
                0,
                Math.Max(MaximumRowFillFraction, other.MaximumRowFillFraction),
                Math.Max(MaximumColumnFillFraction, other.MaximumColumnFillFraction),
                MarkerLikeComponentCount + other.MarkerLikeComponentCount);
    }
}
