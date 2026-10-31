// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;
using System.Collections.ObjectModel;
using GraphReader.Inference;

namespace GraphReader.Ocr;

public sealed record LocalOnnxComponentTextRecognizerOptions(
    ModelIdentity Model,
    string Alphabet)
{
    public int CanvasWidth { get; init; } = 128;

    public int CanvasHeight { get; init; } = 32;

    public int GlyphWidth { get; init; } = 20;

    public int GlyphHeight { get; init; } = 24;

    public int GeometryFeatureCount { get; init; } = 6;

    public int MaximumGlyphs { get; init; } = 8;

    public int RejectClassIndex { get; init; } = 13;

    public float ConfidenceThreshold { get; init; } = 0.65f;

    public float StructuralRejectMinimumHeightRatio { get; init; } = 0.75f;

    public string InputName { get; init; } = "glyphs";

    public string OutputName { get; init; } = "logits";

    public string StageVersion { get; init; } = "0.1.0";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<InferenceProvider>? AllowedProviders { get; init; }

    public bool BypassCache { get; init; }
}

/// <summary>
/// Executes the project-trained graph-numeric component ensemble over a fixed
/// grayscale text crop. Component extraction and geometry encoding are part of
/// the checksum-bound runtime contract rather than model-independent OCR.
/// </summary>
public sealed partial class LocalOnnxComponentTextRecognizer : ITextRecognizer
{
    private readonly InferenceRuntime runtime;
    private readonly LocalOnnxComponentTextRecognizerOptions options;
    private readonly string configurationFingerprint;

    public LocalOnnxComponentTextRecognizer(
        InferenceRuntime runtime,
        LocalOnnxComponentTextRecognizerOptions options)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options with
        {
            AllowedProviders = options.AllowedProviders is null
                ? null
                : Array.AsReadOnly(options.AllowedProviders.ToArray()),
        };
        ValidateOptions(this.options);
        configurationFingerprint = HashStrings(
        [
            this.options.Alphabet,
            this.options.CanvasWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            this.options.CanvasHeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
            this.options.GlyphWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            this.options.GlyphHeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
            this.options.GeometryFeatureCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            this.options.MaximumGlyphs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            this.options.RejectClassIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            this.options.ConfidenceThreshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            this.options.StructuralRejectMinimumHeightRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            this.options.InputName,
            this.options.OutputName,
            this.options.StageVersion,
            "baseline-singleton-review-v1",
            ProviderFingerprint(this.options.AllowedProviders),
        ]);
    }

    public string ModelId => options.Model.ModelId;

    public string ModelVersion => options.Model.Version;

    public string ModelSha256 => options.Model.Sha256;

    public string ConfigurationFingerprint => configurationFingerprint;

    public async ValueTask<IReadOnlyList<OcrRecognition>> RecognizeBatchAsync(
        IReadOnlyList<OcrCrop> crops,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(crops);
        cancellationToken.ThrowIfCancellationRequested();
        if (crops.Count == 0)
        {
            return Array.Empty<OcrRecognition>();
        }

        if (crops.Any(crop =>
                crop.Width != options.CanvasWidth || crop.Height != options.CanvasHeight ||
                crop.Pixels.Length != checked(options.CanvasWidth * options.CanvasHeight) ||
                !PixelsAreValid(crop.Pixels.Span)))
        {
            throw new ArgumentException(
                "Every component-ensemble OCR crop must be finite grayscale [0,1] at the configured canvas size.",
                nameof(crops));
        }

        var encodedByCrop = new EncodedGlyphs[crops.Count];
        var totalGlyphs = 0;
        for (var cropIndex = 0; cropIndex < crops.Count; cropIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            encodedByCrop[cropIndex] = EncodeCrop(crops[cropIndex].Pixels.Span, options);
            totalGlyphs = checked(totalGlyphs + encodedByCrop[cropIndex].Count);
        }

        if (totalGlyphs == 0)
        {
            return FreezeEmpty(crops, encodedByCrop);
        }

        var encodedWidth = checked(options.GlyphWidth + options.GeometryFeatureCount);
        var valuesPerGlyph = checked(options.GlyphHeight * encodedWidth);
        var inputValues = new float[checked(totalGlyphs * valuesPerGlyph)];
        var destinationOffset = 0;
        foreach (EncodedGlyphs encoded in encodedByCrop)
        {
            encoded.Values.CopyTo(inputValues, destinationOffset);
            destinationOffset += encoded.Values.Length;
        }

        var request = new InferenceRequest(
            options.Model,
            new InferenceInput(
                inputValues,
                [totalGlyphs, 1, options.GlyphHeight, encodedWidth],
                options.InputName,
                options.OutputName),
            new StageCacheMaterial(
                HashStrings(crops.Select(static crop => crop.CropSha256)),
                string.Join(',', crops.Select(static crop => crop.RegionId)),
                string.Join(',', crops.Select(static crop => crop.SourceImage.ToString())),
                "ocr_component_recognition",
                options.StageVersion,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["configuration_sha256"] = configurationFingerprint,
                    ["allowed_providers"] = ProviderFingerprint(options.AllowedProviders),
                },
                OcrContract.Version),
            options.Timeout,
            options.AllowedProviders,
            options.BypassCache);

        InferenceResponse response = await runtime.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded || response.Execution is null)
        {
            return FreezeFailure(crops, ToOcrFailure(response.Error), 0);
        }

        if (options.AllowedProviders is not null &&
            !options.AllowedProviders.Contains(response.Execution.Provider))
        {
            return FreezeFailure(
                crops,
                new OcrFailure(
                    "OCR_PROVIDER_EVIDENCE_MISMATCH",
                    "error",
                    "Errors.OCR_PROVIDER_EVIDENCE_MISMATCH",
                    $"OCR component recognition executed with undeclared provider '{response.Execution.Provider}'.",
                    false,
                    "repair_inference_provider_policy"),
                response.Execution.Timing.InferenceMilliseconds / crops.Count);
        }

        var classCount = options.Alphabet.Length + 1;
        if (response.Execution.Output.Count != checked(totalGlyphs * classCount))
        {
            return FreezeFailure(
                crops,
                new OcrFailure(
                    "OCR_MODEL_OUTPUT_SHAPE_MISMATCH",
                    "error",
                    "Errors.OCR_MODEL_OUTPUT_SHAPE_MISMATCH",
                    $"OCR component model returned {response.Execution.Output.Count} values; " +
                    $"{totalGlyphs * classCount} were required.",
                    false,
                    "select_compatible_model"),
                response.Execution.Timing.InferenceMilliseconds / crops.Count);
        }

        float[] output = response.Execution.Output.ToArray();
        var results = new OcrRecognition[crops.Count];
        var glyphOffset = 0;
        for (var cropIndex = 0; cropIndex < crops.Count; cropIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EncodedGlyphs encoded = encodedByCrop[cropIndex];
            if (encoded.HasAmbiguousBaselineSingleton)
            {
                results[cropIndex] = AmbiguousBaselineSingletonFailure(crops[cropIndex],
                    response.Execution.Timing.InferenceMilliseconds / Math.Max(1, crops.Count));
                continue;
            }

            OcrRecognitionAlternative[] alternatives = Decode(
                output.AsSpan(glyphOffset * classCount, encoded.Count * classCount),
                encoded,
                crops[cropIndex].SourceImage,
                options);
            glyphOffset += encoded.Count;
            results[cropIndex] = new OcrRecognition(
                crops[cropIndex].RegionId,
                crops[cropIndex].SourceImage,
                Array.AsReadOnly(alternatives),
                response.Execution.Timing.InferenceMilliseconds / Math.Max(1, crops.Count));
        }

        return Array.AsReadOnly(results);
    }

    public static void ValidateOptions(LocalOnnxComponentTextRecognizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Model.Validate();
        if (string.IsNullOrWhiteSpace(options.Alphabet) ||
            options.Alphabet.Distinct().Count() != options.Alphabet.Length ||
            options.CanvasWidth != 128 || options.CanvasHeight != 32 ||
            options.GlyphWidth != 20 || options.GlyphHeight != 24 ||
            options.GeometryFeatureCount != 6 ||
            options.MaximumGlyphs is < 1 or > 32 ||
            options.RejectClassIndex != options.Alphabet.Length ||
            options.ConfidenceThreshold is < 0 or > 1 ||
            options.StructuralRejectMinimumHeightRatio is <= 0 or > 1 ||
            string.IsNullOrWhiteSpace(options.InputName) ||
            string.IsNullOrWhiteSpace(options.OutputName) ||
            string.IsNullOrWhiteSpace(options.StageVersion) ||
            options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(5) ||
            !ValidProviderPolicy(options.AllowedProviders))
        {
            throw new ArgumentException(
                "Local ONNX component-ensemble OCR recognizer options are invalid.",
                nameof(options));
        }
    }

    private static EncodedGlyphs EncodeCrop(
        ReadOnlySpan<float> pixels,
        LocalOnnxComponentTextRecognizerOptions options)
    {
        var raster = new byte[pixels.Length];
        for (var index = 0; index < pixels.Length; index++)
        {
            raster[index] = (byte)Math.Clamp(
                (int)Math.Round(pixels[index] * 255d, MidpointRounding.ToEven),
                byte.MinValue,
                byte.MaxValue);
        }

        ForegroundFilterResult foregroundResult = FilterForeground(raster, options.CanvasWidth, options.CanvasHeight);
        bool[] foreground = foregroundResult.Pixels;
        var intervals = ActiveIntervals(foreground, options.CanvasWidth, options.CanvasHeight);
        var glyphs = new List<float[]>();
        var structuralReject = false;
        foreach ((int left, int right) in intervals)
        {
            var component = EncodeGlyph(raster, foreground, left, right, options);
            if (component is null)
            {
                continue;
            }

            structuralReject |= component.HeightRatio >= options.StructuralRejectMinimumHeightRatio;
            glyphs.Add(component.Values);
        }

        if (foregroundResult.HasAmbiguousBaselineSingleton)
        {
            return new EncodedGlyphs([], 0, Rejected: false, HasAmbiguousBaselineSingleton: true);
        }

        if (glyphs.Count == 0 || glyphs.Count > options.MaximumGlyphs || structuralReject)
        {
            return new EncodedGlyphs([], 0,
                Rejected: structuralReject || glyphs.Count > options.MaximumGlyphs,
                HasAmbiguousBaselineSingleton: false);
        }

        var values = new float[glyphs.Sum(static glyph => glyph.Length)];
        var offset = 0;
        foreach (float[] glyph in glyphs)
        {
            glyph.CopyTo(values, offset);
            offset += glyph.Length;
        }

        return new EncodedGlyphs(values, glyphs.Count, Rejected: false, HasAmbiguousBaselineSingleton: false);
    }

    private static ForegroundFilterResult FilterForeground(byte[] raster, int width, int height)
    {
        byte low = raster.Min();
        byte[] sorted = (byte[])raster.Clone();
        Array.Sort(sorted);
        int median = sorted.Length % 2 == 0
            ? (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2
            : sorted[sorted.Length / 2];
        int threshold = Math.Min(232, Math.Max(low + 12, (low + median) / 2));
        var raw = new bool[raster.Length];
        for (var index = 0; index < raster.Length; index++)
        {
            raw[index] = raster[index] <= threshold;
        }

        var visited = new bool[raw.Length];
        var filtered = new bool[raw.Length];
        var retainedComponents = new List<ComponentBounds>();
        var singletonComponents = new List<int>();
        var stack = new Stack<int>();
        var points = new List<int>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int start = (y * width) + x;
                if (!raw[start] || visited[start])
                {
                    continue;
                }

                stack.Clear();
                points.Clear();
                stack.Push(start);
                visited[start] = true;
                while (stack.Count > 0)
                {
                    int current = stack.Pop();
                    points.Add(current);
                    int currentY = current / width;
                    int currentX = current % width;
                    for (var neighborY = Math.Max(0, currentY - 1);
                         neighborY < Math.Min(height, currentY + 2);
                         neighborY++)
                    {
                        for (var neighborX = Math.Max(0, currentX - 1);
                             neighborX < Math.Min(width, currentX + 2);
                             neighborX++)
                        {
                            int neighbor = (neighborY * width) + neighborX;
                            if (raw[neighbor] && !visited[neighbor])
                            {
                                visited[neighbor] = true;
                                stack.Push(neighbor);
                            }
                        }
                    }
                }

                if (points.Count >= 2)
                {
                    retainedComponents.Add(ComponentBounds.FromPoints(points, width));
                    foreach (int point in points)
                    {
                        filtered[point] = true;
                    }
                }
                else
                {
                    singletonComponents.Add(points[0]);
                }
            }
        }

        return new ForegroundFilterResult(
            filtered,
            HasAmbiguousBaselineSingleton(singletonComponents, retainedComponents, width));
    }

    private static bool HasAmbiguousBaselineSingleton(
        IReadOnlyList<int> singletonPixels,
        IReadOnlyList<ComponentBounds> retainedComponents,
        int width)
    {
        if (retainedComponents.Count < 2)
        {
            return false;
        }

        foreach (int pixel in singletonPixels)
        {
            int x = pixel % width;
            int y = pixel / width;
            ComponentBounds? left = null;
            ComponentBounds? right = null;
            foreach (ComponentBounds component in retainedComponents)
            {
                if (component.Right < x &&
                    (left is null || component.Right > left.Value.Right))
                {
                    left = component;
                }

                if (component.Left > x &&
                    (right is null || component.Left < right.Value.Left))
                {
                    right = component;
                }
            }

            if (left is not { } leftBounds || right is not { } rightBounds)
            {
                continue;
            }

            // A singleton at the shared lower edge of two distinct glyph
            // components could be a decimal point or isolated scan noise.
            // Keep filtering it from the model tensor, but require review.
            if (Math.Abs(leftBounds.Bottom - rightBounds.Bottom) <= 2 &&
                y >= Math.Min(leftBounds.Bottom, rightBounds.Bottom) - 1 &&
                y <= Math.Max(leftBounds.Bottom, rightBounds.Bottom) + 2)
            {
                return true;
            }
        }

        return false;
    }

    private static List<(int Left, int Right)> ActiveIntervals(
        bool[] foreground,
        int width,
        int height)
    {
        var intervals = new List<(int Left, int Right)>();
        int? start = null;
        for (var x = 0; x < width; x++)
        {
            var active = false;
            for (var y = 0; y < height && !active; y++)
            {
                active = foreground[(y * width) + x];
            }

            if (active && start is null)
            {
                start = x;
            }

            if (start.HasValue && (!active || x == width - 1))
            {
                int right = active && x == width - 1 ? x : x - 1;
                if (intervals.Count > 0 && start.Value - intervals[^1].Right - 1 <= 1)
                {
                    intervals[^1] = (intervals[^1].Left, right);
                }
                else
                {
                    intervals.Add((start.Value, right));
                }

                start = null;
            }
        }

        return intervals;
    }

    private static EncodedGlyph? EncodeGlyph(
        byte[] raster,
        bool[] foreground,
        int left,
        int right,
        LocalOnnxComponentTextRecognizerOptions options)
    {
        var minimumY = options.CanvasHeight;
        var maximumY = -1;
        var minimumX = right;
        var maximumX = left;
        var count = 0;
        long foregroundSum = 0;
        for (var y = 0; y < options.CanvasHeight; y++)
        {
            for (var x = left; x <= right; x++)
            {
                int index = (y * options.CanvasWidth) + x;
                if (!foreground[index])
                {
                    continue;
                }

                minimumY = Math.Min(minimumY, y);
                maximumY = Math.Max(maximumY, y);
                minimumX = Math.Min(minimumX, x);
                maximumX = Math.Max(maximumX, x);
                foregroundSum += raster[index];
                count++;
            }
        }

        if (count < 2)
        {
            return null;
        }

        int sourceWidth = maximumX - minimumX + 1;
        int sourceHeight = maximumY - minimumY + 1;
        var source = new byte[checked(sourceWidth * sourceHeight)];
        for (var y = 0; y < sourceHeight; y++)
        {
            Array.Copy(
                raster,
                ((minimumY + y) * options.CanvasWidth) + minimumX,
                source,
                y * sourceWidth,
                sourceWidth);
        }

        double scale = Math.Min(
            (options.GlyphWidth - 4d) / Math.Max(1, sourceWidth),
            (options.GlyphHeight - 4d) / Math.Max(1, sourceHeight));
        int resizedWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale, MidpointRounding.ToEven));
        int resizedHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale, MidpointRounding.ToEven));
        byte[] resized = PillowBilinearResizer.Resize(
            source,
            sourceWidth,
            sourceHeight,
            resizedWidth,
            resizedHeight);
        var normalized = new float[checked(options.GlyphWidth * options.GlyphHeight)];
        int offsetX = (options.GlyphWidth - resizedWidth) / 2;
        int offsetY = (options.GlyphHeight - resizedHeight) / 2;
        float maximumInk = 0;
        for (var y = 0; y < resizedHeight; y++)
        {
            for (var x = 0; x < resizedWidth; x++)
            {
                float ink = 1f - (resized[(y * resizedWidth) + x] / 255f);
                normalized[((offsetY + y) * options.GlyphWidth) + offsetX + x] = ink;
                maximumInk = Math.Max(maximumInk, ink);
            }
        }

        if (maximumInk > 0)
        {
            for (var index = 0; index < normalized.Length; index++)
            {
                normalized[index] /= maximumInk;
            }
        }

        int componentWidth = right - left + 1;
        int componentHeight = maximumY - minimumY + 1;
        int area = Math.Max(1, componentHeight * componentWidth);
        float foregroundMean = (float)foregroundSum / count;
        float[] geometry =
        [
            (float)(componentHeight / (double)options.CanvasHeight),
            (float)(componentWidth / (double)options.CanvasHeight),
            (float)(((minimumY + maximumY) / 2d) / (options.CanvasHeight - 1)),
            (float)(count / (double)area),
            (float)(foregroundMean / 255d),
            (float)(componentWidth / (double)Math.Max(1, componentHeight)),
        ];
        int encodedWidth = options.GlyphWidth + options.GeometryFeatureCount;
        var values = new float[checked(options.GlyphHeight * encodedWidth)];
        for (var y = 0; y < options.GlyphHeight; y++)
        {
            Array.Copy(normalized, y * options.GlyphWidth, values, y * encodedWidth, options.GlyphWidth);
            Array.Copy(geometry, 0, values, (y * encodedWidth) + options.GlyphWidth, geometry.Length);
        }

        return new EncodedGlyph(values, geometry[0]);
    }

    private static OcrRecognitionAlternative[] Decode(
        ReadOnlySpan<float> logits,
        EncodedGlyphs encoded,
        OcrSourceImage sourceImage,
        LocalOnnxComponentTextRecognizerOptions options)
    {
        if (encoded.Rejected || encoded.Count == 0)
        {
            return [];
        }

        int classCount = options.Alphabet.Length + 1;
        var text = new StringBuilder(encoded.Count);
        double confidenceTotal = 0;
        for (var glyph = 0; glyph < encoded.Count; glyph++)
        {
            ReadOnlySpan<float> row = logits.Slice(glyph * classCount, classCount);
            if (!float.IsFinite(row[0]))
            {
                throw new InvalidDataException("OCR component logits contain a non-finite value.");
            }

            float maximum = row[0];
            for (var index = 1; index < row.Length; index++)
            {
                if (!float.IsFinite(row[index]))
                {
                    throw new InvalidDataException("OCR component logits contain a non-finite value.");
                }

                maximum = Math.Max(maximum, row[index]);
            }

            var denominator = 0d;
            var bestProbability = double.NegativeInfinity;
            var bestClass = -1;
            for (var index = 0; index < row.Length; index++)
            {
                denominator += Math.Exp(row[index] - maximum);
            }

            for (var index = 0; index < row.Length; index++)
            {
                double probability = Math.Exp(row[index] - maximum) / denominator;
                if (probability > bestProbability)
                {
                    bestProbability = probability;
                    bestClass = index;
                }
            }

            if (bestClass == options.RejectClassIndex || bestProbability < options.ConfidenceThreshold)
            {
                return [];
            }

            text.Append(options.Alphabet[bestClass]);
            confidenceTotal += bestProbability;
        }

        string result = text.ToString();
        return GraphNumericParser.IsLiteralGraphNumber(result)
            ? [new OcrRecognitionAlternative(result, confidenceTotal / encoded.Count, sourceImage)]
            : [];
    }

    private static ReadOnlyCollection<OcrRecognition> FreezeEmpty(
        IReadOnlyList<OcrCrop> crops,
        IReadOnlyList<EncodedGlyphs> encoded) =>
        Array.AsReadOnly(crops.Select((crop, index) => encoded[index].HasAmbiguousBaselineSingleton
            ? AmbiguousBaselineSingletonFailure(crop, 0)
            : new OcrRecognition(crop.RegionId, crop.SourceImage,
                Array.Empty<OcrRecognitionAlternative>(), 0)).ToArray());

    private static OcrRecognition AmbiguousBaselineSingletonFailure(OcrCrop crop, double inferenceMilliseconds) =>
        new(
            crop.RegionId,
            crop.SourceImage,
            Array.Empty<OcrRecognitionAlternative>(),
            inferenceMilliseconds,
            new OcrFailure(
                "OCR_COMPONENT_BASELINE_SINGLETON_AMBIGUOUS",
                "warning",
                "Errors.OCR_RECOGNITION_FAILED",
                "A one-pixel mark between lower-aligned glyphs could be a decimal point or scan noise; " +
                "the crop was withheld for review.",
                true,
                "review_ocr_crop"));

    private static ReadOnlyCollection<OcrRecognition> FreezeFailure(
        IReadOnlyList<OcrCrop> crops,
        OcrFailure failure,
        double inferenceMilliseconds) =>
        Array.AsReadOnly(crops.Select(crop => new OcrRecognition(
            crop.RegionId,
            crop.SourceImage,
            Array.Empty<OcrRecognitionAlternative>(),
            inferenceMilliseconds,
            failure)).ToArray());

    private static OcrFailure ToOcrFailure(InferenceError? error) =>
        error is null
            ? new OcrFailure(
                "OCR_INFERENCE_FAILED",
                "error",
                "Errors.OCR_INFERENCE_FAILED",
                "The local inference runtime returned no OCR component result.",
                true,
                "retry")
            : new OcrFailure(
                error.Code,
                error.Severity,
                error.UserMessageKey,
                error.TechnicalMessage,
                error.Recoverable,
                error.SuggestedAction);

    private static bool ValidProviderPolicy(IReadOnlyList<InferenceProvider>? providers) =>
        providers is null ||
        (providers.Count > 0 &&
         providers.Contains(InferenceProvider.Cpu) &&
         providers.All(static provider => provider is InferenceProvider.Cpu or InferenceProvider.DirectMl) &&
         providers.Distinct().Count() == providers.Count);

    private static bool PixelsAreValid(ReadOnlySpan<float> pixels)
    {
        foreach (float value in pixels)
        {
            if (!float.IsFinite(value) || value is < 0 or > 1)
            {
                return false;
            }
        }

        return true;
    }

    private static string ProviderFingerprint(IReadOnlyList<InferenceProvider>? providers) =>
        providers is null
            ? "policy-default"
            : string.Join(',', providers
                .Distinct()
                .OrderBy(static provider => provider)
                .Select(static provider => provider.ToString()));

    private static string HashStrings(IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string value in values)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed record EncodedGlyph(float[] Values, float HeightRatio);

    private sealed record EncodedGlyphs(
        float[] Values,
        int Count,
        bool Rejected,
        bool HasAmbiguousBaselineSingleton);

    private sealed record ForegroundFilterResult(bool[] Pixels, bool HasAmbiguousBaselineSingleton);

    private readonly record struct ComponentBounds(int Left, int Right, int Bottom)
    {
        public static ComponentBounds FromPoints(IReadOnlyList<int> points, int width)
        {
            int left = int.MaxValue;
            int right = int.MinValue;
            int bottom = int.MinValue;
            foreach (int point in points)
            {
                int x = point % width;
                int y = point / width;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }

            return new ComponentBounds(left, right, bottom);
        }
    }
}
