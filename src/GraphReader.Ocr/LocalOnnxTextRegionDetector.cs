// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GraphReader.Inference;
using OpenCvSharp;

namespace GraphReader.Ocr;

public enum OcrDetectionOutputActivation
{
    Probability,
    SigmoidLogit,
    ProbabilityWithParityTolerance,
}

public enum OcrDetectionPostprocessAlgorithm
{
    DenseProbabilityComponentsV1,
    DbPostprocessV1,
}

public enum OcrDbScoreMode
{
    FastMiniBox,
}

public sealed record LocalOnnxTextRegionDetectorOptions(ModelIdentity Model)
{
    public int MaximumSideLength { get; init; } = 960;

    public int DimensionMultiple { get; init; } = 32;

    public int InputChannels { get; init; } = 3;

    public OcrTensorLayout InputLayout { get; init; } = OcrTensorLayout.ChannelsFirst;

    public OcrTensorColorMode InputColorMode { get; init; } =
        OcrTensorColorMode.GrayscaleReplicated;

    public IReadOnlyList<float> ChannelMeans { get; init; } = [0.485f, 0.456f, 0.406f];

    public IReadOnlyList<float> ChannelScales { get; init; } = [1f / 0.229f, 1f / 0.224f, 1f / 0.225f];

    public string InputName { get; init; } = "input";

    public string OutputName { get; init; } = "output";

    public string StageVersion { get; init; } = "0.1.0";

    public OcrDetectionOutputActivation OutputActivation { get; init; } =
        OcrDetectionOutputActivation.Probability;

    public OcrDetectionPostprocessAlgorithm PostprocessAlgorithm { get; init; } =
        OcrDetectionPostprocessAlgorithm.DenseProbabilityComponentsV1;

    public OcrDbScoreMode DbScoreMode { get; init; } = OcrDbScoreMode.FastMiniBox;

    public float ProbabilityThreshold { get; init; } = 0.30f;

    public float BoxConfidenceThreshold { get; init; } = 0.55f;

    public double UnclipRatio { get; init; } = 1.5;

    public int MinimumComponentArea { get; init; } = 3;

    public int MinimumSideLength { get; init; } = 2;

    public int MaximumRegions { get; init; } = 1000;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<InferenceProvider>? AllowedProviders { get; init; }

    public bool BypassCache { get; init; }
}

/// <summary>
/// Executes a checksum-bound local ONNX text-probability model and maps the
/// selected deterministic postprocessing geometry back to immutable original
/// pixels. This adapter contains no weights and does not imply model approval.
/// </summary>
public sealed class LocalOnnxTextRegionDetector : ITextRegionDetector
{
    public const float ProbabilityParityTolerance = 0.00001f;

    private const string OfficialDbResizeAlgorithm = "opencv-inter-linear-uint8-v1";

    private const string LegacyResizeAlgorithm = "floating-bilinear-v1";

    private readonly InferenceRuntime runtime;
    private readonly LocalOnnxTextRegionDetectorOptions options;
    private readonly string configurationFingerprint;

    public LocalOnnxTextRegionDetector(
        InferenceRuntime runtime,
        LocalOnnxTextRegionDetectorOptions options)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options with
        {
            ChannelMeans = options.ChannelMeans is null
                ? Array.Empty<float>()
                : Array.AsReadOnly(options.ChannelMeans.ToArray()),
            ChannelScales = options.ChannelScales is null
                ? Array.Empty<float>()
                : Array.AsReadOnly(options.ChannelScales.ToArray()),
            AllowedProviders = options.AllowedProviders is null
                ? null
                : Array.AsReadOnly(options.AllowedProviders.ToArray()),
        };
        options.Model.Validate();
        ValidateOptions(this.options);
        configurationFingerprint = CreateConfigurationFingerprint(this.options);
    }

    public string ConfigurationFingerprint => configurationFingerprint;

    public async ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateImage(image, options);
        cancellationToken.ThrowIfCancellationRequested();

        (int tensorWidth, int tensorHeight) = TensorDimensions(image, options);
        float[] tensor = CreateTensor(image, tensorWidth, tensorHeight, options, cancellationToken);
        IReadOnlyList<long> shape = options.InputLayout == OcrTensorLayout.ChannelsFirst
            ? [1, options.InputChannels, tensorHeight, tensorWidth]
            : [1, tensorHeight, tensorWidth, options.InputChannels];
        ReadOnlySpan<byte> consumedPixels = options.InputColorMode == OcrTensorColorMode.Bgr
            ? image.BgrPixels!.Pixels.Span
            : image.Pixels.Span;
        string imageSha256 = Convert.ToHexStringLower(SHA256.HashData(consumedPixels));
        var request = new InferenceRequest(
            options.Model,
            new InferenceInput(tensor, shape, options.InputName, options.OutputName),
            new StageCacheMaterial(
                imageSha256,
                FormattableString.Invariant($"0,0,{image.Width},{image.Height}"),
                TransformFingerprint(image.OriginalToImage),
                "ocr_detection",
                options.StageVersion,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["input_width"] = tensorWidth,
                    ["input_height"] = tensorHeight,
                    ["resize_algorithm"] = TensorResizeAlgorithm(options),
                    ["input_channels"] = options.InputChannels,
                    ["input_layout"] = options.InputLayout.ToString(),
                    ["input_color_mode"] = options.InputColorMode.ToString(),
                    ["channel_means"] = options.ChannelMeans.ToArray(),
                    ["channel_scales"] = options.ChannelScales.ToArray(),
                    ["output_activation"] = options.OutputActivation.ToString(),
                    ["output_probability_parity_tolerance"] =
                        options.OutputActivation == OcrDetectionOutputActivation.ProbabilityWithParityTolerance
                            ? ProbabilityParityTolerance
                            : null,
                    ["postprocess_algorithm"] = options.PostprocessAlgorithm.ToString(),
                    ["db_score_mode"] = options.PostprocessAlgorithm == OcrDetectionPostprocessAlgorithm.DbPostprocessV1
                        ? options.DbScoreMode.ToString()
                        : null,
                    ["probability_threshold"] = options.ProbabilityThreshold,
                    ["box_confidence_threshold"] = options.BoxConfidenceThreshold,
                    ["unclip_ratio"] = options.UnclipRatio,
                    ["minimum_component_area"] = options.PostprocessAlgorithm == OcrDetectionPostprocessAlgorithm.DenseProbabilityComponentsV1
                        ? options.MinimumComponentArea
                        : null,
                    ["minimum_side_length"] = options.MinimumSideLength,
                    ["maximum_regions"] = options.MaximumRegions,
                    ["allowed_providers"] = ProviderFingerprint(options.AllowedProviders),
                },
                OcrContract.Version),
            options.Timeout,
            options.AllowedProviders,
            options.BypassCache);

        InferenceResponse response = await runtime.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded || response.Execution is null)
        {
            string diagnostic = response.Error is null
                ? "The OCR detection runtime returned no execution evidence."
                : $"{response.Error.Code}: {response.Error.TechnicalMessage}";
            throw new InvalidOperationException(diagnostic);
        }

        if (options.AllowedProviders is not null &&
            !options.AllowedProviders.Contains(response.Execution.Provider))
        {
            throw new InvalidDataException(
                $"OCR detection executed with undeclared provider '{response.Execution.Provider}'.");
        }

        int expectedOutputCount = checked(tensorWidth * tensorHeight);
        if (response.Execution.Output.Count != expectedOutputCount)
        {
            throw new InvalidDataException(
                $"OCR detection output contained {response.Execution.Output.Count} values; {expectedOutputCount} were required.");
        }

        float[] probabilities = response.Execution.Output.ToArray();
        for (var index = 0; index < probabilities.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probabilities[index] = ToProbability(probabilities[index], options.OutputActivation);
        }

        return options.PostprocessAlgorithm switch
        {
            OcrDetectionPostprocessAlgorithm.DenseProbabilityComponentsV1 =>
                BuildDenseRegions(probabilities, image, tensorWidth, tensorHeight, options, cancellationToken),
            OcrDetectionPostprocessAlgorithm.DbPostprocessV1 =>
                BuildDbRegions(probabilities, image, tensorWidth, tensorHeight, options, cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unsupported OCR detection postprocess algorithm '{options.PostprocessAlgorithm}'."),
        };
    }

    private static ReadOnlyCollection<OcrDetectedRegion> BuildDenseRegions(
        float[] probabilities,
        OcrImage image,
        int tensorWidth,
        int tensorHeight,
        LocalOnnxTextRegionDetectorOptions options,
        CancellationToken cancellationToken)
    {
        Component[] components = FindComponents(
                probabilities,
                tensorWidth,
                tensorHeight,
                options,
                cancellationToken)
            .Where(component => component.Confidence >= options.BoxConfidenceThreshold)
            .OrderByDescending(static component => component.Confidence)
            .ThenByDescending(static component => component.PixelCount)
            .ThenBy(static component => component.Top)
            .ThenBy(static component => component.Left)
            .Take(options.MaximumRegions)
            .OrderBy(static component => component.Top)
            .ThenBy(static component => component.Left)
            .ToArray();

        var regions = new List<OcrDetectedRegion>(components.Length);
        foreach (Component component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle rectangle = MapToOriginal(
                component,
                image,
                tensorWidth,
                tensorHeight,
                options.UnclipRatio);
            if (!rectangle.IsValid)
            {
                continue;
            }

            double density = component.PixelCount /
                (double)checked(component.Width * component.Height);
            OcrPolygon polygon = OcrPolygon.FromRectangle(rectangle);
            regions.Add(new OcrDetectedRegion(
                DeterministicRegionId(options.Model.Sha256, options.PostprocessAlgorithm, polygon),
                polygon,
                OrientationDegrees: 0,
                DetectionConfidence: component.Confidence,
                CoordinateSpace: OcrContract.CoordinateSpace,
                Evidence: new OcrRegionEvidence(
                    ComponentCount: 1,
                    InkDensity: Math.Clamp(density, 0, 1),
                    TextLikelihood: component.Confidence,
                    StructureLikelihood: 1 - component.Confidence,
                    LikelyGraphStructure: false,
                    Reasons: Array.AsReadOnly(["onnx_dense_text_probability"]))));
        }

        return regions.AsReadOnly();
    }

    private static ReadOnlyCollection<OcrDetectedRegion> BuildDbRegions(
        float[] probabilities,
        OcrImage image,
        int tensorWidth,
        int tensorHeight,
        LocalOnnxTextRegionDetectorOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var binary = new Mat(tensorHeight, tensorWidth, MatType.CV_8UC1);
        var binaryPixels = new byte[probabilities.Length];
        for (var index = 0; index < probabilities.Length; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            binaryPixels[index] = probabilities[index] > options.ProbabilityThreshold
                ? byte.MaxValue
                : (byte)0;
        }

        Marshal.Copy(binaryPixels, 0, binary.Data, binaryPixels.Length);
        cancellationToken.ThrowIfCancellationRequested();
        Cv2.FindContours(
            binary,
            out Point[][] contours,
            out _,
            RetrievalModes.List,
            ContourApproximationModes.ApproxSimple);
        cancellationToken.ThrowIfCancellationRequested();

        int candidateCount = Math.Min(contours.Length, options.MaximumRegions);
        var candidates = new List<DbCandidate>(candidateCount);
        for (var contourIndex = 0; contourIndex < candidateCount; contourIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Point[] contour = contours[contourIndex];
            if (contour.Length < 3)
            {
                continue;
            }

            MiniBox initial = GetMiniBox(contour);
            if (initial.ShortSide < options.MinimumSideLength)
            {
                continue;
            }

            PolygonScore score = ScorePolygon(
                probabilities,
                tensorWidth,
                tensorHeight,
                initial.Points,
                options.ProbabilityThreshold,
                cancellationToken);
            if (score.Confidence < options.BoxConfidenceThreshold)
            {
                continue;
            }

            Point2f[][] offsetPaths = UnclipRound(initial.Points, options.UnclipRatio);
            if (offsetPaths.Length != 1)
            {
                continue;
            }

            MiniBox expanded = GetMiniBox(offsetPaths[0]);
            if (expanded.ShortSide < options.MinimumSideLength + 2)
            {
                continue;
            }

            OcrPolygon polygon = MapPolygonToOriginal(
                expanded.Points,
                image,
                tensorWidth,
                tensorHeight);
            if (!polygon.Bounds.IsValid)
            {
                continue;
            }

            candidates.Add(new DbCandidate(
                polygon,
                OrientationDegrees(polygon.Points.ToArray()),
                score.Confidence,
                score.InkDensity));
        }

        OcrDetectedRegion[] regions = candidates
            .Select(candidate => new OcrDetectedRegion(
                DeterministicRegionId(options.Model.Sha256, options.PostprocessAlgorithm, candidate.Polygon),
                candidate.Polygon,
                candidate.OrientationDegrees,
                candidate.Confidence,
                CoordinateSpace: OcrContract.CoordinateSpace,
                Evidence: new OcrRegionEvidence(
                    ComponentCount: 1,
                    InkDensity: candidate.InkDensity,
                    TextLikelihood: candidate.Confidence,
                    StructureLikelihood: 1 - candidate.Confidence,
                    LikelyGraphStructure: false,
                    Reasons: Array.AsReadOnly(["onnx_db_text_probability"]))))
            .ToArray();
        return Array.AsReadOnly(regions);
    }

    private static IEnumerable<Component> FindComponents(
        float[] probabilities,
        int width,
        int height,
        LocalOnnxTextRegionDetectorOptions options,
        CancellationToken cancellationToken)
    {
        var visited = new bool[probabilities.Length];
        var queue = new Queue<int>();
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                int seed = checked((y * width) + x);
                if (visited[seed])
                {
                    continue;
                }

                visited[seed] = true;
                if (probabilities[seed] < options.ProbabilityThreshold)
                {
                    continue;
                }

                queue.Enqueue(seed);
                int left = x;
                int right = x;
                int top = y;
                int bottom = y;
                int pixelCount = 0;
                double confidenceSum = 0;
                while (queue.Count > 0)
                {
                    if ((pixelCount & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    int current = queue.Dequeue();
                    int currentX = current % width;
                    int currentY = current / width;
                    left = Math.Min(left, currentX);
                    right = Math.Max(right, currentX);
                    top = Math.Min(top, currentY);
                    bottom = Math.Max(bottom, currentY);
                    pixelCount++;
                    confidenceSum += probabilities[current];
                    for (var offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        for (var offsetX = -1; offsetX <= 1; offsetX++)
                        {
                            if (offsetX == 0 && offsetY == 0)
                            {
                                continue;
                            }

                            int neighborX = currentX + offsetX;
                            int neighborY = currentY + offsetY;
                            if (neighborX < 0 || neighborY < 0 || neighborX >= width || neighborY >= height)
                            {
                                continue;
                            }

                            int neighbor = checked((neighborY * width) + neighborX);
                            if (visited[neighbor])
                            {
                                continue;
                            }

                            visited[neighbor] = true;
                            if (probabilities[neighbor] >= options.ProbabilityThreshold)
                            {
                                queue.Enqueue(neighbor);
                            }
                        }
                    }
                }

                var component = new Component(
                    left,
                    top,
                    right,
                    bottom,
                    pixelCount,
                    Math.Clamp(confidenceSum / Math.Max(1, pixelCount), 0, 1));
                if (component.PixelCount >= options.MinimumComponentArea &&
                    component.Width >= options.MinimumSideLength &&
                    component.Height >= options.MinimumSideLength)
                {
                    yield return component;
                }
            }
        }
    }

    private static OcrRectangle MapToOriginal(
        Component component,
        OcrImage image,
        int tensorWidth,
        int tensorHeight,
        double unclipRatio)
    {
        double area = checked(component.Width * component.Height);
        double perimeter = 2d * (component.Width + component.Height);
        double expansion = perimeter <= 0 ? 0 : (area * unclipRatio) / perimeter;
        double imageLeft = Math.Clamp(
            (component.Left - expansion) * image.Width / tensorWidth,
            0,
            image.Width);
        double imageTop = Math.Clamp(
            (component.Top - expansion) * image.Height / tensorHeight,
            0,
            image.Height);
        double imageRight = Math.Clamp(
            (component.Right + 1 + expansion) * image.Width / tensorWidth,
            0,
            image.Width);
        double imageBottom = Math.Clamp(
            (component.Bottom + 1 + expansion) * image.Height / tensorHeight,
            0,
            image.Height);
        OcrPoint first = image.OriginalToImage.MapToOriginal(new OcrPoint(imageLeft, imageTop));
        OcrPoint second = image.OriginalToImage.MapToOriginal(new OcrPoint(imageRight, imageBottom));
        double canonicalWidth = image.CanonicalOriginalWidth ?? image.Width;
        double canonicalHeight = image.CanonicalOriginalHeight ?? image.Height;
        double left = Math.Clamp(Math.Min(first.X, second.X), 0, canonicalWidth);
        double top = Math.Clamp(Math.Min(first.Y, second.Y), 0, canonicalHeight);
        double right = Math.Clamp(Math.Max(first.X, second.X), 0, canonicalWidth);
        double bottom = Math.Clamp(Math.Max(first.Y, second.Y), 0, canonicalHeight);
        return new OcrRectangle(left, top, right - left, bottom - top);
    }

    private static PolygonScore ScorePolygon(
        float[] probabilities,
        int width,
        int height,
        Point2f[] box,
        float probabilityThreshold,
        CancellationToken cancellationToken)
    {
        int left = Math.Clamp((int)Math.Floor(box.Min(static point => point.X)), 0, width - 1);
        int top = Math.Clamp((int)Math.Floor(box.Min(static point => point.Y)), 0, height - 1);
        int right = Math.Clamp((int)Math.Ceiling(box.Max(static point => point.X)), 0, width - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(box.Max(static point => point.Y)), 0, height - 1);
        int maskWidth = checked(right - left + 1);
        int maskHeight = checked(bottom - top + 1);
        Point[] localBox = box
            .Select(point => new Point((int)(point.X - left), (int)(point.Y - top)))
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        using var mask = new Mat(maskHeight, maskWidth, MatType.CV_8UC1, Scalar.Black);
        Cv2.FillPoly(mask, [localBox], Scalar.White);
        cancellationToken.ThrowIfCancellationRequested();
        var maskPixels = new byte[checked(maskWidth * maskHeight)];
        Marshal.Copy(mask.Data, maskPixels, 0, maskPixels.Length);

        double confidenceSum = 0;
        int includedPixels = 0;
        int thresholdPixels = 0;
        for (var localY = 0; localY < maskHeight; localY++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var localX = 0; localX < maskWidth; localX++)
            {
                int localIndex = checked((localY * maskWidth) + localX);
                if (maskPixels[localIndex] == 0)
                {
                    continue;
                }

                float probability = probabilities[checked(((top + localY) * width) + left + localX)];
                confidenceSum += probability;
                includedPixels++;
                if (probability > probabilityThreshold)
                {
                    thresholdPixels++;
                }
            }
        }

        return includedPixels == 0
            ? new PolygonScore(0, 0)
            : new PolygonScore(
                Math.Clamp(confidenceSum / includedPixels, 0, 1),
                Math.Clamp(thresholdPixels / (double)includedPixels, 0, 1));
    }

    /// <summary>
    /// Produces the single convex round-join offset path used by pinned Paddle
    /// DB quad postprocessing. OpenCvSharp exposes no polygon-offset primitive,
    /// so arcs are tessellated with at most 0.25 pixel radial error, matching
    /// PyclipperOffset's default arc-tolerance bound. A degenerate or oversized
    /// path returns zero paths; callers reject any result count other than one.
    /// </summary>
    private static Point2f[][] UnclipRound(Point2f[] polygon, double unclipRatio)
    {
        Point2f[] ordered = OrderPolygon(polygon);
        if (ordered.Length < 3)
        {
            return [];
        }

        if (unclipRatio <= 0)
        {
            return [ordered];
        }

        double area = Math.Abs(SignedArea(ordered));
        double perimeter = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            Point2f first = ordered[index];
            Point2f second = ordered[(index + 1) % ordered.Length];
            perimeter += Distance(first, second);
        }

        if (area <= 0 || perimeter <= 0)
        {
            return [];
        }

        double distance = area * unclipRatio / perimeter;
        if (!double.IsFinite(distance) || distance <= 0)
        {
            return [];
        }

        const double arcTolerance = 0.25;
        double maximumStep = distance <= arcTolerance
            ? Math.PI / 2d
            : 2d * Math.Acos(Math.Clamp(1d - (arcTolerance / distance), -1d, 1d));
        if (!double.IsFinite(maximumStep) || maximumStep <= 0)
        {
            return [];
        }

        var expanded = new List<Point2f>();
        for (var index = 0; index < ordered.Length; index++)
        {
            Point2f previous = ordered[(index + ordered.Length - 1) % ordered.Length];
            Point2f vertex = ordered[index];
            Point2f next = ordered[(index + 1) % ordered.Length];
            if (!TryOutwardNormal(previous, vertex, out Point2f previousNormal) ||
                !TryOutwardNormal(vertex, next, out Point2f nextNormal))
            {
                return [];
            }

            double startAngle = Math.Atan2(previousNormal.Y, previousNormal.X);
            double endAngle = Math.Atan2(nextNormal.Y, nextNormal.X);
            while (endAngle <= startAngle)
            {
                endAngle += 2d * Math.PI;
            }

            double sweep = endAngle - startAngle;
            int segments = Math.Max(1, checked((int)Math.Ceiling(sweep / maximumStep)));
            for (var segment = 0; segment <= segments; segment++)
            {
                double angle = startAngle + (sweep * segment / segments);
                expanded.Add(new Point2f(
                    (float)(vertex.X + (Math.Cos(angle) * distance)),
                    (float)(vertex.Y + (Math.Sin(angle) * distance))));
            }

            if (expanded.Count > 4096)
            {
                return [];
            }
        }

        return expanded.Count < 3 ? [] : [OrderPolygon(expanded)];
    }

    private static bool TryOutwardNormal(Point2f first, Point2f second, out Point2f normal)
    {
        double dx = second.X - first.X;
        double dy = second.Y - first.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= 1e-9)
        {
            normal = default;
            return false;
        }

        normal = new Point2f((float)(dy / length), (float)(-dx / length));
        return true;
    }

    private static Point2f[] OrderPolygon(IEnumerable<Point2f> points)
    {
        Point2f[] source = points.ToArray();
        if (source.Length == 0)
        {
            return source;
        }

        double centerX = source.Average(static point => point.X);
        double centerY = source.Average(static point => point.Y);
        Point2f[] ordered = source
            .OrderBy(point => Math.Atan2(point.Y - centerY, point.X - centerX))
            .ThenBy(static point => point.Y)
            .ThenBy(static point => point.X)
            .ToArray();
        if (SignedArea(ordered) < 0)
        {
            Array.Reverse(ordered);
        }

        int start = Enumerable.Range(0, ordered.Length)
            .OrderBy(index => ordered[index].Y)
            .ThenBy(index => ordered[index].X)
            .First();
        return Enumerable.Range(0, ordered.Length)
            .Select(offset => ordered[(start + offset) % ordered.Length])
            .ToArray();
    }

    private static MiniBox GetMiniBox(IEnumerable<Point> points) =>
        GetMiniBox(points.Select(static point => new Point2f(point.X, point.Y)).ToArray());

    private static MiniBox GetMiniBox(Point2f[] points)
    {
        RotatedRect rectangle = Cv2.MinAreaRect(points);
        Point2f[] box = rectangle.Points()
            .OrderBy(static point => point.X)
            .ToArray();
        int firstIndex;
        int fourthIndex;
        if (box[1].Y > box[0].Y)
        {
            firstIndex = 0;
            fourthIndex = 1;
        }
        else
        {
            firstIndex = 1;
            fourthIndex = 0;
        }

        int secondIndex;
        int thirdIndex;
        if (box[3].Y > box[2].Y)
        {
            secondIndex = 2;
            thirdIndex = 3;
        }
        else
        {
            secondIndex = 3;
            thirdIndex = 2;
        }

        return new MiniBox(
            [box[firstIndex], box[secondIndex], box[thirdIndex], box[fourthIndex]],
            Math.Min(rectangle.Size.Width, rectangle.Size.Height));
    }

    private static OcrPolygon MapPolygonToOriginal(
        Point2f[] tensorPolygon,
        OcrImage image,
        int tensorWidth,
        int tensorHeight)
    {
        int canonicalWidth = image.CanonicalOriginalWidth ?? image.Width;
        int canonicalHeight = image.CanonicalOriginalHeight ?? image.Height;
        OcrPoint[] mapped = tensorPolygon
            .Select(point => new OcrPoint(
                Math.Clamp(
                    Math.Round(point.X * image.Width / tensorWidth, MidpointRounding.ToEven),
                    0,
                    image.Width),
                Math.Clamp(
                    Math.Round(point.Y * image.Height / tensorHeight, MidpointRounding.ToEven),
                    0,
                    image.Height)))
            .Select(imagePoint => image.OriginalToImage.MapToOriginal(imagePoint))
            .Select(point => new OcrPoint(
                Math.Clamp(point.X, 0, canonicalWidth),
                Math.Clamp(point.Y, 0, canonicalHeight)))
            .ToArray();
        return new OcrPolygon(mapped);
    }

    private static double OrientationDegrees(OcrPoint[] polygon)
    {
        OcrPoint first = polygon[0];
        OcrPoint second = polygon[1];
        OcrPoint third = polygon[2];
        double firstLength = Distance(first, second);
        double secondLength = Distance(second, third);
        OcrPoint start = firstLength >= secondLength ? first : second;
        OcrPoint end = firstLength >= secondLength ? second : third;
        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X) * 180d / Math.PI;
        while (angle >= 90)
        {
            angle -= 180;
        }

        while (angle < -90)
        {
            angle += 180;
        }

        return Math.Abs(angle) <= 1e-9 ? 0 : angle;
    }

    private static double SignedArea(Point2f[] polygon)
    {
        double area = 0;
        for (var index = 0; index < polygon.Length; index++)
        {
            Point2f first = polygon[index];
            Point2f second = polygon[(index + 1) % polygon.Length];
            area += (first.X * second.Y) - (second.X * first.Y);
        }

        return area / 2d;
    }

    private static double Distance(Point2f first, Point2f second)
    {
        double x = second.X - first.X;
        double y = second.Y - first.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    private static double Distance(OcrPoint first, OcrPoint second)
    {
        double x = second.X - first.X;
        double y = second.Y - first.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    private static float[] CreateTensor(
        OcrImage image,
        int targetWidth,
        int targetHeight,
        LocalOnnxTextRegionDetectorOptions options,
        CancellationToken cancellationToken)
    {
        if (options.PostprocessAlgorithm == OcrDetectionPostprocessAlgorithm.DbPostprocessV1 &&
            options.InputColorMode == OcrTensorColorMode.Bgr &&
            options.InputChannels == 3)
        {
            return CreateOfficialDbTensor(
                image,
                targetWidth,
                targetHeight,
                options,
                cancellationToken);
        }

        int pixelsPerChannel = checked(targetWidth * targetHeight);
        var values = new float[checked(pixelsPerChannel * options.InputChannels)];
        (int sourceWidth, int sourceHeight) = ResizeSourceDimensions(image, options);
        for (var targetY = 0; targetY < targetHeight; targetY++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sourceY = ((targetY + 0.5) * sourceHeight / targetHeight) - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1);
            int y1 = Math.Min(y0 + 1, sourceHeight - 1);
            double yWeight = Math.Clamp(sourceY - Math.Floor(sourceY), 0, 1);
            for (var targetX = 0; targetX < targetWidth; targetX++)
            {
                double sourceX = ((targetX + 0.5) * sourceWidth / targetWidth) - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1);
                int x1 = Math.Min(x0 + 1, sourceWidth - 1);
                double xWeight = Math.Clamp(sourceX - Math.Floor(sourceX), 0, 1);
                int pixelIndex = checked((targetY * targetWidth) + targetX);
                for (var channel = 0; channel < options.InputChannels; channel++)
                {
                    double top = SourceValue(image, x0, y0, channel, options.InputColorMode) * (1 - xWeight) +
                        SourceValue(image, x1, y0, channel, options.InputColorMode) * xWeight;
                    double bottom = SourceValue(image, x0, y1, channel, options.InputColorMode) * (1 - xWeight) +
                        SourceValue(image, x1, y1, channel, options.InputColorMode) * xWeight;
                    float sample = (float)((top * (1 - yWeight) + bottom * yWeight) / 255d);
                    int destination = options.InputLayout == OcrTensorLayout.ChannelsFirst
                        ? checked((channel * pixelsPerChannel) + pixelIndex)
                        : checked((pixelIndex * options.InputChannels) + channel);
                    values[destination] =
                        (sample - options.ChannelMeans[channel]) * options.ChannelScales[channel];
                }
            }
        }

        return values;
    }

    private static float[] CreateOfficialDbTensor(
        OcrImage image,
        int targetWidth,
        int targetHeight,
        LocalOnnxTextRegionDetectorOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (int sourceWidth, int sourceHeight) = ResizeSourceDimensions(image, options);
        var sourcePixels = new byte[checked(sourceWidth * sourceHeight * 3)];
        ReadOnlySpan<byte> inputPixels = image.BgrPixels!.Pixels.Span;
        for (var y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inputPixels.Slice(y * image.BgrPixels.Stride, image.Width * 3)
                .CopyTo(sourcePixels.AsSpan(y * sourceWidth * 3, image.Width * 3));
        }

        using var source = new Mat(sourceHeight, sourceWidth, MatType.CV_8UC3);
        Marshal.Copy(sourcePixels, 0, source.Data, sourcePixels.Length);
        using var resized = new Mat();
        cancellationToken.ThrowIfCancellationRequested();
        Cv2.Resize(
            source,
            resized,
            new Size(targetWidth, targetHeight),
            interpolation: InterpolationFlags.Linear);
        cancellationToken.ThrowIfCancellationRequested();

        var resizedPixels = new byte[checked(targetWidth * targetHeight * 3)];
        Marshal.Copy(resized.Data, resizedPixels, 0, resizedPixels.Length);
        int pixelsPerChannel = checked(targetWidth * targetHeight);
        var values = new float[checked(pixelsPerChannel * options.InputChannels)];
        for (var y = 0; y < targetHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < targetWidth; x++)
            {
                int sourceIndex = checked(((y * targetWidth) + x) * 3);
                int pixelIndex = checked((y * targetWidth) + x);
                for (var channel = 0; channel < options.InputChannels; channel++)
                {
                    float sample = resizedPixels[sourceIndex + channel] / 255f;
                    int destination = options.InputLayout == OcrTensorLayout.ChannelsFirst
                        ? checked((channel * pixelsPerChannel) + pixelIndex)
                        : checked((pixelIndex * options.InputChannels) + channel);
                    values[destination] =
                        (sample - options.ChannelMeans[channel]) * options.ChannelScales[channel];
                }
            }
        }

        return values;
    }

    private static byte SourceValue(
        OcrImage image,
        int x,
        int y,
        int channel,
        OcrTensorColorMode colorMode)
    {
        if (x >= image.Width || y >= image.Height)
        {
            return 0;
        }

        return colorMode == OcrTensorColorMode.Bgr
            ? image.BgrPixels!.Pixels.Span[checked(
                (y * image.BgrPixels.Stride) + (x * 3) + channel)]
            : image.Pixels.Span[checked((y * image.Stride) + x)];
    }

    private static (int Width, int Height) TensorDimensions(
        OcrImage image,
        LocalOnnxTextRegionDetectorOptions options)
    {
        if (options.PostprocessAlgorithm == OcrDetectionPostprocessAlgorithm.DbPostprocessV1)
        {
            (int sourceWidth, int sourceHeight) = ResizeSourceDimensions(image, options);
            double ratio = options.MaximumSideLength / (double)Math.Max(sourceWidth, sourceHeight);
            int resizedWidth = checked((int)(sourceWidth * ratio));
            int resizedHeight = checked((int)(sourceHeight * ratio));
            return (
                CeilingAligned(resizedWidth, options.DimensionMultiple),
                CeilingAligned(resizedHeight, options.DimensionMultiple));
        }

        int alignedMaximum = options.MaximumSideLength / options.DimensionMultiple * options.DimensionMultiple;
        double scale = Math.Min(1d, alignedMaximum / (double)Math.Max(image.Width, image.Height));
        int width = RoundAligned(image.Width * scale, options.DimensionMultiple, alignedMaximum);
        int height = RoundAligned(image.Height * scale, options.DimensionMultiple, alignedMaximum);
        return (width, height);
    }

    private static (int Width, int Height) ResizeSourceDimensions(
        OcrImage image,
        LocalOnnxTextRegionDetectorOptions options) =>
        options.PostprocessAlgorithm == OcrDetectionPostprocessAlgorithm.DbPostprocessV1 &&
        checked(image.Width + image.Height) < 64
            ? (Math.Max(32, image.Width), Math.Max(32, image.Height))
            : (image.Width, image.Height);

    private static int CeilingAligned(int value, int multiple)
    {
        if (value <= 0)
        {
            throw new InvalidDataException(
                "OCR DB resize produced a zero dimension for the source aspect ratio.");
        }

        return checked(((value + multiple - 1) / multiple) * multiple);
    }

    private static int RoundAligned(double value, int multiple, int maximum) =>
        Math.Clamp(
            checked((int)Math.Round(
                value / multiple,
                MidpointRounding.AwayFromZero) * multiple),
            multiple,
            maximum);

    private static float ToProbability(float value, OcrDetectionOutputActivation activation)
    {
        if (!float.IsFinite(value))
        {
            throw new InvalidDataException("OCR detection output contains a non-finite value.");
        }

        return activation switch
        {
            OcrDetectionOutputActivation.Probability when value is >= 0 and <= 1 => value,
            OcrDetectionOutputActivation.Probability => throw new InvalidDataException(
                "OCR detection probability output must remain within [0,1]."),
            OcrDetectionOutputActivation.ProbabilityWithParityTolerance
                when value is >= -ProbabilityParityTolerance and <= 1f + ProbabilityParityTolerance =>
                Math.Clamp(value, 0f, 1f),
            OcrDetectionOutputActivation.ProbabilityWithParityTolerance => throw new InvalidDataException(
                "OCR detection probability output exceeded the fixed 1e-5 parity tolerance around [0,1]."),
            OcrDetectionOutputActivation.SigmoidLogit =>
                (float)(1d / (1d + Math.Exp(-Math.Clamp(value, -80f, 80f)))),
            _ => throw new ArgumentOutOfRangeException(nameof(activation)),
        };
    }

    private static void ValidateImage(
        OcrImage image,
        LocalOnnxTextRegionDetectorOptions options)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Stride < image.Width ||
            image.Pixels.Length < checked(image.Stride * image.Height) ||
            !image.OriginalToImage.IsInvertible ||
            !string.Equals(image.CoordinateSpace, OcrContract.CoordinateSpace, StringComparison.Ordinal) ||
            image.CanonicalOriginalWidth is <= 0 || image.CanonicalOriginalHeight is <= 0 ||
            (options.InputColorMode == OcrTensorColorMode.Bgr && !HasValidBgrPlane(image)))
        {
            throw new ArgumentException("OCR detection image is invalid.", nameof(image));
        }
    }

    private static bool HasValidBgrPlane(OcrImage image) =>
        image.BgrPixels is { } bgr &&
        bgr.Stride >= checked(image.Width * 3) &&
        bgr.Pixels.Length == checked(bgr.Stride * image.Height);

    public static void ValidateOptions(LocalOnnxTextRegionDetectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Model.Validate();
        bool invalidProviders = options.AllowedProviders is not null &&
            (options.AllowedProviders.Count == 0 ||
             options.AllowedProviders.Any(static provider =>
                 provider is not (InferenceProvider.Cpu or InferenceProvider.DirectMl)) ||
             !options.AllowedProviders.Contains(InferenceProvider.Cpu) ||
             options.AllowedProviders.Distinct().Count() != options.AllowedProviders.Count);
        if (options.MaximumSideLength is < 32 or > 4096 ||
            options.DimensionMultiple is < 1 or > 128 ||
            options.DimensionMultiple > options.MaximumSideLength ||
            options.InputChannels is < 1 or > 4 || !Enum.IsDefined(options.InputLayout) ||
            !Enum.IsDefined(options.InputColorMode) ||
            (options.InputColorMode == OcrTensorColorMode.Bgr && options.InputChannels != 3) ||
            options.ChannelMeans.Count != options.InputChannels ||
            options.ChannelScales.Count != options.InputChannels ||
            options.ChannelMeans.Any(static value => !float.IsFinite(value)) ||
            options.ChannelScales.Any(static value => !float.IsFinite(value) || value == 0) ||
            string.IsNullOrWhiteSpace(options.InputName) || string.IsNullOrWhiteSpace(options.OutputName) ||
            string.IsNullOrWhiteSpace(options.StageVersion) || !Enum.IsDefined(options.OutputActivation) ||
            !Enum.IsDefined(options.PostprocessAlgorithm) || !Enum.IsDefined(options.DbScoreMode) ||
            options.ProbabilityThreshold is < 0 or > 1 ||
            options.BoxConfidenceThreshold is < 0 or > 1 ||
            !double.IsFinite(options.UnclipRatio) || options.UnclipRatio is < 0 or > 10 ||
            options.MinimumComponentArea is < 1 or > 16_777_216 ||
            options.MinimumSideLength is < 1 or > 4096 ||
            options.MaximumRegions is < 1 or > 10_000 ||
            options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(5) || invalidProviders)
        {
            throw new ArgumentException("Local ONNX OCR detector options are invalid.", nameof(options));
        }
    }

    private static string CreateConfigurationFingerprint(LocalOnnxTextRegionDetectorOptions options)
    {
        string material = string.Join('|',
            options.Model.Sha256.ToLowerInvariant(),
            options.MaximumSideLength.ToString(CultureInfo.InvariantCulture),
            options.DimensionMultiple.ToString(CultureInfo.InvariantCulture),
            TensorResizeAlgorithm(options),
            options.InputChannels.ToString(CultureInfo.InvariantCulture),
            options.InputLayout,
            options.InputColorMode,
            string.Join(',', options.ChannelMeans.Select(static value => value.ToString("R", CultureInfo.InvariantCulture))),
            string.Join(',', options.ChannelScales.Select(static value => value.ToString("R", CultureInfo.InvariantCulture))),
            options.InputName,
            options.OutputName,
            options.StageVersion,
            options.OutputActivation,
            options.OutputActivation == OcrDetectionOutputActivation.ProbabilityWithParityTolerance
                ? ProbabilityParityTolerance.ToString("R", CultureInfo.InvariantCulture)
                : "not-applicable",
            options.PostprocessAlgorithm,
            options.PostprocessAlgorithm == OcrDetectionPostprocessAlgorithm.DbPostprocessV1
                ? options.DbScoreMode
                : "not-applicable",
            options.ProbabilityThreshold.ToString("R", CultureInfo.InvariantCulture),
            options.BoxConfidenceThreshold.ToString("R", CultureInfo.InvariantCulture),
            options.UnclipRatio.ToString("R", CultureInfo.InvariantCulture),
            options.PostprocessAlgorithm == OcrDetectionPostprocessAlgorithm.DenseProbabilityComponentsV1
                ? options.MinimumComponentArea.ToString(CultureInfo.InvariantCulture)
                : "not-applicable",
            options.MinimumSideLength.ToString(CultureInfo.InvariantCulture),
            options.MaximumRegions.ToString(CultureInfo.InvariantCulture),
            ProviderFingerprint(options.AllowedProviders));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string TensorResizeAlgorithm(LocalOnnxTextRegionDetectorOptions options) =>
        options.PostprocessAlgorithm == OcrDetectionPostprocessAlgorithm.DbPostprocessV1 &&
        options.InputColorMode == OcrTensorColorMode.Bgr &&
        options.InputChannels == 3
            ? OfficialDbResizeAlgorithm
            : LegacyResizeAlgorithm;

    private static string DeterministicRegionId(
        string modelSha256,
        OcrDetectionPostprocessAlgorithm algorithm,
        OcrPolygon polygon)
    {
        string material = string.Join(':',
            modelSha256.ToLowerInvariant(),
            algorithm,
            string.Join(';', polygon.Points.Select(static point =>
                FormattableString.Invariant($"{point.X:R},{point.Y:R}"))));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static string TransformFingerprint(OcrFrameTransform transform) =>
        FormattableString.Invariant(
            $"{transform.ScaleX:R},{transform.ScaleY:R},{transform.OffsetX:R},{transform.OffsetY:R}");

    private static string ProviderFingerprint(IReadOnlyList<InferenceProvider>? providers) =>
        providers is null
            ? "policy-default"
            : string.Join(',', providers
                .Distinct()
                .OrderBy(static provider => provider)
                .Select(static provider => provider.ToString()));

    private sealed record Component(
        int Left,
        int Top,
        int Right,
        int Bottom,
        int PixelCount,
        double Confidence)
    {
        public int Width => Right - Left + 1;

        public int Height => Bottom - Top + 1;
    }

    private sealed record DbCandidate(
        OcrPolygon Polygon,
        double OrientationDegrees,
        double Confidence,
        double InkDensity);

    private readonly record struct PolygonScore(double Confidence, double InkDensity);

    private readonly record struct MiniBox(Point2f[] Points, double ShortSide);
}
