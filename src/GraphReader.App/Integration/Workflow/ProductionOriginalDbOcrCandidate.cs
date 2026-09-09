// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using GraphReader.Inference;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

public sealed partial class ProductionOcrAdapter
{
    internal const string OriginalDbCandidateCompositionVersion = "original-db-head-candidate-v1";

    internal static async Task<ProductionOcrAdapter> CreateForFrozenDbHeadCandidateEvaluationAsync(
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
        reviewedOpenCvRuntimeSha256 = ValidateSha256(reviewedOpenCvRuntimeSha256,
            nameof(reviewedOpenCvRuntimeSha256));
        await ValidateCandidateDescriptorAsync(detectionModel.Identity, detectionModel.ManifestPath,
            detectionModel.ManifestSha256, "ocr_detection", "Frozen DB-head OCR", cancellationToken)
            .ConfigureAwait(false);
        await ValidateCandidateDescriptorAsync(recognitionModel.Identity, recognitionModel.ManifestPath,
            recognitionModel.ManifestSha256, "ocr_recognition", "Frozen DB-head OCR", cancellationToken)
            .ConfigureAwait(false);
        RequireDistinctCandidatePayloads(detectionModel, recognitionModel);
        RequireCpuOnlyManifest(detectionModel.ManifestPath, "DB-head detection manifest");
        RequireCpuOnlyManifest(recognitionModel.ManifestPath, "OCR recognition manifest");
        LocalOnnxTextRegionDetectorOptions options = ReadDetectionOptions(
            detectionModel.Identity, detectionModel.ManifestPath);
        if (options.MaximumSideLength != 960 || options.DimensionMultiple != 128 ||
            options.InputColorMode != OcrTensorColorMode.Bgr || options.InputName != "x" ||
            options.OutputName != "fetch_name_0" ||
            options.OutputActivation != OcrDetectionOutputActivation.ProbabilityWithParityTolerance)
        {
            throw new InvalidDataException("DB-head candidate requires the fixed original PP-OCRv5 tensor contract.");
        }
        (LocalOnnxTextRecognizerOptions Recognizer, OcrPipelineOptions Pipeline) recognition =
            ReadRecognitionOptions(recognitionModel.Identity, recognitionModel.ManifestPath);
        bool spacing = UsesOfficialRecognitionSpacingV2Manifest(recognitionModel.ManifestPath);
        InferenceRuntime runtime = runtimeHost.Runtime;
        await Task.Run(async () =>
        {
            var detector = new LocalOnnxTextRegionDetector(runtime, options with { BypassCache = true });
            var original = new OcrImage(32, 32, 32, new byte[32 * 32], OcrSourceImage.Original,
                OcrFrameTransform.Identity, CanonicalOriginalWidth: 32, CanonicalOriginalHeight: 32,
                BgrPixels: new OcrBgrBytePixels(32 * 3, new byte[32 * 32 * 3]));
            _ = await detector.DetectAsync(original, cancellationToken).ConfigureAwait(false);
            await ValidateRecognizerExecutableAsync(recognition.Recognizer, runtime, cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return new ProductionOcrAdapter(() =>
        {
            ITextRegionDetector detector = new OriginalDbInputDetector(
                new LocalOnnxTextRegionDetector(runtime, options));
            ITextRecognizer recognizer = new LocalOnnxTextRecognizer(runtime, recognition.Recognizer);
            if (spacing)
            {
                recognizer = new OfficialRecognitionSpacingV2TextRecognizer(recognizer);
            }
            return new OcrPipeline(detector, recognizer, new MemoryOcrResultCache(), recognition.Pipeline);
        }, detectionModel.Identity, recognitionModel.Identity, reviewedOpenCvRuntimeSha256,
            OriginalDbCandidateCompositionVersion);
    }

    internal sealed class OriginalDbInputDetector(ITextRegionDetector detector) : IDualInputTextRegionDetector
    {
        private readonly ITextRegionDetector inner = detector ?? throw new ArgumentNullException(nameof(detector));

        public string ConfigurationFingerprint => $"{OriginalDbCandidateCompositionVersion}:{inner.ConfigurationFingerprint}";

        public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(OcrImage image,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(image);
            cancellationToken.ThrowIfCancellationRequested();
            if (image.SourceImage != OcrSourceImage.Original)
            {
                throw new InvalidDataException("DB-head candidate requires immutable original pixels.");
            }
            return inner.DetectAsync(image, cancellationToken);
        }

        public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(OcrImage originalImage,
            OcrImage detectorImage, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(originalImage);
            ArgumentNullException.ThrowIfNull(detectorImage);
            if (originalImage.Width != detectorImage.Width || originalImage.Height != detectorImage.Height ||
                originalImage.CanonicalOriginalWidth != detectorImage.CanonicalOriginalWidth ||
                originalImage.CanonicalOriginalHeight != detectorImage.CanonicalOriginalHeight ||
                originalImage.OriginalToImage != detectorImage.OriginalToImage ||
                !string.Equals(originalImage.CoordinateSpace, detectorImage.CoordinateSpace,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("OCR derivative is not aligned to its original image.");
            }
            return DetectAsync(originalImage, cancellationToken);
        }
    }
}
