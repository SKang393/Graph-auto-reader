// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Inference;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

/// <summary>
/// Composes the project-trained component recognizer without changing the
/// checksum-sealed official PP-OCR adapter source. Approval still requires the
/// shared paired-model gate and an executable CPU preflight of both payloads.
/// </summary>
internal static class ProductionComponentOcrAdapterFactory
{
    internal const string RecognitionPostprocessingAlgorithm =
        "component_ensemble_numeric_v1";
    internal const string RecognitionPreprocessingAlgorithm =
        "component_glyph_encoding_v1";
    private const string DetectionPostprocessingAlgorithm = "db_postprocess_v1";
    private const string Alphabet = "0123456789.-%";
    private static readonly string[] GeometryFeatures =
    [
        "height_over_canvas_height",
        "width_over_canvas_height",
        "vertical_center_over_canvas_height_minus_one",
        "foreground_over_component_area",
        "mean_foreground_intensity",
        "width_over_height",
    ];

    internal static bool UsesComponentEnsemble(ResolvedProductionModel resolvedModel)
    {
        ArgumentNullException.ThrowIfNull(resolvedModel);
        VerifyChecksum(
            resolvedModel.ManifestPath,
            resolvedModel.ManifestSha256,
            "OCR recognition manifest");
        return UsesComponentEnsembleManifest(resolvedModel.ManifestPath);
    }

    internal static bool UsesComponentEnsembleManifest(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement postprocessing = RequiredObject(
            document.RootElement,
            "postprocessing",
            "OCR recognition manifest");
        return string.Equals(
            RequiredString(postprocessing, "algorithm", "OCR recognition postprocessing"),
            RecognitionPostprocessingAlgorithm,
            StringComparison.Ordinal);
    }

    internal static async Task<IProductionOcrAdapter> CreateAsync(
        ResolvedProductionModel detectionModel,
        ResolvedProductionModel recognitionModel,
        ProductionInferenceRuntimeHost runtimeHost,
        string reviewedOpenCvRuntimeSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detectionModel);
        ArgumentNullException.ThrowIfNull(recognitionModel);
        ArgumentNullException.ThrowIfNull(runtimeHost);
        reviewedOpenCvRuntimeSha256 = ValidateSha256(
            reviewedOpenCvRuntimeSha256,
            nameof(reviewedOpenCvRuntimeSha256));
        RequireTask(detectionModel, "ocr_detection");
        RequireTask(recognitionModel, "ocr_recognition");
        RequireCpu(detectionModel);
        RequireCpu(recognitionModel);
        VerifyChecksum(
            detectionModel.ManifestPath,
            detectionModel.ManifestSha256,
            "OCR detection manifest");
        VerifyChecksum(
            recognitionModel.ManifestPath,
            recognitionModel.ManifestSha256,
            "OCR recognition manifest");
        ProductionOcrApprovalGate.Validate(detectionModel, recognitionModel);

        LocalOnnxTextRegionDetectorOptions detectorOptions = ReadDetectionOptions(detectionModel);
        (LocalOnnxComponentTextRecognizerOptions Recognizer, OcrPipelineOptions Pipeline) recognition =
            ReadRecognitionOptions(recognitionModel.Identity, recognitionModel.ManifestPath);
        await ValidateExecutablePairAsync(
                detectorOptions,
                recognition.Recognizer,
                runtimeHost.Runtime,
                cancellationToken)
            .ConfigureAwait(false);

        InferenceRuntime runtime = runtimeHost.Runtime;
        var modelDetector = new LocalOnnxTextRegionDetector(runtime, detectorOptions);
        var detector = new GraphStructureConsensusTextRegionDetector(
            modelDetector,
            new ConnectedComponentTextRegionDetector());
        var recognizer = new LocalOnnxComponentTextRecognizer(runtime, recognition.Recognizer);
        var pipeline = new OcrPipeline(
            detector,
            recognizer,
            new MemoryOcrResultCache(),
            recognition.Pipeline);
        return ProductionOcrAdapter.CreateFromValidatedApprovedPipeline(
            pipeline,
            detectionModel.Identity,
            InferenceProvider.Cpu,
            recognitionModel.Identity,
            InferenceProvider.Cpu,
            reviewedOpenCvRuntimeSha256);
    }

    internal static (
        LocalOnnxComponentTextRecognizerOptions Recognizer,
        OcrPipelineOptions Pipeline) ReadRecognitionOptions(
        ModelIdentity identity,
        string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = document.RootElement;
        JsonElement input = SingleObject(root, "inputs", "OCR recognition");
        JsonElement output = SingleObject(root, "outputs", "OCR recognition");
        RequireString(input, "element_type", "float32", "OCR recognition input");
        RequireString(input, "layout", "NCHW", "OCR recognition input");
        RequireShape(
            input,
            "shape",
            ["glyph_count", "1", "24", "26"],
            "OCR recognition input");
        RequireStringArray(input, "channels", ["grayscale"], "OCR recognition input");
        RequireString(output, "element_type", "float32", "OCR recognition output");
        RequireString(output, "layout", "NC", "OCR recognition output");
        RequireShape(
            output,
            "shape",
            ["glyph_count", "14"],
            "OCR recognition output");
        RequireString(output, "alphabet", Alphabet, "OCR recognition output");
        int rejectClassIndex = RequiredReviewedInt32(
            output,
            "reject_class_index",
            13,
            "OCR recognition output");

        JsonElement preprocessing = RequiredObject(root, "preprocessing", "OCR recognition manifest");
        RequireString(
            preprocessing,
            "algorithm",
            RecognitionPreprocessingAlgorithm,
            "OCR recognition preprocessing");
        int canvasWidth = RequiredReviewedInt32(
            preprocessing,
            "canvas_width",
            128,
            "OCR recognition preprocessing");
        int canvasHeight = RequiredReviewedInt32(
            preprocessing,
            "canvas_height",
            32,
            "OCR recognition preprocessing");
        int glyphWidth = RequiredReviewedInt32(
            preprocessing,
            "glyph_width",
            20,
            "OCR recognition preprocessing");
        int glyphHeight = RequiredReviewedInt32(
            preprocessing,
            "glyph_height",
            24,
            "OCR recognition preprocessing");
        int geometryFeatureCount = RequiredReviewedInt32(
            preprocessing,
            "geometry_feature_count",
            6,
            "OCR recognition preprocessing");
        RequireString(
            preprocessing,
            "resampling",
            "half_pixel_bilinear_v1",
            "OCR recognition preprocessing");
        RequireString(
            preprocessing,
            "crop_resize_mode",
            "preserve_aspect_ratio_pad",
            "OCR recognition preprocessing");
        double cropPaddingPixels = RequiredReviewedDouble(
            preprocessing,
            "crop_padding_pixels",
            1d,
            "OCR recognition preprocessing");
        double cropVerticalContentPaddingRatio = RequiredReviewedDouble(
            preprocessing,
            "crop_vertical_content_padding_ratio",
            0.25d,
            "OCR recognition preprocessing");
        float cropPaddingValue = RequiredReviewedSingle(
            preprocessing,
            "crop_padding_value",
            1f,
            "OCR recognition preprocessing");
        RequireStringArray(
            preprocessing,
            "geometry_features",
            GeometryFeatures,
            "OCR recognition preprocessing");

        JsonElement postprocessing = RequiredObject(root, "postprocessing", "OCR recognition manifest");
        RequireString(
            postprocessing,
            "algorithm",
            RecognitionPostprocessingAlgorithm,
            "OCR recognition postprocessing");
        int maximumGlyphs = RequiredReviewedInt32(
            postprocessing,
            "maximum_glyphs",
            8,
            "OCR recognition postprocessing");
        float confidenceThreshold = RequiredReviewedSingle(
            postprocessing,
            "confidence_threshold",
            0.65f,
            "OCR recognition postprocessing");
        float structuralRejectMinimumHeightRatio = RequiredReviewedSingle(
            postprocessing,
            "structural_reject_minimum_height_ratio",
            0.75f,
            "OCR recognition postprocessing");
        RequireString(
            postprocessing,
            "grammar",
            "graph_numeric_v1",
            "OCR recognition postprocessing");

        var recognizer = new LocalOnnxComponentTextRecognizerOptions(identity, Alphabet)
        {
            CanvasWidth = canvasWidth,
            CanvasHeight = canvasHeight,
            GlyphWidth = glyphWidth,
            GlyphHeight = glyphHeight,
            GeometryFeatureCount = geometryFeatureCount,
            MaximumGlyphs = maximumGlyphs,
            RejectClassIndex = rejectClassIndex,
            ConfidenceThreshold = confidenceThreshold,
            StructuralRejectMinimumHeightRatio = structuralRejectMinimumHeightRatio,
            InputName = RequiredString(input, "name", "OCR recognition input"),
            OutputName = RequiredString(output, "name", "OCR recognition output"),
            StageVersion = identity.Version,
            AllowedProviders = [InferenceProvider.Cpu],
        };
        LocalOnnxComponentTextRecognizer.ValidateOptions(recognizer);
        return (
            recognizer,
            new OcrPipelineOptions
            {
                StageVersion = identity.Version,
                CropWidth = canvasWidth,
                CropHeight = canvasHeight,
                CropPaddingPixels = cropPaddingPixels,
                CropVerticalContentPaddingRatio = cropVerticalContentPaddingRatio,
                CropResizeMode = OcrCropResizeMode.PreserveAspectRatioPad,
                CropPaddingValue = cropPaddingValue,
            });
    }

    private static LocalOnnxTextRegionDetectorOptions ReadDetectionOptions(
        ResolvedProductionModel resolvedModel) =>
        ReadDetectionOptions(resolvedModel.Identity, resolvedModel.ManifestPath);

    internal static LocalOnnxTextRegionDetectorOptions ReadDetectionOptions(
        ModelIdentity identity,
        string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = document.RootElement;
        JsonElement input = SingleObject(root, "inputs", "OCR detection");
        JsonElement output = SingleObject(root, "outputs", "OCR detection");
        RequireString(input, "element_type", "float32", "OCR detection input");
        RequireString(input, "layout", "NCHW", "OCR detection input");
        RequireShape(input, "shape", ["1", "3", "H", "W"], "OCR detection input");
        RequireStringArray(input, "channels", ["b", "g", "r"], "OCR detection input");
        RequireString(output, "element_type", "float32", "OCR detection output");
        RequireString(output, "layout", "NCHW", "OCR detection output");
        RequireShape(output, "shape", ["1", "1", "H", "W"], "OCR detection output");
        RequireStringArray(output, "channels", ["text_probability"], "OCR detection output");
        string activation = RequiredString(output, "activation", "OCR detection output");
        OcrDetectionOutputActivation outputActivation = activation switch
        {
            "probability" => OcrDetectionOutputActivation.Probability,
            "probability_with_1e-5_clamp" => OcrDetectionOutputActivation.ProbabilityWithParityTolerance,
            "sigmoid_logit" => OcrDetectionOutputActivation.SigmoidLogit,
            _ => throw new InvalidDataException(
                "OCR detection output activation must be probability, probability_with_1e-5_clamp, or sigmoid_logit."),
        };

        JsonElement preprocessing = RequiredObject(root, "preprocessing", "OCR detection manifest");
        RequireString(preprocessing, "channel_order", "BGR", "OCR detection preprocessing");
        float[] means = RequiredReviewedSingles(
            preprocessing,
            "channel_means",
            [0.485f, 0.456f, 0.406f],
            "OCR detection preprocessing");
        float[] scales = RequiredReviewedSingles(
            preprocessing,
            "channel_scales",
            [1f / 0.229f, 1f / 0.224f, 1f / 0.225f],
            "OCR detection preprocessing");
        int maximumSideLength = RequiredReviewedInt32(
            preprocessing,
            "maximum_side_length",
            960,
            "OCR detection preprocessing");
        int dimensionMultiple = RequiredReviewedInt32(
            preprocessing,
            "dimension_multiple",
            128,
            "OCR detection preprocessing");

        JsonElement postprocessing = RequiredObject(root, "postprocessing", "OCR detection manifest");
        RequireString(
            postprocessing,
            "algorithm",
            DetectionPostprocessingAlgorithm,
            "OCR detection postprocessing");
        RequireString(postprocessing, "score_mode", "fast", "OCR detection postprocessing");
        var options = new LocalOnnxTextRegionDetectorOptions(identity)
        {
            MaximumSideLength = maximumSideLength,
            DimensionMultiple = dimensionMultiple,
            InputChannels = 3,
            InputLayout = OcrTensorLayout.ChannelsFirst,
            InputColorMode = OcrTensorColorMode.Bgr,
            ChannelMeans = means,
            ChannelScales = scales,
            InputName = RequiredString(input, "name", "OCR detection input"),
            OutputName = RequiredString(output, "name", "OCR detection output"),
            StageVersion = identity.Version,
            OutputActivation = outputActivation,
            PostprocessAlgorithm = OcrDetectionPostprocessAlgorithm.DbPostprocessV1,
            DbScoreMode = OcrDbScoreMode.FastMiniBox,
            ProbabilityThreshold = RequiredReviewedSingle(
                postprocessing,
                "probability_threshold",
                0.30f,
                "OCR detection postprocessing"),
            BoxConfidenceThreshold = RequiredReviewedSingle(
                postprocessing,
                "box_confidence_threshold",
                0.60f,
                "OCR detection postprocessing"),
            UnclipRatio = RequiredReviewedDouble(
                postprocessing,
                "unclip_ratio",
                1.5,
                "OCR detection postprocessing"),
            MinimumSideLength = RequiredReviewedInt32(
                postprocessing,
                "minimum_side_length",
                3,
                "OCR detection postprocessing"),
            MaximumRegions = RequiredReviewedInt32(
                postprocessing,
                "maximum_regions",
                1000,
                "OCR detection postprocessing"),
            AllowedProviders = [InferenceProvider.Cpu],
        };
        LocalOnnxTextRegionDetector.ValidateOptions(options);
        return options;
    }

    private static Task ValidateExecutablePairAsync(
        LocalOnnxTextRegionDetectorOptions detectorOptions,
        LocalOnnxComponentTextRecognizerOptions recognizerOptions,
        InferenceRuntime runtime,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detector = new LocalOnnxTextRegionDetector(
                runtime,
                detectorOptions with { BypassCache = true });
            const int detectorProbeSize = 32;
            var detectorImage = new OcrImage(
                detectorProbeSize,
                detectorProbeSize,
                detectorProbeSize,
                new byte[detectorProbeSize * detectorProbeSize],
                OcrSourceImage.Original,
                OcrFrameTransform.Identity,
                CanonicalOriginalWidth: detectorProbeSize,
                CanonicalOriginalHeight: detectorProbeSize,
                BgrPixels: new OcrBgrBytePixels(
                    detectorProbeSize * 3,
                    new byte[detectorProbeSize * detectorProbeSize * 3]));
            _ = await detector.DetectAsync(detectorImage, cancellationToken).ConfigureAwait(false);

            var recognizer = new LocalOnnxComponentTextRecognizer(
                runtime,
                recognizerOptions with { BypassCache = true });
            float[] pixels = Enumerable.Repeat(
                    1f,
                    checked(recognizerOptions.CanvasWidth * recognizerOptions.CanvasHeight))
                .ToArray();
            FillRectangle(pixels, recognizerOptions.CanvasWidth, 20, 8, 6, 16, 0f);
            FillRectangle(pixels, recognizerOptions.CanvasWidth, 40, 8, 5, 16, 0f);
            string cropSha256 = Convert.ToHexStringLower(
                SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan())));
            OcrPolygon polygon = OcrPolygon.FromRectangle(new OcrRectangle(
                0,
                0,
                recognizerOptions.CanvasWidth,
                recognizerOptions.CanvasHeight));
            var crop = new OcrCrop(
                "component-probe",
                OcrSourceImage.Original,
                recognizerOptions.CanvasWidth,
                recognizerOptions.CanvasHeight,
                pixels,
                cropSha256,
                polygon);
            IReadOnlyList<OcrRecognition> results = await recognizer
                .RecognizeBatchAsync([crop], cancellationToken)
                .ConfigureAwait(false);
            if (results.Count != 1 || results[0].Failure is not null)
            {
                OcrFailure? recognitionFailure = results.Count == 1 ? results[0].Failure : null;
                string failure = recognitionFailure is not null
                    ? $"{recognitionFailure.Code}: {recognitionFailure.TechnicalMessage}"
                    : $"expected one result, found {results.Count}";
                throw new InvalidDataException(
                    $"OCR component-recognition payload failed the CPU executable probe: {failure}.");
            }
        }, cancellationToken);

    private static void FillRectangle(
        float[] pixels,
        int stride,
        int left,
        int top,
        int width,
        int height,
        float value)
    {
        for (int y = top; y < top + height; y++)
        {
            for (int x = left; x < left + width; x++)
            {
                pixels[(y * stride) + x] = value;
            }
        }
    }

    private static void RequireTask(ResolvedProductionModel model, string expected)
    {
        if (!string.Equals(model.Task, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Resolved model task '{model.Task}' is not {expected}.");
        }
    }

    private static void RequireCpu(ResolvedProductionModel model)
    {
        if (!model.AvailableProviders.Contains(InferenceProvider.Cpu))
        {
            throw new InvalidDataException(
                $"Resolved OCR model '{model.Identity.ModelId}' lacks mandatory CPU approval.");
        }
    }

    private static JsonElement SingleObject(JsonElement root, string propertyName, string label)
    {
        JsonElement values = RequiredArray(root, propertyName, label);
        if (values.GetArrayLength() != 1 || values[0].ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} '{propertyName}' must contain exactly one object.");
        }

        return values[0];
    }

    private static JsonElement RequiredObject(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be an object.");
        }

        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be an array.");
        }

        return value;
    }

    private static string RequiredString(JsonElement parent, string propertyName, string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{label} field '{propertyName}' must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static void RequireString(
        JsonElement parent,
        string propertyName,
        string expected,
        string label)
    {
        string actual = RequiredString(parent, propertyName, label);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' must be '{expected}', found '{actual}'.");
        }
    }

    private static void RequireShape(
        JsonElement parent,
        string propertyName,
        IReadOnlyList<string> expected,
        string label)
    {
        JsonElement values = RequiredArray(parent, propertyName, label);
        string[] actual = values.EnumerateArray().Select(ShapeValue).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' must be [{string.Join(',', expected)}], " +
                $"found [{string.Join(',', actual)}].");
        }
    }

    private static string ShapeValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number when value.TryGetInt32(out int integer) =>
            integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException("Model tensor shape values must be strings or integers."),
    };

    private static void RequireStringArray(
        JsonElement parent,
        string propertyName,
        IReadOnlyList<string> expected,
        string label)
    {
        JsonElement values = RequiredArray(parent, propertyName, label);
        string[] actual = values.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : throw new InvalidDataException($"{label} field '{propertyName}' must contain strings."))
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' does not match the reviewed ordered values.");
        }
    }

    private static int RequiredReviewedInt32(
        JsonElement parent,
        string propertyName,
        int expected,
        string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int actual) ||
            actual != expected)
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' must match the reviewed value {expected}.");
        }

        return actual;
    }

    private static float RequiredReviewedSingle(
        JsonElement parent,
        string propertyName,
        float expected,
        string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetSingle(out float actual) ||
            actual != expected)
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' must match the reviewed value {expected:R}.");
        }

        return actual;
    }

    private static double RequiredReviewedDouble(
        JsonElement parent,
        string propertyName,
        double expected,
        string label)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double actual) ||
            actual != expected)
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' must match the reviewed value {expected:R}.");
        }

        return actual;
    }

    private static float[] RequiredReviewedSingles(
        JsonElement parent,
        string propertyName,
        IReadOnlyList<float> expected,
        string label)
    {
        JsonElement values = RequiredArray(parent, propertyName, label);
        float[] actual = values.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float parsed)
                ? parsed
                : throw new InvalidDataException($"{label} field '{propertyName}' must contain numbers."))
            .ToArray();
        if (actual.Length != expected.Count ||
            actual.Where((value, index) => value != expected[index]).Any())
        {
            throw new InvalidDataException(
                $"{label} field '{propertyName}' does not match the reviewed ordered values.");
        }

        return actual;
    }

    private static void VerifyChecksum(string path, string expectedSha256, string label)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"The checksum-resolved {label} is missing: {path}");
        }

        string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The checksum-resolved {label} changed after model-store validation.");
        }
    }

    private static string ValidateSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Value must be a SHA-256 hex digest.", parameterName);
        }

        return value.ToLowerInvariant();
    }
}
