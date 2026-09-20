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
