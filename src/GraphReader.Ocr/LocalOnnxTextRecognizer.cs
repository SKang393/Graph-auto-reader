// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;
using GraphReader.Inference;

namespace GraphReader.Ocr;

public enum OcrTensorLayout
{
    ChannelsFirst,
    ChannelsLast,
}

public enum OcrOutputLayout
{
    BatchTimeClass,
    TimeBatchClass,
}

public enum OcrRecognitionOutputActivation
{
    Auto,
    Probabilities,
    Logits,
}

public sealed record LocalOnnxTextRecognizerOptions(
    ModelIdentity Model,
    string Alphabet)
{
    public int InputWidth { get; init; } = 128;

    public int InputHeight { get; init; } = 32;

    public bool DynamicInputWidth { get; init; }

    public int MaximumInputWidth { get; init; } = 4096;

    public int InputChannels { get; init; } = 1;

    public OcrTensorLayout InputLayout { get; init; } = OcrTensorLayout.ChannelsFirst;

    public OcrTensorColorMode InputColorMode { get; init; } =
        OcrTensorColorMode.GrayscaleReplicated;

    public OcrOutputLayout OutputLayout { get; init; } = OcrOutputLayout.BatchTimeClass;

    public OcrRecognitionOutputActivation OutputActivation { get; init; } =
        OcrRecognitionOutputActivation.Auto;

    public int? ExpectedTimeSteps { get; init; }

    public int BlankClassIndex { get; init; }

    public int MaximumAlternatives { get; init; } = 3;

    public string InputName { get; init; } = "input";

    public string OutputName { get; init; } = "output";

    public string StageVersion { get; init; } = "0.1.0";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public float NormalizeMean { get; init; } = 0.5f;

    public float NormalizeScale { get; init; } = 2f;

    public IReadOnlyList<float>? ChannelMeans { get; init; }

    public IReadOnlyList<float>? ChannelScales { get; init; }

    public IReadOnlyList<InferenceProvider>? AllowedProviders { get; init; }

    public bool BypassCache { get; init; }
}

public sealed record CtcDecodedAlternative(string Text, double Confidence);

public static class CtcRecognitionDecoder
{
    internal const string ConfigurationVersion = "ctc-single-path-support-v3";

    public static IReadOnlyList<CtcDecodedAlternative> Decode(
        ReadOnlySpan<float> logits,
        int timeSteps,
        string alphabet,
        int blankClassIndex = 0,
        int maximumAlternatives = 3,
        OcrRecognitionOutputActivation outputActivation = OcrRecognitionOutputActivation.Auto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alphabet);
        List<string> alphabetSymbols = GetAlphabetSymbols(alphabet);
        var classCount = alphabetSymbols.Count + 1;
        if (timeSteps <= 0 || logits.Length != checked(timeSteps * classCount) ||
            blankClassIndex < 0 || blankClassIndex >= classCount || maximumAlternatives <= 0 ||
            !Enum.IsDefined(outputActivation))
        {
            throw new ArgumentException("CTC logits or decoder options are invalid.", nameof(logits));
        }

        var bestClasses = new int[timeSteps];
        var secondClasses = new int[timeSteps];
        var bestProbabilities = new double[timeSteps];
        var uncertainty = new double[timeSteps];
        for (var time = 0; time < timeSteps; time++)
        {
            var row = logits.Slice(time * classCount, classCount);
            double[] probabilities = ToProbabilities(row, outputActivation);

            var best = -1;
            var second = -1;
            var bestProbability = double.NegativeInfinity;
            var secondProbability = double.NegativeInfinity;
            for (var classIndex = 0; classIndex < classCount; classIndex++)
            {
                double probability = probabilities[classIndex];
                if (probability > bestProbability)
                {
                    second = best;
                    secondProbability = bestProbability;
                    best = classIndex;
                    bestProbability = probability;
                }
                else if (probability > secondProbability)
                {
                    second = classIndex;
                    secondProbability = probability;
                }
            }

            bestClasses[time] = best;
            secondClasses[time] = second;
            bestProbabilities[time] = bestProbability;
            uncertainty[time] = secondProbability / Math.Max(bestProbability, double.Epsilon);
        }

        var greedy = DecodeClasses(bestClasses, bestProbabilities, alphabetSymbols, blankClassIndex);
        var candidates = new List<CtcDecodedAlternative>
        {
            greedy,
        };
        foreach (var time in Enumerable.Range(0, timeSteps).OrderByDescending(index => uncertainty[index]))
        {
            if (candidates.Count >= maximumAlternatives)
            {
                break;
            }

            // A zero-probability class is not recognition evidence. In particular,
            // changing a certain blank must not manufacture an extra character.
            if (uncertainty[time] <= 0)
            {
                continue;
            }

            var modified = (int[])bestClasses.Clone();
            modified[time] = secondClasses[time];
            var probabilities = (double[])bestProbabilities.Clone();
            probabilities[time] *= uncertainty[time];
            var candidate = DecodeClasses(modified, probabilities, alphabetSymbols, blankClassIndex);
            if (candidate.Text.Length > 0 && candidates.All(item => !string.Equals(item.Text, candidate.Text, StringComparison.Ordinal)))
            {
                candidates.Add(candidate with
                {
                    // This path differs from the greedy path at exactly one time
                    // step. Its relative support is second/best at that step.
                    // Averaging the other, certain characters would otherwise
                    // give an unsupported insertion or deletion high confidence.
                    // This cap is not a summed CTC sequence posterior.
                    // Preserve the existing strict ordering below the primary
                    // reading, including equal-probability CTC paths.
                    Confidence = Math.Min(candidate.Confidence,
                        greedy.Confidence * Math.Min(0.99, uncertainty[time])),
                });
            }
        }

        return OcrCollections.Freeze(candidates.Where(static candidate => candidate.Text.Length > 0));
    }

    internal static int CountAlphabetSymbols(string alphabet) => GetAlphabetSymbols(alphabet).Count;

    internal static IReadOnlyList<string> GetAlphabetSymbolsForValidation(string? alphabet) =>
        string.IsNullOrEmpty(alphabet) ? Array.Empty<string>() : GetAlphabetSymbols(alphabet);

    private static List<string> GetAlphabetSymbols(string alphabet)
    {
        var symbols = new List<string>();
        foreach (Rune rune in alphabet.EnumerateRunes())
        {
            symbols.Add(rune.ToString());
        }

        return symbols;
    }

    private static double[] ToProbabilities(
        ReadOnlySpan<float> values,
        OcrRecognitionOutputActivation activation)
    {
        bool probabilityDistribution = IsProbabilityDistribution(values);
        if (activation == OcrRecognitionOutputActivation.Probabilities && !probabilityDistribution)
        {
            throw new InvalidDataException(
                "CTC probability output must contain finite [0,1] rows whose values sum to one.");
        }

        if (activation == OcrRecognitionOutputActivation.Probabilities ||
            (activation == OcrRecognitionOutputActivation.Auto && probabilityDistribution))
        {
            return values.ToArray().Select(static value => (double)value).ToArray();
        }

        float maximum = float.NegativeInfinity;
        foreach (float value in values)
        {
            if (!float.IsFinite(value))
            {
                throw new InvalidDataException("CTC output contains a non-finite value.");
            }

            maximum = Math.Max(maximum, value);
        }

        var probabilities = new double[values.Length];
        double denominator = 0;
        for (var index = 0; index < values.Length; index++)
        {
            probabilities[index] = Math.Exp(values[index] - maximum);
            denominator += probabilities[index];
        }

        for (var index = 0; index < probabilities.Length; index++)
        {
            probabilities[index] /= denominator;
        }

        return probabilities;
    }

    private static bool IsProbabilityDistribution(ReadOnlySpan<float> values)
    {
        double sum = 0;
        foreach (float value in values)
        {
            if (!float.IsFinite(value) || value is < 0 or > 1)
            {
                return false;
            }

            sum += value;
        }

        double tolerance = Math.Max(1e-5, values.Length * 1e-6);
        return Math.Abs(sum - 1d) <= tolerance;
    }

    private static CtcDecodedAlternative DecodeClasses(
        int[] classes,
        double[] probabilities,
        List<string> alphabet,
        int blankClassIndex)
    {
        var builder = new StringBuilder(classes.Length);
        var acceptedProbabilities = new List<double>();
        var prior = -1;
        for (var index = 0; index < classes.Length; index++)
        {
            var classIndex = classes[index];
            if (classIndex != blankClassIndex && classIndex != prior)
            {
                var alphabetIndex = classIndex < blankClassIndex ? classIndex : classIndex - 1;
                builder.Append(alphabet[alphabetIndex]);
                acceptedProbabilities.Add(probabilities[index]);
            }

            prior = classIndex;
        }

        var confidence = acceptedProbabilities.Count == 0 ? 0 : acceptedProbabilities.Average();
        return new CtcDecodedAlternative(builder.ToString(), Math.Clamp(confidence, 0, 1));
    }
}

/// <summary>
/// Local-only batched CTC recognition over the shared ONNX inference runtime.
/// The adapter contains no model weights and does not own the supplied runtime.
/// </summary>
public sealed class LocalOnnxTextRecognizer : ITextRecognizer
{
    private readonly InferenceRuntime _runtime;
    private readonly LocalOnnxTextRecognizerOptions _options;
    private readonly string _configurationFingerprint;

    public LocalOnnxTextRecognizer(
        InferenceRuntime runtime,
        LocalOnnxTextRecognizerOptions options)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(options);
        _options = options with
        {
            ChannelMeans = options.ChannelMeans is null
                ? null
                : Array.AsReadOnly(options.ChannelMeans.ToArray()),
            ChannelScales = options.ChannelScales is null
                ? null
                : Array.AsReadOnly(options.ChannelScales.ToArray()),
            AllowedProviders = options.AllowedProviders is null
                ? null
                : Array.AsReadOnly(options.AllowedProviders.ToArray()),
        };
        ValidateOptions(_options);

        _configurationFingerprint = CreateConfigurationFingerprint(_options);
    }

    public string ModelId => _options.Model.ModelId;

    public string ModelVersion => _options.Model.Version;

    public string ModelSha256 => _options.Model.Sha256;

    public string ConfigurationFingerprint => _configurationFingerprint;

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

        int inputWidth = crops[0].Width;
        if (crops.Any(crop =>
                crop.Width != inputWidth ||
                (!_options.DynamicInputWidth && crop.Width != _options.InputWidth) ||
                (_options.DynamicInputWidth &&
                 (crop.Width < _options.InputWidth || crop.Width > _options.MaximumInputWidth)) ||
                crop.Height != _options.InputHeight ||
                crop.Pixels.Length != checked(crop.Width * crop.Height) ||
                (_options.InputColorMode == OcrTensorColorMode.Bgr &&
                 !HasValidBgrPixels(crop))))
        {
            throw new ArgumentException("Every OCR crop must match the configured model input dimensions.", nameof(crops));
        }

        var pixelsPerCrop = checked(inputWidth * _options.InputHeight);
        var valuesPerCrop = checked(pixelsPerCrop * _options.InputChannels);
        var inputValues = new float[checked(crops.Count * valuesPerCrop)];
        for (var cropIndex = 0; cropIndex < crops.Count; cropIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrCrop crop = crops[cropIndex];
            ReadOnlySpan<float> grayscale = crop.Pixels.Span;
            for (var valueIndex = 0; valueIndex < grayscale.Length; valueIndex++)
            {
                for (var channel = 0; channel < _options.InputChannels; channel++)
                {
                    float mean = _options.ChannelMeans?[channel] ?? _options.NormalizeMean;
                    float scale = _options.ChannelScales?[channel] ?? _options.NormalizeScale;
                    float sourceValue = _options.InputColorMode == OcrTensorColorMode.Bgr
                        ? crop.BgrPixels!.Pixels.Span[
                            checked(((valueIndex / crop.Width) * crop.BgrPixels.Stride) +
                                    ((valueIndex % crop.Width) * 3) + channel)]
                        : grayscale[valueIndex];
                    var normalized = (sourceValue - mean) * scale;
                    var destinationIndex = _options.InputLayout == OcrTensorLayout.ChannelsFirst
                        ? (cropIndex * valuesPerCrop) + (channel * pixelsPerCrop) + valueIndex
                        : (cropIndex * valuesPerCrop) + (valueIndex * _options.InputChannels) + channel;
                    inputValues[destinationIndex] = normalized;
                }
            }
        }

        IReadOnlyList<long> inputShape = _options.InputLayout == OcrTensorLayout.ChannelsFirst
            ? [crops.Count, _options.InputChannels, _options.InputHeight, inputWidth]
            : [crops.Count, _options.InputHeight, inputWidth, _options.InputChannels];

        var inputHash = HashStrings(crops.Select(static crop => crop.CropSha256));
        var request = new InferenceRequest(
            _options.Model,
            new InferenceInput(
                inputValues,
                inputShape,
                _options.InputName,
                _options.OutputName),
            new StageCacheMaterial(
                inputHash,
                string.Join(',', crops.Select(static crop => crop.RegionId)),
                string.Join(',', crops.Select(static crop => crop.SourceImage.ToString())),
                "ocr_recognition",
                _options.StageVersion,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["alphabet_sha256"] = HashStrings([_options.Alphabet]),
                    ["blank_class_index"] = _options.BlankClassIndex,
                    ["input_height"] = _options.InputHeight,
                    ["input_width"] = inputWidth,
                    ["dynamic_input_width"] = _options.DynamicInputWidth,
                    ["maximum_input_width"] = _options.MaximumInputWidth,
                    ["input_channels"] = _options.InputChannels,
                    ["input_layout"] = _options.InputLayout.ToString(),
                    ["input_color_mode"] = _options.InputColorMode.ToString(),
                    ["output_layout"] = _options.OutputLayout.ToString(),
                    ["output_activation"] = _options.OutputActivation.ToString(),
                    ["expected_time_steps"] = _options.ExpectedTimeSteps,
                    ["normalization_mean"] = _options.NormalizeMean,
                    ["normalization_scale"] = _options.NormalizeScale,
                    ["channel_means"] = _options.ChannelMeans?.ToArray(),
                    ["channel_scales"] = _options.ChannelScales?.ToArray(),
                    ["allowed_providers"] = ProviderFingerprint(_options.AllowedProviders),
                },
                OcrContract.Version),
            _options.Timeout,
            _options.AllowedProviders,
            _options.BypassCache);

        var response = await _runtime.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded || response.Execution is null)
        {
            var failure = ToOcrFailure(response.Error);
            return OcrCollections.Freeze(crops.Select(crop => new OcrRecognition(
                crop.RegionId,
                crop.SourceImage,
                Array.Empty<OcrRecognitionAlternative>(),
                0,
                failure)));
        }

        if (_options.AllowedProviders is not null &&
            !_options.AllowedProviders.Contains(response.Execution.Provider))
        {
            var failure = new OcrFailure(
                "OCR_PROVIDER_EVIDENCE_MISMATCH",
                "error",
                "Errors.OCR_PROVIDER_EVIDENCE_MISMATCH",
                $"OCR recognition executed with undeclared provider '{response.Execution.Provider}'.",
                false,
                "repair_inference_provider_policy");
            return OcrCollections.Freeze(crops.Select(crop => new OcrRecognition(
                crop.RegionId,
                crop.SourceImage,
                Array.Empty<OcrRecognitionAlternative>(),
                response.Execution.Timing.InferenceMilliseconds / crops.Count,
                failure)));
        }

        var classCount = CtcRecognitionDecoder.CountAlphabetSymbols(_options.Alphabet) + 1;
        var denominator = checked(crops.Count * classCount);
        if (response.Execution.Output.Count == 0 || response.Execution.Output.Count % denominator != 0)
        {
            var failure = new OcrFailure(
                "OCR_MODEL_OUTPUT_INVALID",
                "error",
                "Errors.OCR_MODEL_OUTPUT_INVALID",
                "OCR model output is not a flattened [batch,time,class] tensor for the configured alphabet.",
                false,
                "select_compatible_model");
            return OcrCollections.Freeze(crops.Select(crop => new OcrRecognition(
                crop.RegionId,
                crop.SourceImage,
                Array.Empty<OcrRecognitionAlternative>(),
                response.Execution.Timing.InferenceMilliseconds / crops.Count,
                failure)));
        }

        var timeSteps = response.Execution.Output.Count / denominator;
        if (_options.ExpectedTimeSteps.HasValue && timeSteps != _options.ExpectedTimeSteps.Value)
        {
            var failure = new OcrFailure(
                "OCR_MODEL_OUTPUT_SHAPE_MISMATCH",
                "error",
                "Errors.OCR_MODEL_OUTPUT_SHAPE_MISMATCH",
                $"OCR model returned {timeSteps} time steps; {_options.ExpectedTimeSteps.Value} were declared.",
                false,
                "select_compatible_model");
            return OcrCollections.Freeze(crops.Select(crop => new OcrRecognition(
                crop.RegionId,
                crop.SourceImage,
                Array.Empty<OcrRecognitionAlternative>(),
                response.Execution.Timing.InferenceMilliseconds / crops.Count,
                failure)));
        }

        var valuesPerResult = checked(timeSteps * classCount);
        var output = response.Execution.Output.ToArray();
        var results = new OcrRecognition[crops.Count];
        for (var cropIndex = 0; cropIndex < crops.Count; cropIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cropOutput = GetCropOutput(
                output,
                cropIndex,
                crops.Count,
                timeSteps,
                classCount,
                _options.OutputLayout);
            var decoded = CtcRecognitionDecoder.Decode(
                cropOutput,
                timeSteps,
                _options.Alphabet,
                _options.BlankClassIndex,
                _options.MaximumAlternatives,
                _options.OutputActivation);
            results[cropIndex] = new OcrRecognition(
                crops[cropIndex].RegionId,
                crops[cropIndex].SourceImage,
                OcrCollections.Freeze(decoded.Select(alternative => new OcrRecognitionAlternative(
                    alternative.Text,
                    alternative.Confidence,
                    crops[cropIndex].SourceImage))),
                response.Execution.Timing.InferenceMilliseconds / crops.Count);
        }

        return Array.AsReadOnly(results);
    }

    private static string CreateConfigurationFingerprint(LocalOnnxTextRecognizerOptions options) =>
        HashStrings([
            CtcRecognitionDecoder.ConfigurationVersion,
            options.MaximumAlternatives.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.Alphabet,
            options.InputWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.InputHeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.DynamicInputWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.MaximumInputWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.InputChannels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.InputLayout.ToString(),
            options.InputColorMode.ToString(),
            options.OutputLayout.ToString(),
            options.OutputActivation.ToString(),
            options.ExpectedTimeSteps?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "dynamic",
            options.BlankClassIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.InputName,
            options.OutputName,
            options.StageVersion,
            options.NormalizeMean.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            options.NormalizeScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            options.ChannelMeans is null
                ? "scalar-means"
                : string.Join(',', options.ChannelMeans.Select(static value =>
                    value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))),
            options.ChannelScales is null
                ? "scalar-scales"
                : string.Join(',', options.ChannelScales.Select(static value =>
                    value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))),
            ProviderFingerprint(options.AllowedProviders),
        ]);

    private static bool ValidChannelStatistics(LocalOnnxTextRecognizerOptions options)
    {
        if ((options.ChannelMeans is null) != (options.ChannelScales is null))
        {
            return false;
        }

        return options.ChannelMeans is null ||
            (options.ChannelMeans.Count == options.InputChannels &&
             options.ChannelScales!.Count == options.InputChannels &&
             options.ChannelMeans.All(static value => float.IsFinite(value)) &&
             options.ChannelScales.All(static value => float.IsFinite(value) && value != 0));
    }

    private static bool ValidProviderPolicy(IReadOnlyList<InferenceProvider>? providers) =>
        providers is null ||
        (providers.Count > 0 &&
         providers.Contains(InferenceProvider.Cpu) &&
         providers.All(static provider =>
             provider is InferenceProvider.Cpu or InferenceProvider.DirectMl) &&
             providers.Distinct().Count() == providers.Count);

    private static bool HasValidBgrPixels(OcrCrop crop) =>
        crop.BgrPixels is { } bgr &&
        bgr.Stride >= checked(crop.Width * 3) &&
        bgr.Pixels.Length == checked(bgr.Stride * crop.Height);

    public static void ValidateOptions(LocalOnnxTextRecognizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Model.Validate();
        IReadOnlyList<string> alphabetSymbols = CtcRecognitionDecoder.GetAlphabetSymbolsForValidation(
            options.Alphabet);
        if (alphabetSymbols.Count == 0 ||
            alphabetSymbols.Distinct(StringComparer.Ordinal).Count() != alphabetSymbols.Count ||
            options.InputWidth is < 1 or > 4096 || options.InputHeight is < 1 or > 4096 ||
            options.MaximumInputWidth < options.InputWidth || options.MaximumInputWidth > 4096 ||
            options.InputChannels is < 1 or > 4 ||
            !Enum.IsDefined(options.InputLayout) || !Enum.IsDefined(options.InputColorMode) ||
            !Enum.IsDefined(options.OutputLayout) ||
            (options.InputColorMode == OcrTensorColorMode.Bgr && options.InputChannels != 3) ||
            !Enum.IsDefined(options.OutputActivation) ||
            options.ExpectedTimeSteps is <= 0 or > 16_384 ||
            (options.DynamicInputWidth && options.ExpectedTimeSteps.HasValue) ||
            options.BlankClassIndex < 0 || options.BlankClassIndex > alphabetSymbols.Count ||
            options.MaximumAlternatives is < 1 or > 16 ||
            options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(5) ||
            !float.IsFinite(options.NormalizeMean) || !float.IsFinite(options.NormalizeScale) ||
            options.NormalizeScale == 0 ||
            !ValidChannelStatistics(options) ||
            !ValidProviderPolicy(options.AllowedProviders))
        {
            throw new ArgumentException("Local ONNX OCR recognizer options are invalid.", nameof(options));
        }
    }

    private static string ProviderFingerprint(IReadOnlyList<InferenceProvider>? providers) =>
        providers is null
            ? "policy-default"
            : string.Join(',', providers
                .Distinct()
                .OrderBy(static provider => provider)
                .Select(static provider => provider.ToString()));

    private static ReadOnlySpan<float> GetCropOutput(
        float[] output,
        int cropIndex,
        int batchSize,
        int timeSteps,
        int classCount,
        OcrOutputLayout layout)
    {
        var valuesPerResult = checked(timeSteps * classCount);
        if (layout == OcrOutputLayout.BatchTimeClass)
        {
            return output.AsSpan(cropIndex * valuesPerResult, valuesPerResult);
        }

        var reordered = new float[valuesPerResult];
        for (var time = 0; time < timeSteps; time++)
        {
            var sourceOffset = checked(((time * batchSize) + cropIndex) * classCount);
            output.AsSpan(sourceOffset, classCount).CopyTo(reordered.AsSpan(time * classCount, classCount));
        }

        return reordered;
    }

    private static string HashStrings(IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static OcrFailure ToOcrFailure(InferenceError? error) =>
        error is null
            ? new OcrFailure(
                "OCR_INFERENCE_FAILED",
                "error",
                "Errors.OCR_INFERENCE_FAILED",
                "The local inference runtime returned no OCR result.",
                true,
                "retry")
            : new OcrFailure(
                error.Code,
                error.Severity,
                error.UserMessageKey,
                error.TechnicalMessage,
                error.Recoverable,
                error.SuggestedAction);
}
