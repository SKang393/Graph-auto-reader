// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;
using GraphReader.Inference;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

public sealed partial class ProductionOcrAdapter
{
    internal const string TiledProbabilityCandidateCompositionVersion =
        "v38-source-tiled-probability-v1";

    private const string TiledProbabilityAlgorithm =
        "v38-gray8-tiled-probability-components-v1";

    internal static async Task<ProductionOcrAdapter>
        CreateForFrozenTiledProbabilityCandidateEvaluationAsync(
            FrozenCandidateOcrModelDescriptor detectionModel,
            FrozenCandidateOcrModelDescriptor recognitionModel,
            ProductionInferenceRuntimeHost runtimeHost,
            string reviewedOpenCvRuntimeSha256,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detectionModel);
        ArgumentNullException.ThrowIfNull(recognitionModel);
        ArgumentNullException.ThrowIfNull(runtimeHost);
        cancellationToken.ThrowIfCancellationRequested();
        reviewedOpenCvRuntimeSha256 = ValidateSha256(
            reviewedOpenCvRuntimeSha256,
            nameof(reviewedOpenCvRuntimeSha256));

        await ValidateCandidateDescriptorAsync(
                detectionModel.Identity,
                detectionModel.ManifestPath,
                detectionModel.ManifestSha256,
                "ocr_detection",
                "Frozen tiled-probability OCR",
                cancellationToken)
            .ConfigureAwait(false);
        await ValidateCandidateDescriptorAsync(
                recognitionModel.Identity,
                recognitionModel.ManifestPath,
                recognitionModel.ManifestSha256,
                "ocr_recognition",
                "Frozen tiled-probability OCR",
                cancellationToken)
            .ConfigureAwait(false);
        RequireDistinctCandidatePayloads(detectionModel, recognitionModel);

        LocalOnnxTiledProbabilityTextRegionDetectorOptions detectorOptions =
            ReadTiledProbabilityDetectionOptions(
                detectionModel.Identity,
                detectionModel.ManifestPath);
        (LocalOnnxTextRecognizerOptions Recognizer, OcrPipelineOptions Pipeline) recognition =
            ReadRecognitionOptions(recognitionModel.Identity, recognitionModel.ManifestPath);
        RequireCpuOnlyManifest(recognitionModel.ManifestPath, "OCR recognition manifest");
        bool usesOfficialSpacingV2 = UsesOfficialRecognitionSpacingV2Manifest(
            recognitionModel.ManifestPath);

        InferenceRuntime runtime = runtimeHost.Runtime;
        await ValidateTiledProbabilityExecutablePairAsync(
                detectorOptions,
                recognition.Recognizer,
                runtime,
                cancellationToken)
            .ConfigureAwait(false);

        return new ProductionOcrAdapter(
            () =>
            {
                ITextRegionDetector detector =
                    new LocalOnnxTiledProbabilityTextRegionDetector(runtime, detectorOptions);
                ITextRecognizer recognizer = new LocalOnnxTextRecognizer(
                    runtime,
                    recognition.Recognizer);
                if (usesOfficialSpacingV2)
                {
                    recognizer = new OfficialRecognitionSpacingV2TextRecognizer(recognizer);
                }

                return new OcrPipeline(
                    detector,
                    recognizer,
                    new MemoryOcrResultCache(),
                    recognition.Pipeline);
            },
            detectionModel.Identity,
            recognitionModel.Identity,
            reviewedOpenCvRuntimeSha256);
    }

    private ProductionOcrAdapter(
        Func<OcrPipeline> pipelineFactory,
        ModelIdentity detectionModel,
        ModelIdentity recognitionModel,
        string reviewedOpenCvRuntimeSha256)
    {
        ArgumentNullException.ThrowIfNull(pipelineFactory);
        pipeline = new Lazy<OcrPipeline>(
            pipelineFactory,
            LazyThreadSafetyMode.ExecutionAndPublication);
        this.detectionModel = ValidateModel(detectionModel, nameof(detectionModel));
        this.recognitionModel = ValidateModel(recognitionModel, nameof(recognitionModel));
        detectionProvider = InferenceProvider.Cpu;
        recognitionProvider = InferenceProvider.Cpu;
        OpenCvRuntimeSha256 = ValidateSha256(
            reviewedOpenCvRuntimeSha256,
            nameof(reviewedOpenCvRuntimeSha256));
        modelInput = GraphStructureModelInput.Original;
        compositionVersion = TiledProbabilityCandidateCompositionVersion;
        configuredModels = new ProductionOcrConfigurationEvidence(
        [
            new ProductionOcrConfiguredModel(
                "ocr_detection",
                this.detectionModel,
                detectionProvider),
            new ProductionOcrConfiguredModel(
                "ocr_recognition",
                this.recognitionModel,
                recognitionProvider),
        ],
        ProductionOcrConfigurationScope.UnapprovedFrozenCandidate);
        IsApproved = false;
    }

    private static LocalOnnxTiledProbabilityTextRegionDetectorOptions
        ReadTiledProbabilityDetectionOptions(ModelIdentity identity, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = document.RootElement;
        RequireCpuOnlyManifest(root, "Tiled OCR detection manifest");

        JsonElement input = SingleObject(root, "inputs", "Tiled OCR detection");
        RequireExactProperties(
            input,
            ["name", "element_type", "layout", "shape", "channels"],
            "Tiled OCR detection input");
        RequireString(
            input,
            "name",
            LocalOnnxTiledProbabilityTextRegionDetector.InputName,
            "Tiled OCR detection input");
        RequireString(input, "element_type", "float32", "Tiled OCR detection input");
        RequireString(input, "layout", "NCHW", "Tiled OCR detection input");
        RequireShape(
            input,
            "shape",
            ["tile_count", "1", "256", "256"],
            "Tiled OCR detection input");
        RequireStringArray(input, "channels", ["gray"], "Tiled OCR detection input");

        JsonElement output = SingleObject(root, "outputs", "Tiled OCR detection");
        RequireExactProperties(
            output,
            ["name", "element_type", "layout", "shape", "channels", "activation"],
            "Tiled OCR detection output");
        RequireString(
            output,
            "name",
            LocalOnnxTiledProbabilityTextRegionDetector.OutputName,
            "Tiled OCR detection output");
        RequireString(output, "element_type", "float32", "Tiled OCR detection output");
        RequireString(output, "layout", "NCHW", "Tiled OCR detection output");
        RequireShape(
            output,
            "shape",
            ["tile_count", "1", "256", "256"],
            "Tiled OCR detection output");
        RequireStringArray(output, "channels", ["text_logit"], "Tiled OCR detection output");
        RequireString(output, "activation", "sigmoid_logit", "Tiled OCR detection output");

        JsonElement preprocessing = RequiredObject(
            root,
            "preprocessing",
            "Tiled OCR detection manifest");
        RequireExactProperties(
            preprocessing,
            [
                "source_image", "tile_order", "tile_size", "tile_overlap", "tile_step",
                "partial_tile_padding", "normalization",
            ],
            "Tiled OCR detection preprocessing");
        RequireString(
            preprocessing,
            "source_image",
            "immutable_original_gray8",
            "Tiled OCR detection preprocessing");
        RequireString(
            preprocessing,
            "tile_order",
            "row-major",
            "Tiled OCR detection preprocessing");
        _ = RequiredReviewedInt32(
            preprocessing,
            "tile_size",
            LocalOnnxTiledProbabilityTextRegionDetector.TileSize,
            "Tiled OCR detection preprocessing");
        _ = RequiredReviewedInt32(
            preprocessing,
            "tile_overlap",
            LocalOnnxTiledProbabilityTextRegionDetector.TileOverlap,
            "Tiled OCR detection preprocessing");
        _ = RequiredReviewedInt32(
            preprocessing,
            "tile_step",
            LocalOnnxTiledProbabilityTextRegionDetector.TileStep,
            "Tiled OCR detection preprocessing");
        RequireString(
            preprocessing,
            "partial_tile_padding",
            "white-255-top-left-valid",
            "Tiled OCR detection preprocessing");
        RequireString(
            preprocessing,
            "normalization",
            "1-gray/255-float32",
            "Tiled OCR detection preprocessing");

        JsonElement postprocessing = RequiredObject(
            root,
            "postprocessing",
            "Tiled OCR detection manifest");
        RequireExactProperties(
            postprocessing,
            [
                "algorithm", "overlap_merge", "probability_threshold", "morphology",
                "connectivity", "minimum_component_area", "minimum_side_length",
                "maximum_tile_count", "rectangle_bounds",
            ],
            "Tiled OCR detection postprocessing");
        RequireString(
            postprocessing,
            "algorithm",
            TiledProbabilityAlgorithm,
            "Tiled OCR detection postprocessing");
        RequireString(
            postprocessing,
            "overlap_merge",
            "valid-region-float32-mean",
            "Tiled OCR detection postprocessing");
        _ = RequiredReviewedSingle(
            postprocessing,
            "probability_threshold",
            LocalOnnxTiledProbabilityTextRegionDetector.ProbabilityThreshold,
            "Tiled OCR detection postprocessing");
        RequireString(
            postprocessing,
            "morphology",
            "global-binary-close-3x3-once",
            "Tiled OCR detection postprocessing");
        _ = RequiredReviewedInt32(
            postprocessing,
            "connectivity",
            8,
            "Tiled OCR detection postprocessing");
        _ = RequiredReviewedInt32(
            postprocessing,
            "minimum_component_area",
            LocalOnnxTiledProbabilityTextRegionDetector.MinimumComponentArea,
            "Tiled OCR detection postprocessing");
        _ = RequiredReviewedInt32(
            postprocessing,
            "minimum_side_length",
            LocalOnnxTiledProbabilityTextRegionDetector.MinimumSideLength,
            "Tiled OCR detection postprocessing");
        _ = RequiredReviewedInt32(
            postprocessing,
            "maximum_tile_count",
            LocalOnnxTiledProbabilityTextRegionDetector.MaximumTileCount,
            "Tiled OCR detection postprocessing");
        RequireString(
            postprocessing,
            "rectangle_bounds",
            "half-open-original-pixel",
            "Tiled OCR detection postprocessing");

        return new LocalOnnxTiledProbabilityTextRegionDetectorOptions(identity)
        {
            StageVersion = TiledProbabilityCandidateCompositionVersion,
            AllowedProviders = [InferenceProvider.Cpu],
        };
    }

    private static void RequireDistinctCandidatePayloads(
        FrozenCandidateOcrModelDescriptor detectionModel,
        FrozenCandidateOcrModelDescriptor recognitionModel)
    {
        if (string.Equals(
                detectionModel.Identity.Sha256,
                recognitionModel.Identity.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Path.GetFullPath(detectionModel.Identity.FilePath),
                Path.GetFullPath(recognitionModel.Identity.FilePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Frozen tiled-probability OCR evaluation requires distinct pinned detection and recognition payloads.");
        }
    }

    private static void RequireCpuOnlyManifest(string manifestPath, string label)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        RequireCpuOnlyManifest(document.RootElement, label);
    }

    private static void RequireCpuOnlyManifest(JsonElement root, string label) =>
        RequireStringArray(root, "providers", ["cpu"], label);

    private static void RequireExactProperties(
        JsonElement value,
        IReadOnlyList<string> expected,
        string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} must be an object.");
        }

        string[] actual = value.EnumerateObject().Select(static property => property.Name).ToArray();
        if (actual.Length != actual.Distinct(StringComparer.Ordinal).Count() ||
            !actual.Order(StringComparer.Ordinal).SequenceEqual(
                expected.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"{label} fields must exactly match [{string.Join(',', expected)}].");
        }
    }

    private static Task ValidateTiledProbabilityExecutablePairAsync(
        LocalOnnxTiledProbabilityTextRegionDetectorOptions detectorOptions,
        LocalOnnxTextRecognizerOptions recognizerOptions,
        InferenceRuntime runtime,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detector = new LocalOnnxTiledProbabilityTextRegionDetector(
                runtime,
                detectorOptions with { BypassCache = true });
            const int probeSize = 32;
            var original = new OcrImage(
                probeSize,
                probeSize,
                probeSize,
                new byte[probeSize * probeSize],
                OcrSourceImage.Original,
                OcrFrameTransform.Identity,
                CanonicalOriginalWidth: probeSize,
                CanonicalOriginalHeight: probeSize);
            var alignedDerivative = original with
            {
                Pixels = Enumerable.Repeat((byte)255, probeSize * probeSize).ToArray(),
            };
            _ = await detector
                .DetectAsync(original, alignedDerivative, cancellationToken)
                .ConfigureAwait(false);

            await ValidateRecognizerExecutableAsync(
                    recognizerOptions,
                    runtime,
                    cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken);

    private static async Task ValidateRecognizerExecutableAsync(
        LocalOnnxTextRecognizerOptions recognizerOptions,
        InferenceRuntime runtime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recognizer = new LocalOnnxTextRecognizer(
            runtime,
            recognizerOptions with { BypassCache = true });
        var cropPixels = new float[checked(
            recognizerOptions.InputWidth * recognizerOptions.InputHeight)];
        OcrBgrFloatPixels? bgrCropPixels = recognizerOptions.InputColorMode == OcrTensorColorMode.Bgr
            ? new OcrBgrFloatPixels(
                recognizerOptions.InputWidth * 3,
                new float[checked(cropPixels.Length * 3)])
            : null;
        string cropSha256 = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                new byte[checked(cropPixels.Length * sizeof(float))]));
        OcrPolygon polygon = OcrPolygon.FromRectangle(new OcrRectangle(
            0,
            0,
            recognizerOptions.InputWidth,
            recognizerOptions.InputHeight));
        OcrCrop[] crops =
        [
            new("probe-1", OcrSourceImage.Original, recognizerOptions.InputWidth,
                recognizerOptions.InputHeight, cropPixels, cropSha256, polygon, bgrCropPixels),
            new("probe-2", OcrSourceImage.Original, recognizerOptions.InputWidth,
                recognizerOptions.InputHeight, cropPixels, cropSha256, polygon, bgrCropPixels),
        ];
        IReadOnlyList<OcrRecognition> results = await recognizer
            .RecognizeBatchAsync(crops, cancellationToken)
            .ConfigureAwait(false);
        if (results.Count != crops.Length || results.Any(static result => result.Failure is not null))
        {
            string failures = string.Join(
                ", ",
                results.Where(static result => result.Failure is not null)
                    .Select(static result =>
                        $"{result.Failure!.Code}: {result.Failure.TechnicalMessage}"));
            throw new InvalidDataException(
                $"OCR recognition payload failed the two-item CPU executable probe: {failures}.");
        }
    }
}
