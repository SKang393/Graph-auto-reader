// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class DenseTickSeparationPipelineTests
{
    [TestMethod]
    public async Task OptInKeepsOriginalAndLiteralCropReadingsWithMandatoryReviewAndDistinctCache()
    {
        var cache = new InMemoryOcrResultCache();
        var recognizer = new StubTextRecognizer((crops, _) => ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(
            crops.Select(crop => new OcrRecognition(crop.RegionId, crop.SourceImage,
                [new(crop.RegionId == "wide" ? "1234567890" : crop.RegionId.EndsWith("001", StringComparison.Ordinal)
                    ? "B4" : "3", .95, crop.SourceImage)], .1)).ToArray()));
        OcrResult baseline = await Pipeline(recognizer, cache, false).RecognizeAsync(Request());
        OcrPipeline trial = Pipeline(recognizer, cache, true);
        OcrResult result = await trial.RecognizeAsync(Request());

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(1, baseline.Regions);
        Assert.AreEqual(OcrTextRole.XTick, baseline.Regions[0].Role);
        Assert.HasCount(5, result.Regions);
        OcrRegion original = result.Regions.Single(static region => region.RegionId == "wide");
        Assert.AreEqual("1234567890", original.Text);
        Assert.AreEqual(OcrTextRole.Other, original.Role);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, original.ReviewStatus);
        OcrRegion confused = result.Regions.Single(static region => region.Text == "B4");
        Assert.AreEqual(OcrTextRole.XTick, confused.Role);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, confused.ReviewStatus);
        Assert.Contains("ocr_tick_sequence_needs_review:XTick:dense_region_separation:wide", result.Warnings);
        Assert.AreNotEqual(baseline.Cache.CacheKey, result.Cache.CacheKey);
        Assert.IsFalse(result.Cache.CacheHit);
        OcrResult cached = await trial.RecognizeAsync(Request());
        Assert.IsTrue(cached.Cache.CacheHit);
        Assert.Contains("ocr_tick_sequence_needs_review:XTick:dense_region_separation:wide", cached.Warnings);
    }

    [TestMethod]
    public async Task FailedCropBatchPreservesTheOriginalAndIsNotCached()
    {
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            if (crops.Any(crop => crop.RegionId.StartsWith("dense-tick:", StringComparison.Ordinal)))
                throw new InvalidOperationException("test crop failure");
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop => new OcrRecognition(
                crop.RegionId, crop.SourceImage, [new("1234567890", .95, crop.SourceImage)], .1)).ToArray());
        });
        OcrPipeline pipeline = Pipeline(recognizer, new InMemoryOcrResultCache(), true);
        OcrResult first = await pipeline.RecognizeAsync(Request());
        Assert.IsTrue(first.Succeeded);
        Assert.HasCount(1, first.Regions);
        Assert.AreEqual(OcrTextRole.XTick, first.Regions[0].Role);
        Assert.IsNotNull(first.RegionFailures);
        Assert.HasCount(4, first.RegionFailures);
        Assert.Contains("ocr_tick_sequence_needs_review:XTick:dense_region_separation:wide", first.Warnings);
        OcrResult second = await pipeline.RecognizeAsync(Request());
        Assert.IsFalse(second.Cache.CacheHit);
    }

    [TestMethod]
    public async Task MismatchedCropResultIdentitiesAreFailuresRatherThanPartialEvidence()
    {
        var recognizer = new StubTextRecognizer((crops, _) => ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(
            crops.Any(crop => crop.RegionId.StartsWith("dense-tick:", StringComparison.Ordinal))
                ? [new("unexpected", OcrSourceImage.Original, [new("1", .9, OcrSourceImage.Original)], .1)]
                : [new("wide", OcrSourceImage.Original, [new("1234567890", .9, OcrSourceImage.Original)], .1)]));
        OcrResult result = await Pipeline(recognizer, new InMemoryOcrResultCache(), true).RecognizeAsync(Request());
        Assert.IsTrue(result.Succeeded);
        Assert.HasCount(1, result.Regions);
        Assert.AreEqual(OcrTextRole.XTick, result.Regions[0].Role);
        Assert.IsNotNull(result.RegionFailures);
        Assert.HasCount(4, result.RegionFailures);
        Assert.Contains("ocr_result_cache_skipped_due_to_failure", result.Warnings);
    }

    [TestMethod]
    public async Task ChangedStrokeEvidenceCannotReuseThePriorResult()
    {
        var recognizer = new StubTextRecognizer((crops, _) => ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(
            crops.Select(crop => new OcrRecognition(crop.RegionId, crop.SourceImage,
                [new(crop.RegionId == "wide" ? "1234567890" : "3", .95, crop.SourceImage)], .1)).ToArray()));
        OcrPipeline pipeline = Pipeline(recognizer, new InMemoryOcrResultCache(), true);
        OcrResult first = await pipeline.RecognizeAsync(Request());
        OcrResult changed = await pipeline.RecognizeAsync(Request() with
        {
            XAxisTickStrokes = [Stroke("a", 61), Stroke("b", 81), Stroke("c", 101), Stroke("d", 121)],
        });
        Assert.IsFalse(changed.Cache.CacheHit);
        Assert.AreNotEqual(first.Cache.CacheKey, changed.Cache.CacheKey);
    }

    private static OcrPipeline Pipeline(ITextRecognizer recognizer, IOcrResultCache cache, bool enabled) =>
        new(new StubTextRegionDetector([]), recognizer, cache,
            new OcrPipelineOptions { EnableDenseTickSeparation = enabled, InferVerticalOrientationForTallRegions = false });

    private static OcrRequest Request()
    {
        const int width = 240, height = 140;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        return OcrTestFixtures.Request([OcrTestFixtures.Region("wide", 50, 105, 100, 10)]) with
        {
            OriginalImage = new OcrImage(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity),
            PlotBounds = new OcrRectangle(40, 10, 150, 90),
            XAxisTickStrokes = [Stroke("a", 60), Stroke("b", 80), Stroke("c", 100), Stroke("d", 120)],
        };
    }

    private static OcrXAxisTickStroke Stroke(string id, double x) =>
        new(id, new(x, 100), new(x, 98), new(x, 103), .9, [id]);
}
