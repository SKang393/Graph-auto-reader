// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GraphReader.Inference;

namespace GraphReader.Ocr;

public sealed record LocalOnnxTiledProbabilityTextRegionDetectorOptions(ModelIdentity Model)
{
    public string StageVersion { get; init; } = "v38-source-tiled-probability-v1";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<InferenceProvider>? AllowedProviders { get; init; }

    public bool BypassCache { get; init; }
}

/// <summary>
/// Executes the retained V38/V37 Gray8 tiled probability-map contract. The
/// adapter contains no weights, approval, or production-selection policy.
/// </summary>
public sealed class LocalOnnxTiledProbabilityTextRegionDetector : IDualInputTextRegionDetector
{
    public const int TileSize = 256;

    public const int TileOverlap = 64;

    public const int TileStep = TileSize - TileOverlap;

    public const float ProbabilityThreshold = 0.4f;

    public const int MinimumComponentArea = 8;

    public const int MinimumSideLength = 2;

    public const int MaximumTileCount = 256;

    public const string InputName = "source_tiles";

    public const string OutputName = "text_logits";

    private const string Algorithm = "v38-gray8-tiled-probability-components-v1";

    private readonly InferenceRuntime runtime;
    private readonly LocalOnnxTiledProbabilityTextRegionDetectorOptions options;
    private readonly string configurationFingerprint;

    public LocalOnnxTiledProbabilityTextRegionDetector(
        InferenceRuntime runtime,
        LocalOnnxTiledProbabilityTextRegionDetectorOptions options)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(options);
        options.Model.Validate();
        ValidateOptions(options);
        this.options = options with
        {
            AllowedProviders = options.AllowedProviders is null
                ? null
                : Array.AsReadOnly(options.AllowedProviders.ToArray()),
        };
        configurationFingerprint = CreateConfigurationFingerprint(this.options);
    }

    public string ConfigurationFingerprint => configurationFingerprint;

    public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateImage(image, requireOriginalSource: true);
        return DetectCoreAsync(image, cancellationToken);
    }

    public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage originalImage,
        OcrImage detectorImage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(originalImage);
        ArgumentNullException.ThrowIfNull(detectorImage);
        ValidateImage(originalImage, requireOriginalSource: true);
        ValidateImage(detectorImage, requireOriginalSource: false);
        if (originalImage.Width != detectorImage.Width ||
            originalImage.Height != detectorImage.Height ||
            originalImage.CanonicalOriginalWidth != detectorImage.CanonicalOriginalWidth ||
            originalImage.CanonicalOriginalHeight != detectorImage.CanonicalOriginalHeight ||
            originalImage.OriginalToImage != detectorImage.OriginalToImage ||
            !string.Equals(
                originalImage.CoordinateSpace,
                detectorImage.CoordinateSpace,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The detector derivative is not coordinate-aligned with the immutable original OCR image.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return DetectCoreAsync(originalImage, cancellationToken);
    }

    private async ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectCoreAsync(
        OcrImage image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateImage(image, requireOriginalSource: false);
        cancellationToken.ThrowIfCancellationRequested();

        Tile[] tiles = BuildTiles(image.Width, image.Height);
        if (tiles.Length > MaximumTileCount)
        {
            throw new InvalidDataException(
                $"Tiled OCR requires {tiles.Length} tiles; the fixed safety limit is {MaximumTileCount}.");
        }

        float[] tensor = BuildTensor(image, tiles, cancellationToken);
        string inputSha256 = HashGray8(image);
        var request = new InferenceRequest(
            options.Model,
            new InferenceInput(
                tensor,
                [tiles.LongLength, 1, TileSize, TileSize],
                InputName,
                OutputName),
            new StageCacheMaterial(
                inputSha256,
                FormattableString.Invariant($"0,0,{image.Width},{image.Height}"),
                TransformFingerprint(image.OriginalToImage),
                "ocr_detection_tiled_probability",
                options.StageVersion,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["algorithm"] = Algorithm,
                    ["input_color_mode"] = "Gray8",
                    ["input_layout"] = "NCHW",
                    ["input_name"] = InputName,
                    ["output_name"] = OutputName,
                    ["output_activation"] = "SigmoidLogit",
                    ["tile_size"] = TileSize,
                    ["tile_overlap"] = TileOverlap,
                    ["tile_step"] = TileStep,
                    ["tile_count"] = tiles.Length,
                    ["tile_padding"] = "white-255-top-left-valid",
                    ["normalization"] = "1-gray/255-float32",
                    ["overlap_merge"] = "valid-region-float32-mean",
                    ["probability_threshold"] = ProbabilityThreshold,
                    ["morphology"] = "global-binary-close-3x3-once",
                    ["connectivity"] = 8,
                    ["minimum_component_area"] = MinimumComponentArea,
                    ["minimum_side_length"] = MinimumSideLength,
                    ["maximum_tile_count"] = MaximumTileCount,
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
                ? "The tiled OCR runtime returned no execution evidence."
                : $"{response.Error.Code}: {response.Error.TechnicalMessage}";
            throw new InvalidOperationException(diagnostic);
        }

        if (options.AllowedProviders is not null &&
            !options.AllowedProviders.Contains(response.Execution.Provider))
        {
            throw new InvalidDataException(
                $"Tiled OCR executed with undeclared provider '{response.Execution.Provider}'.");
        }

        int expectedOutputCount = checked(tiles.Length * TileSize * TileSize);
        if (response.Execution.Output.Count != expectedOutputCount)
        {
            throw new InvalidDataException(
                $"Tiled OCR output contained {response.Execution.Output.Count} values; " +
                $"{expectedOutputCount} were required.");
        }

        float[] probabilities = ReconstructProbabilityMap(
            response.Execution.Output,
            tiles,
            image.Width,
            image.Height,
            cancellationToken);
        byte[] closed = CloseThresholdMask(probabilities, image.Width, image.Height, cancellationToken);
        return BuildRegions(
            closed,
            probabilities,
            image,
            options.Model.Sha256,
            cancellationToken);
    }

    private static Tile[] BuildTiles(int imageWidth, int imageHeight)
    {
        int[] horizontalStarts = TileStarts(imageWidth);
        int[] verticalStarts = TileStarts(imageHeight);
        int tileCount = checked(horizontalStarts.Length * verticalStarts.Length);
        if (tileCount > MaximumTileCount)
        {
            throw new InvalidDataException(
                $"Tiled OCR requires {tileCount} tiles; the fixed safety limit is {MaximumTileCount}.");
        }

        var tiles = new Tile[tileCount];
        var index = 0;
        foreach (int top in verticalStarts)
        {
            foreach (int left in horizontalStarts)
            {
                tiles[index++] = new Tile(
                    left,
                    top,
                    Math.Min(TileSize, imageWidth - left),
                    Math.Min(TileSize, imageHeight - top));
            }
        }

        return tiles;
    }

    private static int[] TileStarts(int length)
    {
        if (length <= TileSize)
        {
            return [0];
        }

        var starts = new List<int>();
        int finalStart = length - TileSize;
        for (var start = 0; start <= finalStart; start += TileStep)
        {
            starts.Add(start);
        }

        if (starts[^1] != finalStart)
        {
            starts.Add(finalStart);
        }

        return starts.ToArray();
    }

    private static float[] BuildTensor(
        OcrImage image,
        IReadOnlyList<Tile> tiles,
        CancellationToken cancellationToken)
    {
        int tilePixels = TileSize * TileSize;
        var tensor = new float[checked(tiles.Count * tilePixels)];
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        for (var tileIndex = 0; tileIndex < tiles.Count; tileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Tile tile = tiles[tileIndex];
            int tileOffset = tileIndex * tilePixels;
            for (var y = 0; y < tile.ValidHeight; y++)
            {
                int sourceOffset = checked(((tile.Top + y) * image.Stride) + tile.Left);
                int targetOffset = checked(tileOffset + (y * TileSize));
                for (var x = 0; x < tile.ValidWidth; x++)
                {
                    tensor[targetOffset + x] = 1f - (pixels[sourceOffset + x] / 255f);
                }
            }
        }

        return tensor;
    }

    private static float[] ReconstructProbabilityMap(
        IReadOnlyList<float> logits,
        IReadOnlyList<Tile> tiles,
        int imageWidth,
        int imageHeight,
        CancellationToken cancellationToken)
    {
        int sourcePixelCount = checked(imageWidth * imageHeight);
        var scores = new float[sourcePixelCount];
        var counts = new float[sourcePixelCount];
        int tilePixels = TileSize * TileSize;
        for (var tileIndex = 0; tileIndex < tiles.Count; tileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Tile tile = tiles[tileIndex];
            int tileOffset = tileIndex * tilePixels;
            for (var y = 0; y < tile.ValidHeight; y++)
            {
                int sourceOffset = checked(((tile.Top + y) * imageWidth) + tile.Left);
                int outputOffset = checked(tileOffset + (y * TileSize));
                for (var x = 0; x < tile.ValidWidth; x++)
                {
                    float logit = logits[outputOffset + x];
                    if (!float.IsFinite(logit))
                    {
                        throw new InvalidDataException("Tiled OCR output contains a non-finite logit.");
                    }

                    float probability = 1f / (1f + MathF.Exp(-logit));
                    scores[sourceOffset + x] += probability;
                    counts[sourceOffset + x] += 1f;
                }
            }
        }

        for (var index = 0; index < sourcePixelCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (counts[index] < 1f)
            {
                throw new InvalidDataException("Tiled OCR did not cover every source pixel.");
            }

            scores[index] /= counts[index];
        }

        return scores;
    }

    private static byte[] CloseThresholdMask(
        float[] probabilities,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var thresholded = new byte[probabilities.Length];
        for (var index = 0; index < probabilities.Length; index++)
        {
            thresholded[index] = probabilities[index] >= ProbabilityThreshold ? (byte)1 : (byte)0;
        }

        var dilated = new byte[thresholded.Length];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                bool foreground = false;
                for (int neighborY = Math.Max(0, y - 1);
                     neighborY <= Math.Min(height - 1, y + 1) && !foreground;
                     neighborY++)
                {
                    for (int neighborX = Math.Max(0, x - 1);
                         neighborX <= Math.Min(width - 1, x + 1);
                         neighborX++)
                    {
                        if (thresholded[(neighborY * width) + neighborX] != 0)
                        {
                            foreground = true;
                            break;
                        }
                    }
                }

                dilated[(y * width) + x] = foreground ? (byte)1 : (byte)0;
            }
        }

        var closed = new byte[thresholded.Length];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                bool foreground = true;
                for (int neighborY = Math.Max(0, y - 1);
                     neighborY <= Math.Min(height - 1, y + 1) && foreground;
                     neighborY++)
                {
                    for (int neighborX = Math.Max(0, x - 1);
                         neighborX <= Math.Min(width - 1, x + 1);
                         neighborX++)
                    {
                        if (dilated[(neighborY * width) + neighborX] == 0)
                        {
                            foreground = false;
                            break;
                        }
                    }
                }

                closed[(y * width) + x] = foreground ? (byte)1 : (byte)0;
            }
        }

        return closed;
    }

    private static ReadOnlyCollection<OcrDetectedRegion> BuildRegions(
        byte[] mask,
        float[] probabilities,
        OcrImage image,
        string modelSha256,
        CancellationToken cancellationToken)
    {
        var visited = new byte[mask.Length];
        var components = new List<Component>();
        var queue = new Queue<int>();
        for (var y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < image.Width; x++)
            {
                int seed = (y * image.Width) + x;
                if (mask[seed] == 0 || visited[seed] != 0)
                {
                    continue;
                }

                visited[seed] = 1;
                queue.Enqueue(seed);
                int left = x;
                int top = y;
                int right = x;
                int bottom = y;
                int area = 0;
                double confidenceSum = 0;
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    int currentY = current / image.Width;
                    int currentX = current - (currentY * image.Width);
                    area++;
                    confidenceSum += probabilities[current];
                    left = Math.Min(left, currentX);
                    top = Math.Min(top, currentY);
                    right = Math.Max(right, currentX);
                    bottom = Math.Max(bottom, currentY);
                    for (int neighborY = Math.Max(0, currentY - 1);
                         neighborY <= Math.Min(image.Height - 1, currentY + 1);
                         neighborY++)
                    {
                        for (int neighborX = Math.Max(0, currentX - 1);
                             neighborX <= Math.Min(image.Width - 1, currentX + 1);
                             neighborX++)
                        {
                            int neighbor = (neighborY * image.Width) + neighborX;
                            if (mask[neighbor] != 0 && visited[neighbor] == 0)
                            {
                                visited[neighbor] = 1;
                                queue.Enqueue(neighbor);
                            }
                        }
                    }
                }

                int componentWidth = right - left + 1;
                int componentHeight = bottom - top + 1;
                if (area >= MinimumComponentArea &&
                    componentWidth >= MinimumSideLength &&
                    componentHeight >= MinimumSideLength)
                {
                    components.Add(new Component(
                        left,
                        top,
                        componentWidth,
                        componentHeight,
                        area,
                        Math.Clamp(confidenceSum / area, 0, 1)));
                }
            }
        }

        var regions = new List<OcrDetectedRegion>(components.Count);
        foreach (Component component in components
                     .OrderBy(static component => component.Top)
                     .ThenBy(static component => component.Left)
                     .ThenBy(static component => component.Bottom)
                     .ThenBy(static component => component.Right))
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle rectangle = MapToOriginal(component, image);
            if (!rectangle.IsValid)
            {
                continue;
            }

            OcrPolygon polygon = OcrPolygon.FromRectangle(rectangle);
            double density = component.Area / (double)(component.Width * component.Height);
            regions.Add(new OcrDetectedRegion(
                DeterministicRegionId(modelSha256, polygon),
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
                    Reasons: Array.AsReadOnly(["onnx_tiled_probability_text"]))));
        }

        return regions.AsReadOnly();
    }

    private static OcrRectangle MapToOriginal(Component component, OcrImage image)
    {
        OcrPoint first = image.OriginalToImage.MapToOriginal(
            new OcrPoint(component.Left, component.Top));
        OcrPoint second = image.OriginalToImage.MapToOriginal(
            new OcrPoint(component.Right, component.Bottom));
        double maximumX = image.CanonicalOriginalWidth!.Value;
        double maximumY = image.CanonicalOriginalHeight!.Value;
        double left = Math.Clamp(Math.Min(first.X, second.X), 0, maximumX);
        double top = Math.Clamp(Math.Min(first.Y, second.Y), 0, maximumY);
        double right = Math.Clamp(Math.Max(first.X, second.X), 0, maximumX);
        double bottom = Math.Clamp(Math.Max(first.Y, second.Y), 0, maximumY);
        return new OcrRectangle(left, top, right - left, bottom - top);
    }

    private static string DeterministicRegionId(string modelSha256, OcrPolygon polygon)
    {
        string material = string.Join(':',
            modelSha256.ToLowerInvariant(),
            Algorithm,
            string.Join(';', polygon.Points.Select(static point =>
                FormattableString.Invariant($"{point.X:R},{point.Y:R}"))));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static void ValidateImage(OcrImage image, bool requireOriginalSource)
    {
        bool multiplicationOverflow = image.Width > 0 && image.Height > int.MaxValue / image.Width;
        if (image.Width <= 0 || image.Height <= 0 || multiplicationOverflow ||
            image.Stride < image.Width || image.Height > int.MaxValue / image.Stride ||
            image.Pixels.Length != image.Stride * image.Height ||
            !image.OriginalToImage.IsInvertible ||
            image.OriginalToImage.ScaleX <= 0 || image.OriginalToImage.ScaleY <= 0 ||
            !string.Equals(image.CoordinateSpace, OcrContract.CoordinateSpace, StringComparison.Ordinal) ||
            image.CanonicalOriginalWidth is <= 0 || image.CanonicalOriginalHeight is <= 0 ||
            (requireOriginalSource && image.SourceImage != OcrSourceImage.Original))
        {
            throw new ArgumentException("Tiled OCR image is invalid.", nameof(image));
        }
    }

    private static void ValidateOptions(LocalOnnxTiledProbabilityTextRegionDetectorOptions options)
    {
        bool invalidProviders = options.AllowedProviders is not null &&
            (options.AllowedProviders.Count == 0 ||
             options.AllowedProviders.Any(static provider =>
                 provider is not (InferenceProvider.Cpu or InferenceProvider.DirectMl)) ||
             !options.AllowedProviders.Contains(InferenceProvider.Cpu) ||
             options.AllowedProviders.Distinct().Count() != options.AllowedProviders.Count);
        if (string.IsNullOrWhiteSpace(options.StageVersion) ||
            options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(5) ||
            invalidProviders)
        {
            throw new ArgumentException("Tiled OCR detector options are invalid.", nameof(options));
        }
    }

    private static string HashGray8(OcrImage image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        for (var y = 0; y < image.Height; y++)
        {
            hash.AppendData(pixels.Slice(y * image.Stride, image.Width));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string CreateConfigurationFingerprint(
        LocalOnnxTiledProbabilityTextRegionDetectorOptions options)
    {
        string material = string.Join('|',
            options.Model.Sha256.ToLowerInvariant(),
            options.StageVersion,
            Algorithm,
            TileSize.ToString(CultureInfo.InvariantCulture),
            TileOverlap.ToString(CultureInfo.InvariantCulture),
            ProbabilityThreshold.ToString("R", CultureInfo.InvariantCulture),
            MinimumComponentArea.ToString(CultureInfo.InvariantCulture),
            MinimumSideLength.ToString(CultureInfo.InvariantCulture),
            MaximumTileCount.ToString(CultureInfo.InvariantCulture),
            ProviderFingerprint(options.AllowedProviders));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
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

    private readonly record struct Tile(
        int Left,
        int Top,
        int ValidWidth,
        int ValidHeight);

    private readonly record struct Component(
        int Left,
        int Top,
        int Width,
        int Height,
        int Area,
        double Confidence)
    {
        public int Right => Left + Width;

        public int Bottom => Top + Height;
    }
}
