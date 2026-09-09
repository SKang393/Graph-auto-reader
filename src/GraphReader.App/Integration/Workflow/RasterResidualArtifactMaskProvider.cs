// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Axis;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

public enum RasterResidualArtifactCategory
{
    AnnotationArrow,
    Bracket,
    LegendStructure,
    ConnectingLineIntersection,
}

public readonly record struct RasterResidualBounds(int X, int Y, int Width, int Height)
{
    public int Right => X + Width - 1;

    public int Bottom => Y + Height - 1;

    public bool Contains(int x, int y) =>
        x >= X && x <= Right && y >= Y && y <= Bottom;
}

public sealed record RasterResidualArtifactRegion(
    RasterResidualArtifactCategory Category,
    RasterResidualBounds Bounds,
    float Confidence,
    int SourceInkPixelCount,
    string Reason);

public sealed class RasterResidualArtifactMaskResult
{
    private readonly float[] mask;

    internal RasterResidualArtifactMaskResult(
        int width,
        int height,
        string configurationFingerprint,
        float[] mask,
        IEnumerable<RasterResidualArtifactRegion> regions,
        IEnumerable<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (width <= 0 || height <= 0 || mask.Length != checked(width * height) ||
            mask.AsSpan().ContainsAnyExceptInRange(0f, 1f))
        {
            throw new ArgumentException("Residual artifact mask must be normalized and match positive dimensions.", nameof(mask));
        }

        Width = width;
        Height = height;
        ConfigurationFingerprint = configurationFingerprint;
        this.mask = (float[])mask.Clone();
        Regions = Array.AsReadOnly(regions.ToArray());
        Warnings = Array.AsReadOnly(warnings.ToArray());
    }

    public int Width { get; }

    public int Height { get; }

    public string ConfigurationFingerprint { get; }

    public ReadOnlyMemory<float> Mask => mask;

    public IReadOnlyList<RasterResidualArtifactRegion> Regions { get; }

    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>
/// Produces conservative residual artifact evidence from immutable Gray8 pixels,
/// axis geometry, OCR roles, and seed masks. It exists because the V24 family
/// baseline in docs/GOAL-22-PHASE-4R-V24-FAMILY-DEV-BASELINE.json reached only
/// 0.4723618090452261 precision with a 0.02512562814070352 prohibited-structure
/// hit rate, while its sparse [N,4] output is incompatible with the dense artifact
/// head required by ProductionMarkerArtifactMaskAdapter. This analyzer is an
/// unapproved engineering prototype and does not enable production composition.
/// </summary>
public sealed class RasterResidualArtifactMaskProvider
{
    private const byte ForegroundMaximumGray = 196;
    private const float SeedMaskedThreshold = 0.5f;
    private const int MinimumArrowSpan = 14;
    private const int MinimumBracketSpan = 14;
    private const int MinimumIntersectionArm = 5;
    private const int MaximumIntersectionArm = 64;
    private const int IntersectionMaskRadius = 2;
    private const int IntersectionNmsRadius = 6;
    private const int MaximumCompactMarkerSpan = 13;
    private const float ArrowConfidence = 0.94f;
    private const float BracketConfidence = 0.90f;
    private const float LegendConfidence = 0.90f;
    private const float IntersectionConfidence = 0.90f;
    private const float StructuralConfirmationBonus = 0.02f;

    public const string ConfigurationFingerprint =
        "raster-residual-v1;gray<=196;seed>=0.5;component=8;arrow-span>=14;" +
        "bracket-span>=14;intersection-arm=5..64;intersection-radius=2;nms=6;" +
        "compact-review-span<=13;legend-left-gap<=3.5h";

    private readonly IPreOcrStructuralProbabilityProvider structuralProvider;

    public RasterResidualArtifactMaskProvider(
        IPreOcrStructuralProbabilityProvider? structuralProvider = null) =>
        this.structuralProvider = structuralProvider ?? new RasterPreOcrStructuralProbabilityProvider();

    public async Task<RasterResidualArtifactMaskResult> AnalyzeAsync(
        ProductionDecodedRaster raster,
        ProductionAxisGeometryEvidence axis,
        OcrResult ocr,
        ProductionDetectionMaskSeed seed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(axis);
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(seed);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateInputs(raster, axis, ocr, seed);

        OcrImage image = raster.CreateOcrImage();
        float[] ocrSeed = seed.CopyOcrMask().Values.ToArray();
        float[] geometrySeed = seed.CopyArtifactMask().Values.ToArray();
        var geometryMask = new byte[geometrySeed.Length];
        for (int index = 0; index < geometryMask.Length; index++)
        {
            if ((index & 0x3fff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            geometryMask[index] = geometrySeed[index] >= SeedMaskedThreshold ? byte.MaxValue : (byte)0;
        }

        PreOcrStructuralProbabilityResult structure = await structuralProvider.AnalyzeAsync(
            new PreOcrStructuralFrame(
                raster.Width,
                raster.Height,
                image.Stride,
                image.Pixels,
                raster.Width,
                geometryMask),
            cancellationToken).ConfigureAwait(false);
        ValidateStructureResult(raster, structure);

        return await Task.Run(
            () => AnalyzeCore(
                raster.Width,
                raster.Height,
                image.Pixels.ToArray(),
                ocrSeed,
                geometrySeed,
                axis.Geometry.PlotPolygon,
                ocr.Regions,
                structure.MarkerLikeProbabilities.ToArray(),
                structure.ThinConnectorProbabilities.ToArray(),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static RasterResidualArtifactMaskResult AnalyzeCore(
        int width,
        int height,
        byte[] gray,
        float[] ocrSeed,
        float[] geometrySeed,
        PlotPolygon plot,
        IReadOnlyList<OcrRegion> ocrRegions,
        float[] markerProbabilities,
        float[] connectorProbabilities,
        CancellationToken cancellationToken)
    {
        int pixelCount = checked(width * height);
        var foreground = new bool[pixelCount];
        var visited = new bool[pixelCount];
        var mask = new float[pixelCount];
        var regions = new List<RasterResidualArtifactRegion>();
        OcrRectangle[] annotations = ocrRegions
            .Where(static region => region.Role == OcrTextRole.Annotation)
            .Select(static region => region.Polygon.Bounds)
            .ToArray();
        OcrRectangle[] legends = ocrRegions
            .Where(static region => region.Role == OcrTextRole.LegendText)
            .Select(static region => region.Polygon.Bounds)
            .ToArray();

        for (int index = 0; index < pixelCount; index++)
        {
            if ((index & 0x3fff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            foreground[index] = gray[index] <= ForegroundMaximumGray &&
                ocrSeed[index] < SeedMaskedThreshold &&
                geometrySeed[index] < SeedMaskedThreshold;
        }

        int ambiguousCompactCount = 0;
        for (int index = 0; index < pixelCount; index++)
        {
            if (!foreground[index] || visited[index])
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Component component = ReadComponent(
                index,
                width,
                height,
                foreground,
                visited,
                markerProbabilities,
                connectorProbabilities,
                cancellationToken);
            Classification? classification = ClassifyComponent(component, plot, annotations, legends);
            if (classification is not null)
            {
                WriteComponentMask(mask, component, classification.Confidence);
                regions.Add(new RasterResidualArtifactRegion(
                    classification.Category,
                    component.Bounds,
                    classification.Confidence,
                    component.Pixels.Count,
                    classification.Reason));
            }
            else if (IsCompactAmbiguity(component))
            {
                ambiguousCompactCount++;
            }
        }

        AddIntersections(
            width,
            height,
            foreground,
            mask,
            plot,
            connectorProbabilities,
            regions,
            cancellationToken);

        var warnings = new List<string>
        {
            "Residual artifact output is an unapproved engineering prototype; no representative acceptance gate has run.",
        };
        if (ambiguousCompactCount > 0)
        {
            warnings.Add($"{ambiguousCompactCount} compact marker-like component(s) remained reviewable instead of being suppressed.");
        }

        return new RasterResidualArtifactMaskResult(
            width,
            height,
            ConfigurationFingerprint,
            mask,
            regions,
            warnings);
    }

    private static Classification? ClassifyComponent(
        Component component,
        PlotPolygon plot,
        IReadOnlyList<OcrRectangle> annotations,
        IReadOnlyList<OcrRectangle> legends)
    {
        if (TryClassifyArrow(component, plot, annotations, out Classification? arrow))
        {
            return arrow;
        }

        if (IsBracket(component))
        {
            return new Classification(
                RasterResidualArtifactCategory.Bracket,
                AddStructuralBonus(BracketConfidence, component.ConnectorFraction),
                "open three-sided bracket topology");
        }

        if (IsLegendStructure(component, plot, legends))
        {
            return new Classification(
                RasterResidualArtifactCategory.LegendStructure,
                AddStructuralBonus(LegendConfidence, component.MarkerFraction),
                "OCR legend-role neighborhood with raster glyph or frame geometry");
        }

        return null;
    }

    private static bool TryClassifyArrow(
        Component component,
        PlotPolygon plot,
        IReadOnlyList<OcrRectangle> annotations,
        out Classification? classification)
    {
        classification = null;
        bool horizontal = component.Width >= MinimumArrowSpan && component.Width >= component.Height * 2;
        bool vertical = component.Height >= MinimumArrowSpan && component.Height >= component.Width * 2;
        if (!horizontal && !vertical)
        {
            return false;
        }

        EndpointProfile endpoints = EndpointProfile.Create(component, horizontal);
        if (endpoints.HeadSpan < 4 || endpoints.HeadSpan < endpoints.TailSpan + 2 || endpoints.AxisCoverage < 0.72)
        {
            return false;
        }

        PixelPoint head = horizontal
            ? new PixelPoint(endpoints.HeadAtMinimum ? component.Left : component.Right, component.CenterY)
            : new PixelPoint(component.CenterX, endpoints.HeadAtMinimum ? component.Top : component.Bottom);
        PixelPoint tail = horizontal
            ? new PixelPoint(endpoints.HeadAtMinimum ? component.Right : component.Left, component.CenterY)
            : new PixelPoint(component.CenterX, endpoints.HeadAtMinimum ? component.Bottom : component.Top);
        if (!Contains(plot, head))
        {
            return false;
        }

        OcrRectangle? matched = annotations
            .Where(annotation => !Contains(plot, annotation.Center))
            .OrderBy(annotation => Distance(annotation, tail))
            .Select(static annotation => (OcrRectangle?)annotation)
            .FirstOrDefault();
        if (matched is null)
        {
            return false;
        }

        double maximumTailDistance = Math.Max(18, Math.Max(matched.Value.Width, matched.Value.Height) * 1.5);
        if (Distance(matched.Value, tail) > maximumTailDistance)
        {
            return false;
        }

        classification = new Classification(
            RasterResidualArtifactCategory.AnnotationArrow,
            AddStructuralBonus(ArrowConfidence, component.ConnectorFraction),
            "asymmetric shaft/head geometry directed from out-of-plot annotation OCR");
        return true;
    }

    private static bool IsBracket(Component component)
    {
        ProjectionProfile profile = ProjectionProfile.Create(component);
        bool vertical = component.Height >= MinimumBracketSpan &&
            component.Width >= 7 &&
            component.Height >= component.Width * 1.25 &&
            Math.Max(profile.LeftEdgeCoverage, profile.RightEdgeCoverage) >= 0.68 &&
            Math.Min(profile.LeftEdgeCoverage, profile.RightEdgeCoverage) <= 0.35 &&
            profile.TopEdgeCoverage >= 0.58 &&
            profile.BottomEdgeCoverage >= 0.58;
        bool horizontal = component.Width >= MinimumBracketSpan &&
            component.Height >= 7 &&
            component.Width >= component.Height * 1.25 &&
            Math.Max(profile.TopEdgeCoverage, profile.BottomEdgeCoverage) >= 0.68 &&
            Math.Min(profile.TopEdgeCoverage, profile.BottomEdgeCoverage) <= 0.35 &&
            profile.LeftEdgeCoverage >= 0.58 &&
            profile.RightEdgeCoverage >= 0.58;
        return vertical || horizontal;
    }

    private static bool IsLegendStructure(
        Component component,
        PlotPolygon plot,
        IReadOnlyList<OcrRectangle> legends)
    {
        foreach (OcrRectangle legend in legends)
        {
            bool labelOrGlyphOutsidePlot = !Contains(plot, legend.Center) ||
                !Contains(plot, new PixelPoint(component.CenterX, component.CenterY));
            if (!labelOrGlyphOutsidePlot)
            {
                continue;
            }

            bool glyphToLeft = component.Right < legend.Left &&
                legend.Left - component.Right - 1 <= Math.Max(8, legend.Height * 3.5) &&
                Math.Abs(component.CenterY - legend.Center.Y) <= Math.Max(component.Height, legend.Height);
            bool enclosingFrame = component.Left <= legend.Left - 2 &&
                component.Right >= legend.Right + 2 &&
                component.Top <= legend.Top - 2 &&
                component.Bottom >= legend.Bottom + 2 &&
                component.Density <= 0.45;
            if ((glyphToLeft && component.Width is >= 3 and <= 24 && component.Height is >= 3 and <= 24) ||
                enclosingFrame)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddIntersections(
        int width,
        int height,
        bool[] foreground,
        float[] mask,
        PlotPolygon plot,
        float[] connectorProbabilities,
        List<RasterResidualArtifactRegion> regions,
        CancellationToken cancellationToken)
    {
        var candidates = new List<IntersectionCandidate>();
        for (int y = MinimumIntersectionArm; y < height - MinimumIntersectionArm; y++)
        {
            for (int x = MinimumIntersectionArm; x < width - MinimumIntersectionArm; x++)
            {
                int index = (y * width) + x;
                if (!foreground[index] || mask[index] >= SeedMaskedThreshold || !Contains(plot, new PixelPoint(x, y)))
                {
                    continue;
                }

                if ((index & 0x1fff) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int pairCount = 0;
                int strength = 0;
                foreach ((int X, int Y) direction in OpposingDirections)
                {
                    int forward = RayLength(foreground, width, height, x, y, direction.X, direction.Y);
                    int backward = RayLength(foreground, width, height, x, y, -direction.X, -direction.Y);
                    if (forward >= MinimumIntersectionArm && backward >= MinimumIntersectionArm)
                    {
                        pairCount++;
                        strength += Math.Min(forward, MaximumIntersectionArm) + Math.Min(backward, MaximumIntersectionArm);
                    }
                }

                if (pairCount >= 2)
                {
                    candidates.Add(new IntersectionCandidate(x, y, strength, connectorProbabilities[index]));
                }
            }
        }

        var accepted = new List<IntersectionCandidate>();
        foreach (IntersectionCandidate candidate in candidates.OrderByDescending(static item => item.Strength))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (accepted.Any(existing => SquaredDistance(existing.X, existing.Y, candidate.X, candidate.Y) <=
                    IntersectionNmsRadius * IntersectionNmsRadius))
            {
                continue;
            }

            accepted.Add(candidate);
            float confidence = AddStructuralBonus(IntersectionConfidence, candidate.ConnectorProbability);
            int inkPixels = 0;
            for (int y = candidate.Y - IntersectionMaskRadius; y <= candidate.Y + IntersectionMaskRadius; y++)
            {
                for (int x = candidate.X - IntersectionMaskRadius; x <= candidate.X + IntersectionMaskRadius; x++)
                {
                    int index = (y * width) + x;
                    mask[index] = Math.Max(mask[index], confidence);
                    if (foreground[index])
                    {
                        inkPixels++;
                    }
                }
            }

            regions.Add(new RasterResidualArtifactRegion(
                RasterResidualArtifactCategory.ConnectingLineIntersection,
                new RasterResidualBounds(
                    candidate.X - IntersectionMaskRadius,
                    candidate.Y - IntersectionMaskRadius,
                    (IntersectionMaskRadius * 2) + 1,
                    (IntersectionMaskRadius * 2) + 1),
                confidence,
                inkPixels,
                "two nonparallel line pairs cross inside the axis plot"));
        }
    }

    private static readonly (int X, int Y)[] OpposingDirections =
    [
        (1, 0),
        (0, 1),
        (1, 1),
        (1, -1),
    ];

    private static int RayLength(
        bool[] foreground,
        int width,
        int height,
        int originX,
        int originY,
        int stepX,
        int stepY)
    {
        int length = 0;
        int x = originX + stepX;
        int y = originY + stepY;
        while (length < MaximumIntersectionArm &&
               x >= 0 && x < width && y >= 0 && y < height &&
               foreground[(y * width) + x])
        {
            length++;
            x += stepX;
            y += stepY;
        }

        return length;
    }

    private static Component ReadComponent(
        int start,
        int width,
        int height,
        bool[] foreground,
        bool[] visited,
        float[] markerProbabilities,
        float[] connectorProbabilities,
        CancellationToken cancellationToken)
    {
        var pixels = new List<int> { start };
        visited[start] = true;
        int cursor = 0;
        int left = start % width;
        int right = left;
        int top = start / width;
        int bottom = top;
        double markerSum = 0;
        double connectorSum = 0;
        while (cursor < pixels.Count)
        {
            if ((cursor & 0xff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int index = pixels[cursor++];
            int x = index % width;
            int y = index / width;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
            markerSum += markerProbabilities[index];
            connectorSum += connectorProbabilities[index];
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                int neighborY = y + offsetY;
                if (neighborY < 0 || neighborY >= height)
                {
                    continue;
                }

                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    int neighborX = x + offsetX;
                    if ((offsetX == 0 && offsetY == 0) || neighborX < 0 || neighborX >= width)
                    {
                        continue;
                    }

                    int neighbor = (neighborY * width) + neighborX;
                    if (foreground[neighbor] && !visited[neighbor])
                    {
                        visited[neighbor] = true;
                        pixels.Add(neighbor);
                    }
                }
            }
        }

        return new Component(
            pixels,
            width,
            left,
            top,
            right,
            bottom,
            (float)(markerSum / pixels.Count),
            (float)(connectorSum / pixels.Count));
    }

    private static void WriteComponentMask(float[] mask, Component component, float confidence)
    {
        foreach (int index in component.Pixels)
        {
            mask[index] = Math.Max(mask[index], confidence);
        }
    }

    private static bool IsCompactAmbiguity(Component component) =>
        component.Width is >= 3 and <= MaximumCompactMarkerSpan &&
        component.Height is >= 3 and <= MaximumCompactMarkerSpan &&
        component.Density >= 0.12;

    private static float AddStructuralBonus(float confidence, float structuralProbability) =>
        Math.Min(1f, confidence + (structuralProbability >= 0.5f ? StructuralConfirmationBonus : 0f));

    private static bool Contains(PlotPolygon polygon, PixelPoint point)
    {
        IReadOnlyList<PixelPoint> vertices = polygon.Points;
        bool inside = false;
        for (int current = 0, previous = vertices.Count - 1; current < vertices.Count; previous = current++)
        {
            PixelPoint first = vertices[current];
            PixelPoint second = vertices[previous];
            bool crosses = (first.Y > point.Y) != (second.Y > point.Y) &&
                point.X < ((second.X - first.X) * (point.Y - first.Y) / (second.Y - first.Y)) + first.X;
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool Contains(PlotPolygon polygon, OcrPoint point) =>
        Contains(polygon, new PixelPoint(point.X, point.Y));

    private static double Distance(OcrRectangle rectangle, PixelPoint point)
    {
        double deltaX = point.X < rectangle.Left
            ? rectangle.Left - point.X
            : point.X > rectangle.Right
                ? point.X - rectangle.Right
                : 0;
        double deltaY = point.Y < rectangle.Top
            ? rectangle.Top - point.Y
            : point.Y > rectangle.Bottom
                ? point.Y - rectangle.Bottom
                : 0;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }

    private static int SquaredDistance(int firstX, int firstY, int secondX, int secondY)
    {
        int deltaX = firstX - secondX;
        int deltaY = firstY - secondY;
        return (deltaX * deltaX) + (deltaY * deltaY);
    }

    private static void ValidateInputs(
        ProductionDecodedRaster raster,
        ProductionAxisGeometryEvidence axis,
        OcrResult ocr,
        ProductionDetectionMaskSeed seed)
    {
        if (raster.Variant != WorkflowImageVariant.Original ||
            raster.OriginalToFrame != MarkerAffineTransform.Identity ||
            raster.OriginalToImage != OcrFrameTransform.Identity ||
            seed.Width != raster.Width || seed.Height != raster.Height ||
            seed.RasterVariant != raster.Variant ||
            !string.Equals(seed.RasterSha256, raster.InputSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(axis.Geometry.CoordinateSpace, AxisGeometryCoordinateSpaces.OriginalPixels, StringComparison.Ordinal) ||
            !string.Equals(axis.Envelope.InputSha256, raster.InputSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ocr.InputSha256, raster.InputSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ocr.CoordinateSpace, OcrContract.CoordinateSpace, StringComparison.Ordinal) ||
            !ocr.Succeeded)
        {
            throw new ArgumentException(
                "Residual artifact analysis requires matching immutable original-pixel raster, axis, OCR, and seed evidence.");
        }
    }

    private static void ValidateStructureResult(
        ProductionDecodedRaster raster,
        PreOcrStructuralProbabilityResult result)
    {
        int expectedLength = checked(raster.Width * raster.Height);
        if (result.Width != raster.Width || result.Height != raster.Height || result.Stride != raster.Width ||
            result.MarkerLikeProbabilities.Length != expectedLength ||
            result.ThinConnectorProbabilities.Length != expectedLength ||
            !string.Equals(result.CoordinateSpace, OcrContract.CoordinateSpace, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Pre-OCR structural evidence does not match the residual raster contract.");
        }
    }

    private sealed record Classification(
        RasterResidualArtifactCategory Category,
        float Confidence,
        string Reason);

    private sealed record IntersectionCandidate(
        int X,
        int Y,
        int Strength,
        float ConnectorProbability);

    private sealed class Component
    {
        public Component(
            IReadOnlyList<int> pixels,
            int imageWidth,
            int left,
            int top,
            int right,
            int bottom,
            float markerFraction,
            float connectorFraction)
        {
            Pixels = pixels;
            ImageWidth = imageWidth;
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
            MarkerFraction = markerFraction;
            ConnectorFraction = connectorFraction;
        }

        public IReadOnlyList<int> Pixels { get; }

        public int ImageWidth { get; }

        public int Left { get; }

        public int Top { get; }

        public int Right { get; }

        public int Bottom { get; }

        public int Width => Right - Left + 1;

        public int Height => Bottom - Top + 1;

        public double CenterX => (Left + Right) / 2d;

        public double CenterY => (Top + Bottom) / 2d;

        public double Density => Pixels.Count / (double)(Width * Height);

        public float MarkerFraction { get; }

        public float ConnectorFraction { get; }

        public RasterResidualBounds Bounds => new(Left, Top, Width, Height);
    }

    private sealed record EndpointProfile(
        int HeadSpan,
        int TailSpan,
        bool HeadAtMinimum,
        double AxisCoverage)
    {
        public static EndpointProfile Create(Component component, bool horizontal)
        {
            int axisLength = horizontal ? component.Width : component.Height;
            int perpendicularLength = horizontal ? component.Height : component.Width;
            int band = Math.Max(2, axisLength / 5);
            var minimumPerpendicular = new bool[perpendicularLength];
            var maximumPerpendicular = new bool[perpendicularLength];
            var occupiedAxis = new bool[axisLength];
            foreach (int index in component.Pixels)
            {
                int localX = (index % component.ImageWidth) - component.Left;
                int localY = (index / component.ImageWidth) - component.Top;
                int axis = horizontal ? localX : localY;
                int perpendicular = horizontal ? localY : localX;
                occupiedAxis[axis] = true;
                if (axis < band)
                {
                    minimumPerpendicular[perpendicular] = true;
                }

                if (axis >= axisLength - band)
                {
                    maximumPerpendicular[perpendicular] = true;
                }
            }

            int minimumSpan = minimumPerpendicular.Count(static value => value);
            int maximumSpan = maximumPerpendicular.Count(static value => value);
            bool headAtMinimum = minimumSpan >= maximumSpan;
            return new EndpointProfile(
                Math.Max(minimumSpan, maximumSpan),
                Math.Min(minimumSpan, maximumSpan),
                headAtMinimum,
                occupiedAxis.Count(static value => value) / (double)axisLength);
        }
    }

    private sealed record ProjectionProfile(
        double LeftEdgeCoverage,
        double RightEdgeCoverage,
        double TopEdgeCoverage,
        double BottomEdgeCoverage)
    {
        public static ProjectionProfile Create(Component component)
        {
            var leftRows = new bool[component.Height];
            var rightRows = new bool[component.Height];
            var topColumns = new bool[component.Width];
            var bottomColumns = new bool[component.Width];
            foreach (int index in component.Pixels)
            {
                int localX = (index % component.ImageWidth) - component.Left;
                int localY = (index / component.ImageWidth) - component.Top;
                if (localX <= 1)
                {
                    leftRows[localY] = true;
                }

                if (localX >= component.Width - 2)
                {
                    rightRows[localY] = true;
                }

                if (localY <= 1)
                {
                    topColumns[localX] = true;
                }

                if (localY >= component.Height - 2)
                {
                    bottomColumns[localX] = true;
                }
            }

            return new ProjectionProfile(
                leftRows.Count(static value => value) / (double)component.Height,
                rightRows.Count(static value => value) / (double)component.Height,
                topColumns.Count(static value => value) / (double)component.Width,
                bottomColumns.Count(static value => value) / (double)component.Width);
        }
    }
}
