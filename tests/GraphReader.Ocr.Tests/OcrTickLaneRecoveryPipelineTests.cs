// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class OcrTickLaneRecoveryPipelineTests
{
    [TestMethod]
    [DataRow("7")]
    [DataRow("T")]
    public async Task OptInReadsOriginalPixelsWithoutReplacingBaselineOrGuessingADigit(string recoveredText)
    {
        OcrRequest request = Request();
        var cache = new InMemoryOcrResultCache();
        var seen = new List<OcrCrop>();
        var recognizer = Recognizer(recoveredText, seen);
        OcrResult baseline = await Pipeline(recognizer, cache, false).RecognizeAsync(request);
        var pipeline = Pipeline(recognizer, cache, true);
        OcrResult recovered = await pipeline.RecognizeAsync(request);

        Assert.IsTrue(baseline.Succeeded);
        Assert.IsTrue(recovered.Succeeded, recovered.Failure?.TechnicalMessage);
        Assert.HasCount(3, baseline.Regions);
        Assert.HasCount(4, recovered.Regions);
        Assert.AreEqual(JsonSerializer.Serialize(baseline.Regions),
            JsonSerializer.Serialize(recovered.Regions.Take(3)));
        OcrRegion added = recovered.Regions.Single(region => region.RegionId.StartsWith("tick-lane:", StringComparison.Ordinal));
        Assert.AreEqual(recoveredText, added.Text);
        Assert.AreEqual(OcrSourceImage.Original, added.SourceImage);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, added.ReviewStatus);
        Assert.AreEqual(new OcrRectangle(80, 91, 5, 8), added.Polygon.Bounds);
        Assert.Contains($"ocr_role_needs_review:{added.RegionId}:original_pixel_tick_recovery", recovered.Warnings);
        Assert.AreEqual(4, recovered.Cache.CropCount);
        Assert.AreEqual(2, recovered.Cache.BatchCount);
        Assert.IsFalse(recovered.Cache.RecognitionCacheHit);
        Assert.AreNotEqual(baseline.Cache.CacheKey, recovered.Cache.CacheKey);
        Assert.AreNotEqual(baseline.Cache.RecognitionCacheKey, recovered.Cache.RecognitionCacheKey);
        OcrCrop crop = seen.Single(item => item.RegionId == added.RegionId);
        Assert.IsTrue(crop.Pixels.ToArray().Any(value => value == 0));
        Assert.IsTrue(crop.Pixels.ToArray().Any(value => value == 1));
        Assert.AreEqual(2, recognizer.CallCount);
        OcrResult cached = await pipeline.RecognizeAsync(request);
        Assert.IsTrue(cached.Cache.CacheHit);
        Assert.AreEqual(2, recognizer.CallCount);
        Assert.AreEqual(JsonSerializer.Serialize(recovered.Regions), JsonSerializer.Serialize(cached.Regions));
    }

    [TestMethod]
    [DataRow("37")]
    [DataRow("T")]
    public async Task LeftAlignedYRecoveryReadsPixelsAndPreservesExistingReadings(string recoveredText)
    {
        const int width = 240, height = 200;
        OcrDetectedRegion[] anchors = [OcrTestFixtures.Region("one", 20, 20, 8, 8),
            OcrTestFixtures.Region("two", 20, 80, 16, 8), OcrTestFixtures.Region("three", 20, 140, 24, 8)];
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int y = 110; y < 118; y++)
            for (int x = 20; x < 32; x++) pixels[y * width + x] = 0;
        var request = OcrTestFixtures.Request(anchors) with
        {
            OriginalImage = new OcrImage(width, height, width, pixels,
                OcrSourceImage.Original, OcrFrameTransform.Identity),
            PlotBounds = new OcrRectangle(60, 10, 160, 160),
        };
        var seen = new List<OcrCrop>();
        var recognizer = Recognizer(recoveredText, seen);
        OcrResult baseline = await Pipeline(recognizer, new InMemoryOcrResultCache(), false).RecognizeAsync(request);
        OcrResult result = await Pipeline(recognizer, new InMemoryOcrResultCache(), true).RecognizeAsync(request);
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(4, result.Regions);
        Assert.AreEqual(JsonSerializer.Serialize(baseline.Regions), JsonSerializer.Serialize(result.Regions.Take(3)));
        OcrRegion added = result.Regions[^1];
        Assert.AreEqual(recoveredText, added.Text);
        Assert.AreEqual(new OcrRectangle(20, 110, 12, 8), added.Polygon.Bounds);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, added.ReviewStatus);
        Assert.Contains($"ocr_role_needs_review:{added.RegionId}:original_pixel_tick_recovery", result.Warnings);
        Assert.IsTrue(seen.Single(crop => crop.RegionId == added.RegionId).Pixels.ToArray().Any(value => value == 0));
        Assert.IsTrue(pixels.Skip(110 * width + 20).Take(12).All(value => value == 0));
    }

    [TestMethod]
    public async Task InsufficientAnchorsDoNotTriggerAdditionalRecognition()
    {
        OcrRequest request = Request();
        request = request with { DetectedRegions = request.DetectedRegions!.Take(2).ToArray() };
        var recognizer = Recognizer("7", []);
        OcrResult result = await Pipeline(recognizer, new InMemoryOcrResultCache(), true).RecognizeAsync(request);
        Assert.IsTrue(result.Succeeded);
        Assert.HasCount(2, result.Regions);
        Assert.AreEqual(1, recognizer.CallCount);
    }

    [TestMethod]
    public async Task RecoveryFailureIsExplicitAndDoesNotEraseValidBaseline()
    {
        var recognizer = Recognizer("7", [], () => throw new InvalidOperationException("recognizer unavailable"));
        var cache = new InMemoryOcrResultCache();
        var pipeline = Pipeline(recognizer, cache, true);
        OcrResult result = await pipeline.RecognizeAsync(Request());
        Assert.IsTrue(result.Succeeded);
        Assert.HasCount(3, result.Regions);
        Assert.IsNotNull(result.RegionFailures);
        Assert.HasCount(1, result.RegionFailures);
        Assert.AreEqual("OCR_RECOGNITION_FAILED", result.RegionFailures[0].Failure.Code);
        StringAssert.StartsWith(result.RegionFailures[0].RegionId, "tick-lane:");
        OcrResult retried = await pipeline.RecognizeAsync(Request());
        Assert.IsFalse(retried.Cache.CacheHit);
        Assert.AreEqual(3, recognizer.CallCount);
    }

    [TestMethod]
    public async Task CancellationDuringRecoveryPropagatesWithoutCachingPartialResult()
    {
        using var cancellation = new CancellationTokenSource();
        var recognizer = Recognizer("7", [], () =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        });
        var cache = new InMemoryOcrResultCache();
        var pipeline = Pipeline(recognizer, cache, true);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await pipeline.RecognizeAsync(Request(), cancellation.Token));
        Assert.AreEqual(0, cache.WriteCount);
    }

    private static OcrRequest Request()
    {
        const int width = 260;
        byte[] pixels = Enumerable.Repeat((byte)255, width * 100).ToArray();
        foreach (int left in new[] { 40, 80, 120, 200 })
        {
            for (int y = 91; y < 99; y++)
            {
                for (int x = left; x < left + 5; x++)
                {
                    pixels[y * width + x] = 0;
                }
            }
        }
        return OcrTestFixtures.Request([
            OcrTestFixtures.Region("one", 40, 91, 5, 8),
            OcrTestFixtures.Region("two", 120, 91, 5, 8),
            OcrTestFixtures.Region("three", 200, 91, 5, 8)]) with
        {
            OriginalImage = new OcrImage(width, 100, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity),
            PlotBounds = new OcrRectangle(30, 15, 220, 70),
        };
    }

    private static StubTextRecognizer Recognizer(string recoveredText, List<OcrCrop> seen, Action? onRecovery = null) =>
        new((crops, _) =>
        {
            seen.AddRange(crops);
            if (crops.Any(crop => crop.RegionId.StartsWith("tick-lane:", StringComparison.Ordinal)))
            {
                onRecovery?.Invoke();
            }
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop => new OcrRecognition(
                crop.RegionId, crop.SourceImage,
                [new OcrRecognitionAlternative(crop.RegionId switch
                {
                    "one" => "1", "two" => "5", "three" => "9", _ => recoveredText,
                }, 0.95, crop.SourceImage)], 0.1)).ToArray());
        });

    private static OcrPipeline Pipeline(ITextRecognizer recognizer, IOcrResultCache cache, bool enabled) =>
        new(new StubTextRegionDetector([]), recognizer, cache,
            new OcrPipelineOptions { EnableTickLaneRecovery = enabled, InferVerticalOrientationForTallRegions = false });
}
