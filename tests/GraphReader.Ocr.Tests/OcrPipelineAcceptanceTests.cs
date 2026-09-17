// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Globalization;
using System.Security.Cryptography;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class OcrPipelineAcceptanceTests
{
    private static readonly int[] ExpectedBatchSizes = [2, 2, 1];

    [TestMethod]
    public async Task TinyAndFadedNumericCropsRemainVisibleWithHonestConfidence()
    {
        OcrDetectedRegion[] regions =
        [
            OcrTestFixtures.Region("tiny", 18, 43, 3, 5, confidence: 0.88,
                context: new OcrRegionContext(NumericExpected: true)),
            OcrTestFixtures.Region("faded", 19, 57, 7, 5, confidence: 0.58,
                context: new OcrRegionContext(NumericExpected: true)),
        ];
        var recognizer = Recognizer(
            ("tiny", OcrSourceImage.Original, "1", 0.82),
            ("faded", OcrSourceImage.Original, "50", 0.56));
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([]),
            recognizer,
            new InMemoryOcrResultCache(),
            batchSize: 8);

        OcrResult result = await pipeline.RecognizeAsync(OcrTestFixtures.Request(regions), CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.AreEqual("1", result.Regions.Single(region => region.RegionId == "tiny").Text);
        OcrRegion faded = result.Regions.Single(region => region.RegionId == "faded");
        Assert.AreEqual("50", faded.Text);
        Assert.IsGreaterThan(0d, faded.Confidence);
        Assert.IsLessThan(0.8d, faded.Confidence);
    }

    [TestMethod]
    public async Task TallHorizontalRegionCanPreserveDetectorOrientation()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "tall-horizontal-digit",
            45,
            30,
            4,
            12,
            context: new OcrRegionContext(NumericExpected: true));
        OcrRequest request = OcrTestFixtures.Request([region]);
        var cropOptions = new OcrCropBatcherOptions
        {
            TargetWidth = 128,
            TargetHeight = 32,
            BatchSize = 16,
            HorizontalPaddingPixels = 12,
            VerticalPaddingPixels = 1,
            VerticalContentPaddingRatio = 0.25,
            ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad,
            PaddingValue = 1f,
        };
        string horizontalHash = OcrCropBatcher.CreateBatches(
            request.OriginalImage,
            [region],
            cropOptions)[0][0].CropSha256;
        string rotatedHash = OcrCropBatcher.CreateBatches(
            request.OriginalImage,
            [region with { OrientationDegrees = -90d }],
            cropOptions)[0][0].CropSha256;
        string? observedHash = null;
        var recognizer = new StubTextRecognizer((crops, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            observedHash = crops.Single().CropSha256;
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(
            [
                new OcrRecognition(
                    crops[0].RegionId,
                    crops[0].SourceImage,
                    [new OcrRecognitionAlternative("1", 0.99, crops[0].SourceImage)],
                    0.25),
            ]);
        });
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([]),
            recognizer,
            new InMemoryOcrResultCache(),
            new OcrPipelineOptions
            {
                StageVersion = "horizontal-tight-box-test",
                CropWidth = cropOptions.TargetWidth,
                CropHeight = cropOptions.TargetHeight,
                BatchSize = cropOptions.BatchSize,
                CropHorizontalPaddingPixels = cropOptions.HorizontalPaddingPixels,
                CropVerticalPaddingPixels = cropOptions.VerticalPaddingPixels,
                CropVerticalContentPaddingRatio = cropOptions.VerticalContentPaddingRatio,
                CropResizeMode = cropOptions.ResizeMode,
                CropPaddingValue = cropOptions.PaddingValue,
                InferVerticalOrientationForTallRegions = false,
            });

        OcrResult result = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.AreNotEqual(rotatedHash, horizontalHash);
        Assert.AreEqual(horizontalHash, observedHash);
    }

    [TestMethod]
    public async Task CropRecognitionUsesBoundedBatchesAndPreservesInputOrder()
    {
        OcrDetectedRegion[] regions = Enumerable.Range(0, 5)
            .Select(index => OcrTestFixtures.Region(
                $"region-{index}",
                38 + (index * 15),
                88,
                9,
                6,
                context: new OcrRegionContext(NumericExpected: true)))
            .ToArray();
        var recognizer = Recognizer(regions.Select((region, index) =>
            (region.RegionId, OcrSourceImage.Original, index.ToString(CultureInfo.InvariantCulture), 0.9d)).ToArray());
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([]),
            recognizer,
            new InMemoryOcrResultCache(),
            batchSize: 2);

        OcrResult result = await pipeline.RecognizeAsync(OcrTestFixtures.Request(regions), CancellationToken.None);

        CollectionAssert.AreEqual(ExpectedBatchSizes, recognizer.BatchSizes.ToArray());
        CollectionAssert.AreEqual(
            regions.Select(static region => region.RegionId).ToArray(),
            result.Regions.Select(static region => region.RegionId).ToArray());
        Assert.AreEqual(3, result.Cache.BatchCount);
        Assert.AreEqual(5, result.Cache.CropCount);
    }

    [TestMethod]
    public async Task WarmRepeatUsesCacheWithoutDetectionOrRecognition()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "cached",
            42,
            88,
            10,
            6,
            context: new OcrRegionContext(NumericExpected: true));
        var detector = new StubTextRegionDetector([region]);
        var recognizer = Recognizer(("cached", OcrSourceImage.Original, "10", 0.94));
        var cache = new InMemoryOcrResultCache();
        var pipeline = new OcrPipeline(detector, recognizer, cache, batchSize: 4);
        OcrRequest request = OcrTestFixtures.Request();

        OcrResult cold = await pipeline.RecognizeAsync(request, CancellationToken.None);
        OcrResult warm = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.IsFalse(cold.Cache.CacheHit);
        Assert.IsTrue(warm.Cache.CacheHit);
        Assert.AreEqual(1, detector.CallCount);
        Assert.AreEqual(1, recognizer.CallCount);
        Assert.AreEqual(2, cache.ReadCount);
        Assert.AreEqual(1, cache.WriteCount);
        CollectionAssert.AreEqual(cold.Regions.ToArray(), warm.Regions.ToArray());
    }

    [TestMethod]
    public async Task DetectorOnlyDerivativeDoesNotReplaceImmutableRecognitionPixels()
    {
        const int width = 40;
        const int height = 10;
        var original = new OcrImage(
            width,
            height,
            width,
            Enumerable.Repeat((byte)255, width * height).ToArray(),
            OcrSourceImage.Original,
            OcrFrameTransform.Identity);
        var masked = new OcrImage(
            width,
            height,
            width,
            new byte[width * height],
            OcrSourceImage.Original,
            OcrFrameTransform.Identity);
        string maskedSha256 = Convert.ToHexStringLower(SHA256.HashData(masked.Pixels.Span));
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "detector-only",
            0,
            0,
            width,
            height,
            context: new OcrRegionContext(NumericExpected: true));
        var detector = new StubTextRegionDetector((image, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsTrue(image.Pixels.Span.ToArray().All(static value => value == 0));
            return ValueTask.FromResult<IReadOnlyList<OcrDetectedRegion>>([region]);
        });
        var recognizer = new StubTextRecognizer((crops, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.HasCount(1, crops);
            Assert.AreEqual(OcrSourceImage.Original, crops[0].SourceImage);
            Assert.IsTrue(crops[0].Pixels.Span.ToArray().All(static value => value == 1f));
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>([
                new OcrRecognition(
                    crops[0].RegionId,
                    crops[0].SourceImage,
                    [new OcrRecognitionAlternative("10", 0.95, crops[0].SourceImage)],
                    0.1),
            ]);
        });
        var pipeline = new OcrPipeline(
            detector,
            recognizer,
            new InMemoryOcrResultCache(),
            new OcrPipelineOptions
            {
                CropWidth = width,
                CropHeight = height,
                CropPaddingPixels = 0,
            });
        var request = new OcrRequest(
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            new string('a', 64),
            original,
            new OcrRectangle(0, 0, width, height),
            DetectorImage: new OcrDetectorImage(masked, maskedSha256));

        OcrResult result = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.AreEqual(1, detector.CallCount);
        Assert.AreEqual(1, recognizer.CallCount);
        Assert.AreEqual("10", result.Regions[0].Text);
    }

    [TestMethod]
    public async Task DetectorOnlyDerivativeRequiresMatchingPixelChecksum()
    {
        OcrImage original = OcrTestFixtures.Image(width: 40, height: 10);
        OcrImage masked = original with { Pixels = new byte[400] };
        var detector = new StubTextRegionDetector([]);
        var pipeline = new OcrPipeline(
            detector,
            new StubTextRecognizer((_, _) => throw new AssertFailedException("Recognizer must not run.")),
            new InMemoryOcrResultCache());
        OcrRequest request = OcrTestFixtures.Request() with
        {
            OriginalImage = original,
            PlotBounds = new OcrRectangle(0, 0, 40, 10),
            DetectorImage = new OcrDetectorImage(masked, new string('f', 64)),
        };

        OcrResult result = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.AreEqual("OCR_INPUT_INVALID", result.Failure?.Code);
        Assert.AreEqual(0, detector.CallCount);
    }

    [TestMethod]
    public async Task DetectorOnlyDerivativeRequiresOriginalPixelGeometry()
    {
        OcrImage original = OcrTestFixtures.Image(width: 40, height: 10);
        OcrImage misaligned = original with
        {
            Pixels = new byte[400],
            OriginalToImage = new OcrFrameTransform(2, 2, 0, 0),
        };
        string checksum = Convert.ToHexStringLower(SHA256.HashData(misaligned.Pixels.Span));
        var detector = new StubTextRegionDetector([]);
        var pipeline = new OcrPipeline(
            detector,
            new StubTextRecognizer((_, _) => throw new AssertFailedException("Recognizer must not run.")),
            new InMemoryOcrResultCache());
        OcrRequest request = OcrTestFixtures.Request() with
        {
            OriginalImage = original,
            PlotBounds = new OcrRectangle(0, 0, 40, 10),
            DetectorImage = new OcrDetectorImage(misaligned, checksum),
        };

        OcrResult result = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.AreEqual("OCR_INPUT_INVALID", result.Failure?.Code);
        Assert.AreEqual(0, detector.CallCount);
    }

    [TestMethod]
    public async Task CacheKeyInvalidatesWhenTransformChainChanges()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("number", 42, 88, 10, 6);
        var detector = new StubTextRegionDetector([region]);
        var recognizer = Recognizer(("number", OcrSourceImage.Original, "10", 0.94));
        var pipeline = new OcrPipeline(detector, recognizer, new InMemoryOcrResultCache(), batchSize: 4);
        OcrRequest identity = OcrTestFixtures.Request() with { TransformChain = "identity" };
        OcrRequest deskewed = identity with { TransformChain = "deskew:1.5" };

        OcrResult first = await pipeline.RecognizeAsync(identity, CancellationToken.None);
        OcrResult second = await pipeline.RecognizeAsync(deskewed, CancellationToken.None);

        Assert.IsFalse(first.Cache.CacheHit);
        Assert.IsFalse(second.Cache.CacheHit);
        Assert.AreNotEqual(first.Cache.CacheKey, second.Cache.CacheKey);
        Assert.AreEqual(2, detector.CallCount);
        Assert.AreEqual(2, recognizer.CallCount);
    }

    [TestMethod]
    public async Task OriginalAndEnhancedAlternativesAreRetainedButMasksUseOriginalPixels()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "ambiguous",
            20,
            30,
            12,
            8,
            context: new OcrRegionContext(NumericExpected: true));
        OcrImage enhanced = OcrTestFixtures.Image(
            OcrSourceImage.Enhanced,
            width: 320,
            height: 200,
            transform: new OcrFrameTransform(2, 2, 0, 0));
        var recognizer = Recognizer(
            ("ambiguous", OcrSourceImage.Original, "10O", 0.63),
            ("ambiguous", OcrSourceImage.Enhanced, "100", 0.96));
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([]),
            recognizer,
            new InMemoryOcrResultCache(),
            batchSize: 4);

        OcrResult result = await pipeline.RecognizeAsync(
            OcrTestFixtures.Request([region], enhanced),
            CancellationToken.None);

        Assert.HasCount(1, result.Regions);
        OcrRegion output = result.Regions[0];
        Assert.AreEqual("100", output.Text);
        Assert.AreEqual(OcrSourceImage.Enhanced, output.SourceImage);
        CollectionAssert.AreEquivalent(
            new[] { OcrSourceImage.Original, OcrSourceImage.Enhanced },
            output.Alternatives.Select(static alternative => alternative.SourceImage).Distinct().ToArray());
        Assert.HasCount(1, result.Masks);
        OcrMask mask = result.Masks[0];
        Assert.AreEqual(OcrContract.CoordinateSpace, mask.CoordinateSpace);
        Assert.AreEqual(new OcrRectangle(19, 29, 14, 10), mask.Polygon.Bounds);
        Assert.IsTrue(output.Polygon.Points.All(point => point.X <= 32 && point.Y <= 38));
    }

    [TestMethod]
    public async Task GeneralizationProducesAnnotationRegionAndMarkerExclusionMask()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "generalization",
            78,
            40,
            50,
            9,
            context: new OcrRegionContext(NearAnnotationArrow: true));
        var recognizer = Recognizer(
            ("generalization", OcrSourceImage.Original, "Generalization", 0.97));
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([]),
            recognizer,
            new InMemoryOcrResultCache(),
            batchSize: 4);

        OcrResult result = await pipeline.RecognizeAsync(OcrTestFixtures.Request([region]), CancellationToken.None);

        Assert.HasCount(1, result.Regions);
        OcrRegion output = result.Regions[0];
        Assert.AreEqual(OcrTextRole.Annotation, output.Role);
        Assert.HasCount(1, result.Masks);
        OcrMask mask = result.Masks[0];
        Assert.AreEqual("generalization", mask.RegionId);
        Assert.AreEqual(OcrContract.CoordinateSpace, mask.CoordinateSpace);
    }

    [TestMethod]
    public async Task ParticipantLaneAssemblyRecognizesOneUnionCropAndUsesParticipantCue()
    {
        OcrResult result = await RecognizeParticipantLaneUnionAsync("Participant 01");

        Assert.HasCount(1, result.Regions);
        Assert.AreEqual("Participant 01", result.Regions[0].Text);
        Assert.AreEqual(OcrTextRole.Participant, result.Regions[0].Role);
        Assert.AreEqual(new OcrRectangle(2, 35, 25, 10), result.Regions[0].Polygon.Bounds);
    }

    [TestMethod]
    public async Task ParticipantLaneAssemblyKeepsNumericUnionClassifiedAsYTick()
    {
        OcrResult result = await RecognizeParticipantLaneUnionAsync("10");

        Assert.HasCount(1, result.Regions);
        Assert.AreEqual("10", result.Regions[0].Text);
        Assert.AreEqual(OcrTextRole.YTick, result.Regions[0].Role);
    }

    [TestMethod]
    public async Task OutputMatchesFrozenEnvelopeAndReportsTimingAndConfidence()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "participant",
            62,
            2,
            48,
            10,
            context: new OcrRegionContext(InParticipantBand: true));
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([]),
            Recognizer(("participant", OcrSourceImage.Original, "Chandler", 0.93)),
            new InMemoryOcrResultCache(),
            batchSize: 4);
        OcrRequest request = OcrTestFixtures.Request([region]);

        OcrResult result = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.AreEqual(1, result.ContractVersion);
        Assert.AreEqual(OcrContract.Stage, result.Stage);
        Assert.AreEqual(OcrContract.CoordinateSpace, result.CoordinateSpace);
        Assert.AreEqual(request.ProjectId, result.ProjectId);
        Assert.AreEqual(request.PanelId, result.PanelId);
        Assert.AreEqual(request.InputSha256, result.InputSha256);
        Assert.IsTrue(Guid.TryParse(result.RunId, out _));
        Assert.IsTrue(result.Timing.PreprocessMilliseconds >= 0);
        Assert.IsTrue(result.Timing.InferenceMilliseconds >= 0);
        Assert.IsTrue(result.Timing.PostprocessMilliseconds >= 0);
        Assert.IsGreaterThanOrEqualTo(
            result.Timing.PreprocessMilliseconds + result.Timing.InferenceMilliseconds +
            result.Timing.PostprocessMilliseconds,
            result.Timing.TotalMilliseconds + 0.01);
        Assert.IsTrue(result.Confidence is >= 0 and <= 1);
        Assert.HasCount(1, result.Regions);
        Assert.AreEqual(OcrTextRole.Participant, result.Regions[0].Role);
        Assert.IsNull(result.Failure);
    }

    [TestMethod]
    public async Task RecognitionFailureReturnsStructuredRecoverableFailure()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("failure", 40, 40, 20, 8);
        var failure = new OcrFailure(
            "MODEL_NOT_FOUND",
            "error",
            "Errors.ModelNotFound",
            "Fixture model is unavailable.",
            true,
            "select_manual_mode");
        var recognizer = new StubTextRecognizer((crops, _) =>
            ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(
                crops.Select(crop => new OcrRecognition(
                    crop.RegionId,
                    crop.SourceImage,
                    [],
                    0,
                    failure)).ToArray()));
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([]),
            recognizer,
            new InMemoryOcrResultCache(),
            batchSize: 4);

        OcrResult result = await pipeline.RecognizeAsync(OcrTestFixtures.Request([region]), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual("MODEL_NOT_FOUND", result.Failure.Code);
        Assert.AreEqual("Errors.ModelNotFound", result.Failure.UserMessageKey);
        Assert.IsTrue(result.Failure.Recoverable);
        Assert.AreEqual("select_manual_mode", result.Failure.SuggestedAction);
    }

    [TestMethod]
    public async Task RecoverableRecognitionFailureIsNotCachedAndIdenticalRetrySucceeds()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("transient", 40, 40, 20, 8);
        var failure = new OcrFailure(
            "OCR_RECOGNITION_FAILED",
            "error",
            "Errors.OcrRecognitionFailed",
            "Transient fixture failure.",
            true,
            "retry");
        var attempts = 0;
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            attempts++;
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                attempts == 1
                    ? new OcrRecognition(crop.RegionId, crop.SourceImage, [], 0.1, failure)
                    : new OcrRecognition(
                        crop.RegionId,
                        crop.SourceImage,
                        [new OcrRecognitionAlternative("10", 0.99, crop.SourceImage)],
                        0.1)).ToArray());
        });
        var cache = new InMemoryOcrResultCache();
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([]),
            recognizer,
            cache,
            batchSize: 4);
        OcrRequest request = OcrTestFixtures.Request([region]);

        OcrResult failed = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.IsFalse(failed.Succeeded);
        Assert.AreEqual("OCR_RECOGNITION_FAILED", failed.Failure?.Code);
        Assert.IsFalse(failed.Cache.CacheHit);
        Assert.IsFalse(failed.Cache.RecognitionCacheHit);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failed.Cache.CacheKey));
        Assert.IsFalse(string.IsNullOrWhiteSpace(failed.Cache.RecognitionCacheKey));
        Assert.AreEqual(1, failed.Cache.CropCount);
        Assert.AreEqual(1, failed.Cache.BatchCount);
        CollectionAssert.Contains(
            failed.Warnings.ToArray(),
            "ocr_recognition_cache_skipped_due_to_failure");
        Assert.AreEqual(0, cache.WriteCount);
        Assert.AreEqual(0, cache.RecognitionWriteCount);

        OcrResult retried = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.IsTrue(retried.Succeeded, retried.Failure?.TechnicalMessage);
        Assert.AreEqual(2, recognizer.CallCount);
        Assert.IsFalse(retried.Cache.CacheHit);
        Assert.IsFalse(retried.Cache.RecognitionCacheHit);
        Assert.IsFalse(string.IsNullOrWhiteSpace(retried.Cache.RecognitionCacheKey));
        Assert.AreEqual(2, cache.RecognitionReadCount);
        Assert.AreEqual(1, cache.WriteCount);
        Assert.AreEqual(1, cache.RecognitionWriteCount);
        Assert.HasCount(1, retried.Regions);
        Assert.AreEqual("10", retried.Regions[0].Text);
    }

    [TestMethod]
    public async Task CancellationStopsRecognitionAndDoesNotPopulateCache()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("cancel", 40, 40, 20, 8);
        using var cancellation = new CancellationTokenSource();
        var cache = new InMemoryOcrResultCache();
        var recognizer = new StubTextRecognizer((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>([]);
        });
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([]),
            recognizer,
            cache,
            batchSize: 4);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => pipeline.RecognizeAsync(OcrTestFixtures.Request([region]), cancellation.Token).AsTask());
        Assert.AreEqual(0, cache.WriteCount);
    }

    [TestMethod]
    public async Task MixedRecognitionFailureRetainsSuccessfulEvidenceAndStructuredWarning()
    {
        OcrDetectedRegion successRegion = OcrTestFixtures.Region("success", 42, 88, 10, 6);
        OcrDetectedRegion failedRegion = OcrTestFixtures.Region("failed", 62, 88, 10, 6);
        var modelFailure = new OcrFailure(
            "MODEL_NOT_FOUND",
            "error",
            "Errors.ModelNotFound",
            "Fixture model is unavailable.",
            true,
            "select_manual_mode");
        var recognizer = new StubTextRecognizer((crops, _) =>
            ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                crop.RegionId == "success"
                    ? new OcrRecognition(
                        crop.RegionId,
                        crop.SourceImage,
                        [new OcrRecognitionAlternative("10", 0.95, crop.SourceImage)],
                        0.1)
                    : new OcrRecognition(crop.RegionId, crop.SourceImage, [], 0.1, modelFailure)).ToArray()));
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([]),
            recognizer,
            new InMemoryOcrResultCache(),
            batchSize: 4);

        OcrResult result = await pipeline.RecognizeAsync(
            OcrTestFixtures.Request([successRegion, failedRegion]),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.HasCount(1, result.Regions);
        Assert.AreEqual("success", result.Regions[0].RegionId);
        Assert.HasCount(1, result.Masks);
        Assert.AreEqual("success", result.Masks[0].RegionId);
        Assert.HasCount(1, result.RegionFailures ?? []);
        Assert.AreEqual("failed", result.RegionFailures![0].RegionId);
        Assert.AreEqual("MODEL_NOT_FOUND", result.RegionFailures[0].Failure.Code);
        Assert.IsTrue(result.Warnings.Any(warning =>
            warning.Contains("failed", StringComparison.Ordinal) &&
            warning.Contains("MODEL_NOT_FOUND", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task MinorEditOutsideOcrCropReusesUnchangedCropRecognition()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("stable-crop", 20, 30, 12, 8);
        OcrImage firstImage = OcrTestFixtures.Image();
        byte[] editedPixels = firstImage.Pixels.ToArray();
        editedPixels[^1] ^= 0xff;
        OcrImage editedImage = firstImage with { Pixels = editedPixels };
        var recognizer = Recognizer(("stable-crop", OcrSourceImage.Original, "10", 0.94));
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([]),
            recognizer,
            new InMemoryOcrResultCache(),
            batchSize: 4);
        OcrRequest firstRequest = OcrTestFixtures.Request(
            [region],
            inputHash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") with
        {
            OriginalImage = firstImage,
        };
        OcrRequest editedRequest = firstRequest with
        {
            InputSha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            OriginalImage = editedImage,
        };

        OcrResult first = await pipeline.RecognizeAsync(firstRequest, CancellationToken.None);
        OcrResult reused = await pipeline.RecognizeAsync(editedRequest, CancellationToken.None);

        Assert.IsFalse(first.Cache.CacheHit);
        Assert.IsFalse(reused.Cache.CacheHit);
        Assert.IsTrue(reused.Cache.RecognitionCacheHit);
        Assert.IsFalse(string.IsNullOrWhiteSpace(reused.Cache.RecognitionCacheKey));
        Assert.AreEqual(1, recognizer.CallCount);
        CollectionAssert.AreEqual(first.Regions.ToArray(), reused.Regions.ToArray());
    }

    private static StubTextRecognizer Recognizer(
        params (string RegionId, OcrSourceImage Source, string Text, double Confidence)[] results)
    {
        var alternatives = results.ToDictionary(
            static result => (result.RegionId, result.Source),
            static result => (IReadOnlyList<OcrRecognitionAlternative>)[
                new OcrRecognitionAlternative(result.Text, result.Confidence, result.Source),
            ]);
        return new StubTextRecognizer(alternatives);
    }

    private static async Task<OcrResult> RecognizeParticipantLaneUnionAsync(string recognizedText)
    {
        OcrDetectedRegion word = OcrTestFixtures.Region("word", 2, 35, 18, 10);
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 22, 35, 5, 10);
        var recognizer = new StubTextRecognizer((crops, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.HasCount(1, crops);
            OcrCrop crop = crops[0];
            Assert.AreEqual(OcrSourceImage.Original, crop.SourceImage);
            Assert.AreEqual(new OcrRectangle(2, 35, 25, 10), crop.OriginalPolygon.Bounds);
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>
            ([
                new OcrRecognition(
                    crop.RegionId,
                    crop.SourceImage,
                    [new OcrRecognitionAlternative(recognizedText, 0.95, crop.SourceImage)],
                    0.1),
            ]);
        });
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([]),
            recognizer,
            new InMemoryOcrResultCache(),
            new OcrPipelineOptions
            {
                EnableParticipantLaneAssembly = true,
                CropPaddingPixels = 0,
            });

        OcrResult result = await pipeline.RecognizeAsync(
            OcrTestFixtures.Request([word, suffix]),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.AreEqual(1, recognizer.CallCount);
        return result;
    }
}
