// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class FramedLegendTextRecoveryTests
{
    [TestMethod]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    public async Task EmptyDetectorRecoversMeasuredTextBesideDetachedSymbol(int scale, bool hollow)
    {
        OcrImage image = Fixture(scale, hollow);
        byte[] original = image.Pixels.ToArray();
        IReadOnlyList<OcrDetectedRegion> recovered = await FramedLegendRoleResolver.RecoverMissingTextAsync(image, []);
        Assert.HasCount(1, recovered);
        OcrDetectedRegion region = recovered.Single();
        Assert.AreEqual(new OcrRectangle(72 * scale, 36 * scale, 75 * scale, 10 * scale), region.Polygon.Bounds);
        Assert.AreEqual(0, region.OrientationDegrees);
        Assert.IsTrue(region.DetectionConfidence <= 0.7);
        Assert.IsNull(region.Context);
        Assert.IsNull(region.Evidence);
        Assert.AreEqual(OcrContract.CoordinateSpace, region.CoordinateSpace);
        Assert.StartsWith("framed-legend:", region.RegionId);
        CollectionAssert.AreEqual(original, image.Pixels.ToArray());
        CollectionAssert.AreEqual(recovered.ToArray(),
            (await FramedLegendRoleResolver.RecoverMissingTextAsync(image, [])).ToArray());
    }

    [TestMethod]
    public async Task WideCanvasCannotMergeFrameWithItsTextComponents()
    {
        // A frame can pass the component-size filter on a wide graph. Its
        // enclosing rectangle must not merge all of its separate contents.
        IReadOnlyList<OcrDetectedRegion> recovered = await FramedLegendRoleResolver.RecoverMissingTextAsync(
            Fixture(wideCanvas: true), []);
        Assert.HasCount(1, recovered);
        Assert.AreEqual(new OcrRectangle(72, 36, 75, 10), recovered.Single().Polygon.Bounds);
    }

    [TestMethod]
    [DataRow(13, false)]
    [DataRow(14, false)]
    [DataRow(26, false)]
    [DataRow(13, true)]
    [DataRow(14, true)]
    [DataRow(26, true)]
    public async Task DetachedSymbolSeparationUsesForegroundPixelCenters(int textHeight, bool partial)
    {
        OcrImage image = SeparationFixture(textHeight, 0);
        byte[] original = image.Pixels.ToArray();
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 144, 45, 8, textHeight);
        var recovered = partial
            ? await FramedLegendRoleResolver.RecoverPartialTextRowsAsync(image, [suffix])
            : await FramedLegendRoleResolver.RecoverMissingTextAsync(image, []);
        Assert.HasCount(1, recovered);
        Assert.AreEqual(new OcrRectangle(72, 45, 80, textHeight), recovered.Single().Polygon.Bounds);
        Assert.IsNull(recovered.Single().Context);
        Assert.IsNull(recovered.Single().Evidence);
        CollectionAssert.AreEqual(original, image.Pixels.ToArray());
        CollectionAssert.AreEqual(recovered.ToArray(),
            (await FramedLegendRoleResolver.RecoverPartialTextRowsAsync(image, recovered)).ToArray());
    }

    [TestMethod]
    [DataRow(13)]
    [DataRow(14)]
    [DataRow(26)]
    public async Task OnePixelBelowRequiredForegroundSeparationIsStillRejected(int textHeight)
    {
        OcrImage image = SeparationFixture(textHeight, -1);
        Assert.IsEmpty(await FramedLegendRoleResolver.RecoverMissingTextAsync(image, []));
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 144, 45, 8, textHeight);
        CollectionAssert.AreEqual(new[] { suffix },
            (await FramedLegendRoleResolver.RecoverPartialTextRowsAsync(image, [suffix])).ToArray());
    }

    private static OcrImage SeparationFixture(int textHeight, int gapOffset)
    {
        const int width = 220, height = 160, stride = 225;
        byte[] pixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
        void Rectangle(int left, int top, int right, int bottom, bool outline = false)
        {
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (!outline || x == left || x == right - 1 || y == top || y == bottom - 1)
                    pixels[y * stride + x] = 0;
        }
        Rectangle(25, 32, 178, 45 + textHeight + 12, true);
        int gap = (int)Math.Ceiling(textHeight / 2d) - 1 + gapOffset;
        int symbolRight = 72 - gap;
        Rectangle(symbolRight - textHeight, 45, symbolRight, 45 + textHeight, true);
        for (int x = 72; x <= 144; x += 12) Rectangle(x, 45, x + 8, 45 + textHeight);
        return new OcrImage(width, height, stride, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
    }

    [TestMethod]
    public async Task IndividualComponentsRemainOptInAndHaveDistinctConfigurationIdentity()
    {
        OcrImage image = Fixture(wideCanvas: true);
        var grouped = new ConnectedComponentTextRegionDetector();
        var separate = new ConnectedComponentTextRegionDetector(new() { GroupComponentsIntoLines = false });
        Assert.HasCount(1, await grouped.DetectAsync(image, default));
        Assert.HasCount(11, await separate.DetectAsync(image, default));
        Assert.AreEqual(grouped.ConfigurationFingerprint + ":ungrouped", separate.ConfigurationFingerprint);
        Assert.AreEqual(grouped.ConfigurationFingerprint,
            new ConnectedComponentTextRegionDetector(new() { GroupComponentsIntoLines = true }).ConfigurationFingerprint);
    }

    [TestMethod]
    [DataRow("no-frame")]
    [DataRow("missing-edge")]
    [DataRow("no-symbol")]
    [DataRow("two-symbols")]
    [DataRow("attached-symbol")]
    [DataRow("extra-row")]
    public async Task AmbiguousOrIncompleteGeometryCannotSupplyMissingText(string defect)
    {
        Assert.IsEmpty(await FramedLegendRoleResolver.RecoverMissingTextAsync(Fixture(defect: defect), []));
    }

    [TestMethod]
    [DataRow(10)]
    [DataRow(75)]
    public async Task ExistingPartialOrCompleteTextPreventsDuplicateRecovery(int width)
    {
        OcrDetectedRegion prior = OcrTestFixtures.Region("existing", 72, 36, width, 10);
        Assert.IsEmpty(await FramedLegendRoleResolver.RecoverMissingTextAsync(Fixture(), [prior]));
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    public async Task MeasuredSingleRowReplacesContainedFragmentsWithoutDuplicatingText(int scale, bool suffixOnly)
    {
        OcrImage image = Fixture(scale);
        byte[] before = image.Pixels.ToArray();
        OcrDetectedRegion prefix = OcrTestFixtures.Region("prefix", 72 * scale, 36 * scale, 10 * scale, 10 * scale);
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 139 * scale, 36 * scale, 8 * scale, 10 * scale);
        OcrDetectedRegion outside = OcrTestFixtures.Region("outside", 10 * scale, 70 * scale, 25 * scale, 10 * scale);
        OcrDetectedRegion[] input = suffixOnly ? [suffix, outside] : [prefix, suffix, outside];
        var result = await FramedLegendRoleResolver.RecoverPartialTextRowsAsync(image, input);
        Assert.HasCount(2, result);
        Assert.AreEqual(outside, result.Single(r => r.RegionId == "outside"));
        OcrDetectedRegion row = result.Single(r => r.RegionId != "outside");
        Assert.AreEqual(new OcrRectangle(72 * scale, 36 * scale, 75 * scale, 10 * scale), row.Polygon.Bounds);
        Assert.IsTrue(row.DetectionConfidence <= 0.7);
        Assert.IsNull(row.Context);
        Assert.IsNull(row.Evidence);
        Assert.AreEqual(row, (await FramedLegendRoleResolver.RecoverPartialTextRowsAsync(image, input.Reverse().ToArray()))
            .Single(r => r.RegionId != "outside"));
        CollectionAssert.AreEqual(result.ToArray(), (await FramedLegendRoleResolver.RecoverPartialTextRowsAsync(image, result)).ToArray());
        CollectionAssert.AreEqual(before, image.Pixels.ToArray());
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    public async Task IndependentlyMeasuredRowSeparatesAnOverlappingLegendSymbol(int scale, bool hollow)
    {
        OcrImage image = Fixture(scale, hollow);
        byte[] before = image.Pixels.ToArray();
        OcrDetectedRegion prior = OcrTestFixtures.Region("overlap", 55 * scale, 36 * scale, 92 * scale, 10 * scale);
        OcrDetectedRegion outside = OcrTestFixtures.Region("outside", 10 * scale, 70 * scale, 25 * scale, 10 * scale);
        var result = await FramedLegendRoleResolver.RecoverPartialTextRowsAsync(image, [prior, outside]);
        Assert.HasCount(2, result);
        Assert.AreEqual(outside, result.Single(r => r.RegionId == "outside"));
        OcrDetectedRegion row = result.Single(r => r.RegionId != "outside");
        Assert.AreEqual(new OcrRectangle(72 * scale, 36 * scale, 75 * scale, 10 * scale), row.Polygon.Bounds);
        Assert.IsTrue(row.DetectionConfidence <= 0.7);
        Assert.IsNull(row.Context);
        Assert.IsNull(row.Evidence);
        Assert.AreEqual(row, (await FramedLegendRoleResolver.RecoverPartialTextRowsAsync(image, [outside, prior]))
            .Single(r => r.RegionId != "outside"));
        CollectionAssert.AreEqual(result.ToArray(), (await FramedLegendRoleResolver.RecoverPartialTextRowsAsync(image, result)).ToArray());
        CollectionAssert.AreEqual(before, image.Pixels.ToArray());
    }

    [TestMethod]
    [DataRow("before-symbol")]
    [DataRow("past-text")]
    [DataRow("above-text")]
    [DataRow("below-text")]
    [DataRow("protected")]
    [DataRow("vertical")]
    [DataRow("missing-edge")]
    [DataRow("two-symbols")]
    [DataRow("extra-row")]
    [DataRow("attached-symbol")]
    [DataRow("no-symbol")]
    public async Task SymbolOverhangRequiresCompleteIndependentGeometry(string defect)
    {
        OcrImage image = Fixture(defect: defect);
        OcrDetectedRegion prior = OcrTestFixtures.Region("overlap",
            defect == "before-symbol" ? 44 : 55, defect == "above-text" ? 35 : 36,
            defect == "before-symbol" ? 103 : defect == "past-text" ? 93 : 92,
            defect is "above-text" or "below-text" ? 11 : 10);
        if (defect == "protected") prior = prior with { Context = new(ExplicitRoleHint: OcrTextRole.Annotation) };
        if (defect == "vertical") prior = prior with { OrientationDegrees = 90 };
        CollectionAssert.AreEqual(new[] { prior }, (await FramedLegendRoleResolver.RecoverPartialTextRowsAsync(image, [prior])).ToArray());
    }

    [TestMethod]
    [DataRow("complete")]
    [DataRow("protected")]
    [DataRow("vertical")]
    [DataRow("overlapping-outside")]
    [DataRow("missing-edge")]
    [DataRow("two-symbols")]
    [DataRow("extra-row")]
    public async Task PartialRowRecoveryPreservesCompleteProtectedAndAmbiguousDetections(string reason)
    {
        OcrImage image = Fixture(defect: reason);
        OcrDetectedRegion prior = OcrTestFixtures.Region("prior", reason == "overlapping-outside" ? 68 : 72,
            36, reason == "complete" ? 75 : 10, 10);
        if (reason == "protected") prior = prior with { Context = new(NumericExpected: true) };
        if (reason == "vertical") prior = prior with { OrientationDegrees = 90 };
        CollectionAssert.AreEqual(new[] { prior }, (await FramedLegendRoleResolver.RecoverPartialTextRowsAsync(image, [prior])).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PipelineRecognizesTheRecoveredRowOnceAndRetainsReviewWarning(bool overlapsSymbol)
    {
        OcrImage image = Fixture();
        var request = OcrTestFixtures.Request([OcrTestFixtures.Region("fragment",
            overlapsSymbol ? 55 : 72, 36, overlapsSymbol ? 92 : 10, 10)]) with
        {
            OriginalImage = image,
            PlotBounds = new OcrRectangle(5, 5, 190, 80),
        };
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            Assert.HasCount(1, crops);
            Assert.AreEqual(new OcrRectangle(72, 36, 75, 10), crops[0].OriginalPolygon.Bounds);
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>([new(crops[0].RegionId,
                crops[0].SourceImage, [new("Measured label", 0.95, crops[0].SourceImage)], 0.1)]);
        });
        var pipeline = new OcrPipeline(new StubTextRegionDetector([]), recognizer, new InMemoryOcrResultCache(),
            new OcrPipelineOptions { CropPaddingPixels = 0, EnableFramedLegendTextRecovery = true, EnableFramedLegendRoleResolution = true });
        OcrResult result = await pipeline.RecognizeAsync(request);
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(1, result.Regions);
        Assert.AreEqual("Measured label", result.Regions[0].Text);
        Assert.AreEqual(OcrTextRole.LegendText, result.Regions[0].Role);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, result.Regions[0].ReviewStatus);
        Assert.IsTrue(result.Warnings.Any(w => w.EndsWith(":original_pixel_framed_legend_assembly", StringComparison.Ordinal)));
        Assert.AreEqual(1, recognizer.CallCount);
        Assert.IsTrue((await pipeline.RecognizeAsync(request)).Cache.CacheHit);
        Assert.AreEqual(1, recognizer.CallCount);
    }

    [TestMethod]
    public async Task InvalidImageGeometryAndCancellationFailClosed()
    {
        OcrImage image = Fixture();
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await FramedLegendRoleResolver.RecoverMissingTextAsync(image with { SourceImage = OcrSourceImage.Enhanced }, []));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await FramedLegendRoleResolver.RecoverMissingTextAsync(image with { Stride = 1 }, []));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await FramedLegendRoleResolver.RecoverMissingTextAsync(image with { OriginalToImage = new(2, 2, 0, 0) }, []));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await FramedLegendRoleResolver.RecoverMissingTextAsync(image with { CoordinateSpace = "derived" }, []));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await FramedLegendRoleResolver.RecoverMissingTextAsync(image, [OcrTestFixtures.Region("outside", -1, 0, 5, 5)]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await FramedLegendRoleResolver.RecoverMissingTextAsync(image, [], cancellation.Token));
    }

    [TestMethod]
    [DataRow("Unfamiliar series", true)]
    [DataRow("20", false)]
    public async Task PipelineReadsOriginalCropAndPreservesExistingRegionWithSeparateCache(string recognizedText, bool legend)
    {
        OcrImage image = Fixture();
        byte[] original = image.Pixels.ToArray();
        OcrDetectedRegion prior = OcrTestFixtures.Region("outside", 10, 70, 25, 10);
        var request = OcrTestFixtures.Request([prior]) with
        {
            OriginalImage = image,
            PlotBounds = new OcrRectangle(5, 5, 190, 80),
        };
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            foreach (OcrCrop crop in crops.Where(static c => c.RegionId != "outside"))
            {
                Assert.AreEqual(OcrSourceImage.Original, crop.SourceImage);
                Assert.AreEqual(new OcrRectangle(72, 36, 75, 10), crop.OriginalPolygon.Bounds);
                Assert.IsTrue(crop.Pixels.Span.Contains(0f));
                Assert.IsTrue(crop.Pixels.Span.Contains(1f));
            }
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                new OcrRecognition(crop.RegionId, crop.SourceImage,
                    [new OcrRecognitionAlternative(crop.RegionId == "outside" ? "Existing note" : recognizedText,
                        0.95, crop.SourceImage)], 0.1)).ToArray());
        });
        var cache = new InMemoryOcrResultCache();
        var options = new OcrPipelineOptions { CropPaddingPixels = 0, EnableFramedLegendRoleResolution = true };
        var baseline = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache, options);
        var recovery = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache,
            options with { EnableFramedLegendTextRecovery = true });
        OcrResult before = await baseline.RecognizeAsync(request);
        OcrResult after = await recovery.RecognizeAsync(request);
        Assert.IsTrue(before.Succeeded, before.Failure?.TechnicalMessage);
        Assert.IsTrue(after.Succeeded, after.Failure?.TechnicalMessage);
        Assert.HasCount(1, before.Regions);
        Assert.HasCount(2, after.Regions);
        Assert.AreEqual(JsonSerializer.Serialize(before.Regions.Single()),
            JsonSerializer.Serialize(after.Regions.Single(static r => r.RegionId == "outside")));
        OcrRegion label = after.Regions.Single(static r => r.RegionId != "outside");
        Assert.AreEqual(recognizedText, label.Text);
        Assert.AreEqual(legend, label.Role == OcrTextRole.LegendText);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, label.ReviewStatus);
        Assert.AreEqual(OcrSourceImage.Original, label.SourceImage);
        if (legend) Assert.IsTrue(label.Confidence <= 0.7);
        Assert.AreNotEqual(before.Cache.CacheKey, after.Cache.CacheKey);
        Assert.IsFalse(after.Cache.CacheHit);
        CollectionAssert.Contains(after.Warnings.ToArray(),
            $"ocr_role_needs_review:{label.RegionId}:original_pixel_framed_legend_recovery");
        int calls = recognizer.CallCount;
        OcrResult cached = await recovery.RecognizeAsync(request);
        Assert.IsTrue(cached.Cache.CacheHit);
        Assert.AreEqual(calls, recognizer.CallCount);
        CollectionAssert.AreEqual(after.Regions.ToArray(), cached.Regions.ToArray());
        CollectionAssert.AreEqual(original, image.Pixels.ToArray());
    }

    private static OcrImage Fixture(int scale = 1, bool hollow = false, string? defect = null, bool wideCanvas = false)
    {
        int width = (wideCanvas ? 1200 : 200) * scale, height = (wideCanvas ? 350 : 90) * scale, stride = width + 5;
        byte[] pixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
        void Rectangle(int left, int top, int right, int bottom, bool outline)
        {
            for (int y = top * scale; y < bottom * scale; y++)
            for (int x = left * scale; x < right * scale; x++)
                if (!outline || x < (left + 1) * scale || x >= (right - 1) * scale ||
                    y < (top + 1) * scale || y >= (bottom - 1) * scale)
                    pixels[y * stride + x] = 0;
        }
        if (defect != "no-frame") Rectangle(35, 20, 165, 54, true);
        if (defect == "missing-edge")
            for (int y = 21 * scale; y < 53 * scale; y++)
            for (int x = 35 * scale; x < 36 * scale; x++) pixels[y * stride + x] = 255;
        if (defect == "two-symbols")
        {
            Rectangle(43, 38, 49, 44, false);
            Rectangle(56, 38, 62, 44, false);
        }
        else if (defect != "no-symbol") Rectangle(45, 36, defect == "attached-symbol" ? 74 : 58, 48, hollow);
        for (int x = 72; x < 148; x += 9) Rectangle(x, 36, x + 3, 46, false);
        if (defect == "extra-row") Rectangle(82, 24, 86, 30, false);
        return new OcrImage(width, height, stride, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
    }
}
