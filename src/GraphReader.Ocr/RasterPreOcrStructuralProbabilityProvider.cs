// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

/// <summary>
/// Immutable Gray8 evidence available before OCR. The geometry mask contains
/// only axis, tick, divider, and ambiguous grid lines already resolved in
/// original-image pixels.
/// </summary>
public sealed record PreOcrStructuralFrame(
    int Width,
    int Height,
    int Gray8Stride,
    ReadOnlyMemory<byte> Gray8Pixels,
    int GeometryMaskStride,
    ReadOnlyMemory<byte> GeometryMask,
    string CoordinateSpace = OcrContract.CoordinateSpace);

/// <summary>
/// Descriptive raster evidence only. These planes are not production-approved
/// suppression masks and have no enabled operating threshold.
/// </summary>
public sealed record PreOcrStructuralProbabilityResult(
    int Width,
    int Height,
    int Stride,
    ReadOnlyMemory<float> MarkerLikeProbabilities,
    ReadOnlyMemory<float> ThinConnectorProbabilities,
    string CoordinateSpace = OcrContract.CoordinateSpace);

public interface IPreOcrStructuralProbabilityProvider
{
    ValueTask<PreOcrStructuralProbabilityResult> AnalyzeAsync(
        PreOcrStructuralFrame frame,
        CancellationToken cancellationToken);
}

/// <summary>
/// Derives conservative structure probabilities from raster morphology. An
/// isolated hollow component remains below 0.5 because an open marker and a
/// small letter O can be pixel-identical without post-OCR context.
/// </summary>
public sealed class RasterPreOcrStructuralProbabilityProvider :
    IPreOcrStructuralProbabilityProvider
{
    private const float TextAssociatedProbability = 0.04f;
    private const float OtherInkProbability = 0.08f;
    private const float AmbiguousHollowProbability = 0.35f;
    private const float MarkerProbability = 0.90f;
    private const float ConnectorProbability = 0.90f;
    private const int SpatialCellSize = 32;
    private const int MaximumGlyphWidth = 36;
    private const int MaximumGlyphHeight = 48;
    private const int MaximumGlyphArea = 1_000;
    private const int MaximumNeighborSearchPixels = 20;
    private const int MaximumNeighborCandidatesPerComponent = 2_048;
    private const int CancellationPollingMask = 255;
    private const int LocalDescriptorRadius = 2;

    public ValueTask<PreOcrStructuralProbabilityResult> AnalyzeAsync(
        PreOcrStructuralFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Validate(frame);
        cancellationToken.ThrowIfCancellationRequested();

        byte foregroundThreshold = EstimateForegroundThreshold(frame, cancellationToken);
        List<Component> components = FindComponents(frame, foregroundThreshold, cancellationToken);
        bool[] textAssociated = FindTextAssociatedComponents(components, cancellationToken);
        int pixelCount = checked(frame.Width * frame.Height);
        var markerProbabilities = new float[pixelCount];
        var connectorProbabilities = new float[pixelCount];

        for (int componentIndex = 0; componentIndex < components.Count; componentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Component component = components[componentIndex];
            WriteProbabilities(
                component,
                textAssociated[componentIndex],
                frame.Width,
                markerProbabilities,
                connectorProbabilities,
                cancellationToken);
        }

        return ValueTask.FromResult(new PreOcrStructuralProbabilityResult(
            frame.Width,
            frame.Height,
            frame.Width,
            markerProbabilities,
            connectorProbabilities));
    }

    private static void WriteProbabilities(
        Component component,
        bool textAssociated,
        int imageWidth,
        float[] markerProbabilities,
        float[] connectorProbabilities,
        CancellationToken cancellationToken)
    {
        if (!textAssociated && IsLongSparseStroke(component))
        {
            var componentPixels = new HashSet<int>(component.Area);
            int indexed = 0;
            foreach (int pixelIndex in component.Pixels)
            {
                if ((indexed++ & CancellationPollingMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                componentPixels.Add(pixelIndex);
            }

            int written = 0;
            foreach (int pixelIndex in component.Pixels)
            {
                if ((written++ & CancellationPollingMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                bool markerCore = IsLocalBlobCore(component, componentPixels, imageWidth, pixelIndex);
                markerProbabilities[pixelIndex] = markerCore
                    ? MarkerProbability
                    : OtherInkProbability;
                connectorProbabilities[pixelIndex] = markerCore
                    ? OtherInkProbability
                    : ConnectorProbability;
            }

            return;
        }

        (float marker, float connector) = Classify(component, textAssociated);
        int outputIndex = 0;
        foreach (int pixelIndex in component.Pixels)
        {
            if ((outputIndex++ & CancellationPollingMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            markerProbabilities[pixelIndex] = marker;
            connectorProbabilities[pixelIndex] = connector;
        }
    }

    private static bool IsLocalBlobCore(
        Component component,
        HashSet<int> componentPixels,
        int imageWidth,
        int pixelIndex)
    {
        int centerX = pixelIndex % imageWidth;
        int centerY = pixelIndex / imageWidth;
        Span<int> rows = stackalloc int[(LocalDescriptorRadius * 2) + 1];
        Span<int> columns = stackalloc int[(LocalDescriptorRadius * 2) + 1];
        int localInk = 0;

        for (int yOffset = -LocalDescriptorRadius; yOffset <= LocalDescriptorRadius; yOffset++)
        {
            int y = centerY + yOffset;
            if (y < component.Top || y > component.Bottom)
            {
                continue;
            }

            for (int xOffset = -LocalDescriptorRadius; xOffset <= LocalDescriptorRadius; xOffset++)
            {
                int x = centerX + xOffset;
                if (x < component.Left || x > component.Right ||
                    !componentPixels.Contains((y * imageWidth) + x))
                {
                    continue;
                }

                rows[yOffset + LocalDescriptorRadius]++;
                columns[xOffset + LocalDescriptorRadius]++;
                localInk++;
            }
        }

        int occupiedRows = 0;
        int occupiedColumns = 0;
        int maximumRowInk = 0;
        int maximumColumnInk = 0;
        for (int index = 0; index < rows.Length; index++)
        {
            if (rows[index] > 0)
            {
                occupiedRows++;
            }

            if (columns[index] > 0)
            {
                occupiedColumns++;
            }

            maximumRowInk = Math.Max(maximumRowInk, rows[index]);
            maximumColumnInk = Math.Max(maximumColumnInk, columns[index]);
        }

        return localInk >= 10 &&
            occupiedRows >= 4 &&
            occupiedColumns >= 4 &&
            maximumRowInk >= 3 &&
            maximumColumnInk >= 3;
    }

    private static (float Marker, float Connector) Classify(
        Component component,
        bool textAssociated)
    {
        if (textAssociated)
        {
            return (TextAssociatedProbability, TextAssociatedProbability);
        }

        if (IsLongSparseStroke(component))
        {
            return (OtherInkProbability, ConnectorProbability);
        }

        if (IsFilledMarkerLike(component))
        {
            return (MarkerProbability, OtherInkProbability);
        }

        double aspect = component.Width / (double)component.Height;
        bool compact = aspect is >= 0.65 and <= 1.55 &&
            component.Width is >= 4 and <= 12 &&
            component.Height is >= 4 and <= 12;
        bool hollowOrCompactAmbiguity = compact &&
            component.Density >= 0.20 &&
            component.MaximumRowFillFraction >= 0.45 &&
            component.MaximumColumnFillFraction >= 0.45;
        if (hollowOrCompactAmbiguity)
        {
            return (AmbiguousHollowProbability, OtherInkProbability);
        }

        return (OtherInkProbability, OtherInkProbability);
    }

    private static bool[] FindTextAssociatedComponents(
        List<Component> components,
        CancellationToken cancellationToken)
    {
        var associated = new bool[components.Count];
        var spatialIndex = new Dictionary<(int X, int Y), List<int>>();
        for (int componentIndex = 0; componentIndex < components.Count; componentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Component component = components[componentIndex];
            if (!IsGlyphSized(component))
            {
                continue;
            }

            for (int cellY = component.Top / SpatialCellSize;
                 cellY <= component.Bottom / SpatialCellSize;
                 cellY++)
            {
                for (int cellX = component.Left / SpatialCellSize;
                     cellX <= component.Right / SpatialCellSize;
                     cellX++)
                {
                    if (!spatialIndex.TryGetValue((cellX, cellY), out List<int>? bucket))
                    {
                        bucket = [];
                        spatialIndex.Add((cellX, cellY), bucket);
                    }

                    bucket.Add(componentIndex);
                }
            }
        }

        for (int leftIndex = 0; leftIndex < components.Count; leftIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Component left = components[leftIndex];
            if (!IsGlyphSized(left))
            {
                continue;
            }

            var localCandidates = new HashSet<int>();
            int minimumCellX = Math.Max(0, left.Left - MaximumNeighborSearchPixels) / SpatialCellSize;
            int maximumCellX = (left.Right + MaximumNeighborSearchPixels) / SpatialCellSize;
            int minimumCellY = Math.Max(0, left.Top - MaximumNeighborSearchPixels) / SpatialCellSize;
            int maximumCellY = (left.Bottom + MaximumNeighborSearchPixels) / SpatialCellSize;
            for (int cellY = minimumCellY; cellY <= maximumCellY; cellY++)
            {
                for (int cellX = minimumCellX; cellX <= maximumCellX; cellX++)
                {
                    if (!spatialIndex.TryGetValue((cellX, cellY), out List<int>? bucket))
                    {
                        continue;
                    }

                    foreach (int candidateIndex in bucket)
                    {
                        if (candidateIndex > leftIndex)
                        {
                            localCandidates.Add(candidateIndex);
                        }
                    }
                }
            }

            int inspected = 0;
            foreach (int rightIndex in localCandidates)
            {
                if ((inspected++ & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (inspected > MaximumNeighborCandidatesPerComponent)
                {
                    associated[leftIndex] = true;
                    break;
                }

                Component right = components[rightIndex];
                if (AreGlyphNeighbors(left, right))
                {
                    associated[leftIndex] = true;
                    associated[rightIndex] = true;
                }
            }
        }

        return associated;
    }

    private static bool IsGlyphSized(Component component) =>
        component.Width <= MaximumGlyphWidth &&
        component.Height <= MaximumGlyphHeight &&
        component.Area <= MaximumGlyphArea;

    private static bool AreGlyphNeighbors(Component first, Component second)
    {
        if (IsStrongStructure(first) && IsStrongStructure(second))
        {
            return false;
        }

        int horizontalGap = Gap(first.Left, first.Right, second.Left, second.Right);
        int verticalGap = Gap(first.Top, first.Bottom, second.Top, second.Bottom);
        int verticalOverlap = Overlap(first.Top, first.Bottom, second.Top, second.Bottom);
        int horizontalOverlap = Overlap(first.Left, first.Right, second.Left, second.Right);
        double firstCenterX = (first.Left + first.Right) / 2d;
        double secondCenterX = (second.Left + second.Right) / 2d;
        double firstCenterY = (first.Top + first.Bottom) / 2d;
        double secondCenterY = (second.Top + second.Bottom) / 2d;

        int maximumHorizontalGap = Math.Min(
            MaximumNeighborSearchPixels,
            Math.Max(2, (int)Math.Ceiling(Math.Min(first.Height, second.Height) * 0.45)));
        int maximumVerticalGap = Math.Min(
            MaximumNeighborSearchPixels,
            Math.Max(2, (int)Math.Ceiling(Math.Min(first.Width, second.Width) * 0.45)));
        bool sameTextRow = horizontalGap <= maximumHorizontalGap &&
            Math.Abs(firstCenterY - secondCenterY) <= Math.Max(first.Height, second.Height) * 0.75 &&
            (verticalOverlap > 0 || verticalGap <= maximumVerticalGap);
        bool sameRotatedTextColumn = verticalGap <= maximumVerticalGap &&
            Math.Abs(firstCenterX - secondCenterX) <= Math.Max(first.Width, second.Width) * 0.75 &&
            (horizontalOverlap > 0 || horizontalGap <= maximumHorizontalGap);
        return sameTextRow || sameRotatedTextColumn;
    }

    private static bool IsStrongStructure(Component component) =>
        IsLongSparseStroke(component) || IsFilledMarkerLike(component);

    private static bool IsLongSparseStroke(Component component)
    {
        double aspect = component.Width / (double)component.Height;
        double diagonal = Math.Sqrt(
            (component.Width * component.Width) + (component.Height * component.Height));
        return diagonal >= 12 &&
            (((aspect >= 2.5 || aspect <= 0.4) &&
              (Math.Min(component.Width, component.Height) <= 3 || component.Density <= 0.55)) ||
             component.Density <= 0.35);
    }

    private static bool IsFilledMarkerLike(Component component)
    {
        double aspect = component.Width / (double)component.Height;
        bool compact = aspect is >= 0.65 and <= 1.55 &&
            component.Width is >= 4 and <= 12 &&
            component.Height is >= 4 and <= 12;
        return compact &&
            component.Density >= 0.60 &&
            component.MaximumRowFillFraction >= 0.75 &&
            component.MaximumColumnFillFraction >= 0.75;
    }

    private static int Gap(int firstMinimum, int firstMaximum, int secondMinimum, int secondMaximum) =>
        firstMaximum < secondMinimum
            ? secondMinimum - firstMaximum - 1
            : secondMaximum < firstMinimum
                ? firstMinimum - secondMaximum - 1
                : 0;

    private static int Overlap(int firstMinimum, int firstMaximum, int secondMinimum, int secondMaximum) =>
        Math.Max(0, Math.Min(firstMaximum, secondMaximum) - Math.Max(firstMinimum, secondMinimum) + 1);

    private static List<Component> FindComponents(
        PreOcrStructuralFrame frame,
        byte threshold,
        CancellationToken cancellationToken)
    {
        int pixelCount = checked(frame.Width * frame.Height);
        var visited = new bool[pixelCount];
        var components = new List<Component>();
        var queue = new Queue<int>();

        for (int y = 0; y < frame.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < frame.Width; x++)
            {
                int compactIndex = (y * frame.Width) + x;
                if (visited[compactIndex])
                {
                    continue;
                }

                visited[compactIndex] = true;
                if (!IsForeground(frame, x, y, threshold))
                {
                    continue;
                }

                queue.Enqueue(compactIndex);
                var pixels = new List<int>();
                int left = x;
                int right = x;
                int top = y;
                int bottom = y;
                int traversed = 0;
                while (queue.Count > 0)
                {
                    if ((traversed++ & CancellationPollingMask) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    int current = queue.Dequeue();
                    pixels.Add(current);
                    int currentX = current % frame.Width;
                    int currentY = current / frame.Width;
                    left = Math.Min(left, currentX);
                    right = Math.Max(right, currentX);
                    top = Math.Min(top, currentY);
                    bottom = Math.Max(bottom, currentY);
                    for (int yOffset = -1; yOffset <= 1; yOffset++)
                    {
                        for (int xOffset = -1; xOffset <= 1; xOffset++)
                        {
                            if (xOffset == 0 && yOffset == 0)
                            {
                                continue;
                            }

                            Visit(currentX + xOffset, currentY + yOffset);
                        }
                    }
                }

                components.Add(Component.Create(
                    frame.Width,
                    pixels,
                    left,
                    top,
                    right,
                    bottom,
                    cancellationToken));

                void Visit(int neighborX, int neighborY)
                {
                    if (neighborX < 0 || neighborY < 0 ||
                        neighborX >= frame.Width || neighborY >= frame.Height)
                    {
                        return;
                    }

                    int neighbor = (neighborY * frame.Width) + neighborX;
                    if (visited[neighbor])
                    {
                        return;
                    }

                    visited[neighbor] = true;
                    if (IsForeground(frame, neighborX, neighborY, threshold))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }

        return components;
    }

    private static bool IsForeground(
        PreOcrStructuralFrame frame,
        int x,
        int y,
        byte threshold)
    {
        if (frame.GeometryMask.Span[(y * frame.GeometryMaskStride) + x] != 0)
        {
            return false;
        }

        return frame.Gray8Pixels.Span[(y * frame.Gray8Stride) + x] <= threshold;
    }

    private static byte EstimateForegroundThreshold(
        PreOcrStructuralFrame frame,
        CancellationToken cancellationToken)
    {
        long sum = 0;
        int samples = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < frame.Width; x++)
            {
                if (frame.GeometryMask.Span[(y * frame.GeometryMaskStride) + x] != 0)
                {
                    continue;
                }

                sum += frame.Gray8Pixels.Span[(y * frame.Gray8Stride) + x];
                samples++;
            }
        }

        double mean = samples == 0 ? byte.MaxValue : sum / (double)samples;
        return (byte)Math.Clamp(Math.Round(mean * 0.80), 64, 224);
    }

    private static void Validate(PreOcrStructuralFrame frame)
    {
        bool invalidDimensions = frame.Width <= 0 || frame.Height <= 0 ||
            frame.Gray8Stride < frame.Width || frame.GeometryMaskStride < frame.Width;
        bool invalidLengths = !invalidDimensions &&
            (frame.Gray8Pixels.Length < checked(frame.Gray8Stride * frame.Height) ||
             frame.GeometryMask.Length < checked(frame.GeometryMaskStride * frame.Height));
        if (invalidDimensions || invalidLengths ||
            !string.Equals(frame.CoordinateSpace, OcrContract.CoordinateSpace, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Pre-OCR structure input must be a same-size Gray8 raster and geometry mask in original_pixels.",
                nameof(frame));
        }
    }

    private sealed record Component(
        IReadOnlyList<int> Pixels,
        int Left,
        int Top,
        int Right,
        int Bottom,
        double Density,
        double MaximumRowFillFraction,
        double MaximumColumnFillFraction)
    {
        public int Width => Right - Left + 1;

        public int Height => Bottom - Top + 1;

        public int Area => Pixels.Count;

        public static Component Create(
            int imageWidth,
            List<int> pixels,
            int left,
            int top,
            int right,
            int bottom,
            CancellationToken cancellationToken)
        {
            int width = right - left + 1;
            int height = bottom - top + 1;
            var rows = new int[height];
            var columns = new int[width];
            for (int pixelIndex = 0; pixelIndex < pixels.Count; pixelIndex++)
            {
                if ((pixelIndex & CancellationPollingMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int pixel = pixels[pixelIndex];
                int x = pixel % imageWidth;
                int y = pixel / imageWidth;
                rows[y - top]++;
                columns[x - left]++;
            }

            return new Component(
                Array.AsReadOnly(pixels.ToArray()),
                left,
                top,
                right,
                bottom,
                pixels.Count / (double)checked(width * height),
                rows.Max() / (double)width,
                columns.Max() / (double)height);
        }
    }
}
