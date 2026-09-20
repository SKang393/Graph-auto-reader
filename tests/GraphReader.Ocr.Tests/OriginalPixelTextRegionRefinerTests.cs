// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class OriginalPixelTextRegionRefinerTests
{
    [TestMethod]
    public void TrimmingKeepsNoiseIdentityAndContextButInvalidatesGeometryEvidence()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 160 * 100).ToArray();
        pixels[34 * 160 + 44] = 0;
        pixels[38 * 160 + 49] = 40; // Every foreground pixel remains, even isolated noise.
        var context = new OcrRegionContext(NearAnnotationArrow: true);
        OcrDetectedRegion region = OcrTestFixtures.Region("text", 40, 30, 20, 15, context: context) with
        {
            Evidence = new OcrRegionEvidence(1, 0.1, 0.9, 0, false, ["fixture"]),
        };
        OcrImage original = OcrTestFixtures.Image() with { Pixels = pixels };
        OcrDetectedRegion result = OriginalPixelTextRegionRefiner.Refine(original, [region]).Single();
        Assert.AreEqual(new OcrRectangle(44, 34, 6, 5), result.Polygon.Bounds);
        Assert.AreEqual(region.RegionId, result.RegionId);
        Assert.AreEqual(region.DetectionConfidence, result.DetectionConfidence);
        Assert.AreSame(context, result.Context);
        Assert.IsNull(result.Evidence);
        Assert.AreEqual((byte)255, pixels[30 * 160 + 40]);
    }

    [TestMethod]
    public void BlankProposalIsRetainedAndStridePaddingDoesNotAffectThreshold()
    {
        byte[] pixels = new byte[100 * 20];
        for (int y = 0; y < 20; y++)
        {
            Array.Fill(pixels, (byte)255, y * 100, 20);
        }
        pixels[8 * 100 + 8] = 150;
        var image = new OcrImage(20, 20, 100, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
        OcrDetectedRegion blank = OcrTestFixtures.Region("blank", 0, 0, 2, 2);
        OcrDetectedRegion text = OcrTestFixtures.Region("text", 5, 5, 10, 10);
        IReadOnlyList<OcrDetectedRegion> result = OriginalPixelTextRegionRefiner.Refine(image, [blank, text]);
        Assert.AreSame(blank, result[0]);
        Assert.AreEqual(new OcrRectangle(8, 8, 1, 1), result[1].Polygon.Bounds);
    }

    [TestMethod]
    public void FractionalBoundaryCannotExpandBeyondProposal()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 100).ToArray();
        pixels[2 * 10 + 2] = 0;
        pixels[5 * 10 + 5] = 0;
        var image = new OcrImage(10, 10, 10, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
        OcrDetectedRegion region = OcrTestFixtures.Region("text", 2.5, 2.25, 5, 5);
        OcrDetectedRegion result = OriginalPixelTextRegionRefiner.Refine(image, [region]).Single();
        Assert.AreEqual(new OcrRectangle(2.5, 2.25, 3.5, 3.75), result.Polygon.Bounds);
    }

    [TestMethod]
    public void InvalidPixelsGeometryAndCancellationFailClosed()
    {
        OcrImage image = OcrTestFixtures.Image();
        OcrDetectedRegion region = OcrTestFixtures.Region("text", 40, 30, 20, 15);
        Assert.ThrowsExactly<ArgumentException>(() => OriginalPixelTextRegionRefiner.Refine(
            image with { SourceImage = OcrSourceImage.Enhanced }, [region]));
        Assert.ThrowsExactly<ArgumentException>(() => OriginalPixelTextRegionRefiner.Refine(
            image with { Stride = 1 }, [region]));
        Assert.ThrowsExactly<ArgumentException>(() => OriginalPixelTextRegionRefiner.Refine(
            image, [OcrTestFixtures.Region("outside", -1, 0, 5, 5)]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => OriginalPixelTextRegionRefiner.Refine(
            image, [region], cancellation.Token));
    }

    [TestMethod]
    public async Task PipelineUsesRefinedOriginalCropAndCannotReuseDisabledCache()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 160 * 100).ToArray();
        pixels[34 * 160 + 44] = 0;
        pixels[38 * 160 + 49] = 0;
        OcrDetectedRegion region = OcrTestFixtures.Region("text", 40, 30, 20, 15);
        OcrRequest request = OcrTestFixtures.Request([region]) with
        {
            OriginalImage = OcrTestFixtures.Image() with { Pixels = pixels },
        };
        var observed = new List<OcrCrop>();
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            observed.AddRange(crops);
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                new OcrRecognition(crop.RegionId, crop.SourceImage,
                    [new OcrRecognitionAlternative("text", 0.95, crop.SourceImage)], 0.1)).ToArray());
        });
        var cache = new InMemoryOcrResultCache();
        var options = new OcrPipelineOptions { CropPaddingPixels = 0 };
        var disabled = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache, options);
        var enabled = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache,
            options with { EnableOriginalPixelBoundsRefinement = true });
        OcrResult baseline = await disabled.RecognizeAsync(request);
        OcrResult refined = await enabled.RecognizeAsync(request);
        Assert.IsTrue(baseline.Succeeded, baseline.Failure?.TechnicalMessage);
        Assert.IsTrue(refined.Succeeded, refined.Failure?.TechnicalMessage);
        Assert.HasCount(2, observed);
        Assert.AreEqual(region.Polygon.Bounds, observed[0].OriginalPolygon.Bounds);
        Assert.AreEqual(new OcrRectangle(44, 34, 6, 5), observed[1].OriginalPolygon.Bounds);
        Assert.AreEqual(OcrSourceImage.Original, observed[1].SourceImage);
        Assert.AreEqual(observed[1].OriginalPolygon.Bounds, refined.Regions.Single().Polygon.Bounds);
        Assert.AreEqual(OcrCacheKeyDeriver.CreateRequestAlias(request, recognizer, options, "detector"),
            OcrCacheKeyDeriver.CreateRequestAlias(request, recognizer,
                options with { EnableOriginalPixelBoundsRefinement = false }, "detector"));
    }
}
