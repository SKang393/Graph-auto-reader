// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class FramedLegendTextCompletionTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void TruncatedRowIncludesVisibleTrailingInkButNotFrameOrSymbol(int scale)
    {
        var fixture = Fixture(scale);
        byte[] before = fixture.Image.Pixels.ToArray();
        OcrDetectedRegion result = OriginalPixelTextRegionRefiner.Refine(fixture.Image, [fixture.Detection]).Single();
        Assert.AreEqual(new OcrRectangle(70 * scale, 31 * scale, 139 * scale, 15 * scale), result.Polygon.Bounds);
        Assert.AreEqual(fixture.Detection.RegionId, result.RegionId);
        Assert.AreEqual(fixture.Detection.DetectionConfidence, result.DetectionConfidence);
        Assert.IsNull(result.Context);
        Assert.IsNull(result.Evidence);
        CollectionAssert.AreEqual(before, fixture.Image.Pixels.ToArray());
        var full = fixture.Detection with { Polygon = result.Polygon };
        Assert.AreEqual(full, OriginalPixelTextRegionRefiner.Refine(fixture.Image, [full]).Single());
    }

    [TestMethod]
    [DataRow("missing-edge")]
    [DataRow("no-symbol")]
    [DataRow("two-symbols")]
    [DataRow("tall-frame")]
    [DataRow("large-gap")]
    [DataRow("frame-connected-stroke")]
    public void IncompleteFrameSymbolOrRowEvidenceCannotExpandText(string defect)
    {
        var fixture = Fixture(defect: defect);
        var result = OriginalPixelTextRegionRefiner.Refine(fixture.Image, [fixture.Detection]).Single();
        Assert.AreEqual(new OcrRectangle(70, 31, 48, 12), result.Polygon.Bounds);
    }

    [TestMethod]
    public void FullyBoundedRowDoesNotChaseNearbyDetachedInk()
    {
        var fixture = Fixture();
        byte[] pixels = fixture.Image.Pixels.ToArray();
        for (int x = 212; x < 222; x++) pixels[36 * fixture.Image.Stride + x] = 0;
        var image = fixture.Image with { Pixels = pixels };
        var complete = fixture.Detection with { Polygon = OcrPolygon.FromRectangle(new OcrRectangle(70, 31, 139, 15)) };
        Assert.AreEqual(complete, OriginalPixelTextRegionRefiner.Refine(image, [complete]).Single());
    }

    [TestMethod]
    public void ExistingSuffixAndExplicitContextAreNeverAbsorbed()
    {
        var fixture = Fixture();
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 125, 31, 84, 15);
        var result = OriginalPixelTextRegionRefiner.Refine(fixture.Image, [fixture.Detection, suffix]);
        Assert.AreEqual(118, result[0].Polygon.Bounds.Right);
        Assert.AreEqual(suffix.Polygon, result[1].Polygon);
        foreach (OcrRegionContext context in new OcrRegionContext[]
        {
            new(ExplicitRoleHint: OcrTextRole.Annotation), new(NearAnnotationArrow: true),
            new(NearPhaseDivider: true), new(NumericExpected: true),
            new(AxisTitleExpected: true), new(InParticipantBand: true),
        })
        {
            var protectedRegion = fixture.Detection with { Context = context };
            Assert.AreEqual(118, OriginalPixelTextRegionRefiner.Refine(fixture.Image, [protectedRegion])[0].Polygon.Bounds.Right);
        }
        var vertical = fixture.Detection with { OrientationDegrees = 90 };
        Assert.AreEqual(118, OriginalPixelTextRegionRefiner.Refine(fixture.Image, [vertical])[0].Polygon.Bounds.Right);
    }

    [TestMethod]
    public async Task PipelineRecognizesCompletedOriginalPixelsAndRetainsReviewAndCacheBoundaries()
    {
        var fixture = Fixture();
        var request = OcrTestFixtures.Request([fixture.Detection]) with
        {
            OriginalImage = fixture.Image,
            PlotBounds = new OcrRectangle(25, 10, 220, 125),
        };
        var seen = new List<OcrCrop>();
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            seen.AddRange(crops);
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                new OcrRecognition(crop.RegionId, crop.SourceImage,
                    [new OcrRecognitionAlternative(crop.OriginalPolygon.Bounds.Width > 100
                        ? "Unknown trailing words" : "Unknown", 0.95, crop.SourceImage)], 0.1)).ToArray());
        });
        var cache = new InMemoryOcrResultCache();
        var options = new OcrPipelineOptions { CropPaddingPixels = 0 };
        var baseline = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache, options);
        var completed = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache,
            options with { EnableOriginalPixelBoundsRefinement = true, EnableFramedLegendRoleResolution = true });
        OcrResult before = await baseline.RecognizeAsync(request);
        OcrResult after = await completed.RecognizeAsync(request);
        Assert.IsTrue(after.Succeeded, after.Failure?.TechnicalMessage);
        Assert.AreEqual("Unknown", before.Regions.Single().Text);
        Assert.AreEqual("Unknown trailing words", after.Regions.Single().Text);
        Assert.AreEqual(OcrTextRole.LegendText, after.Regions.Single().Role);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, after.Regions.Single().ReviewStatus);
        Assert.AreEqual(new OcrRectangle(70, 31, 139, 15), seen[1].OriginalPolygon.Bounds);
        Assert.AreEqual(OcrSourceImage.Original, seen[1].SourceImage);
        Assert.IsTrue(seen[1].Pixels.ToArray().Any(static value => value == 0));
        Assert.Contains("ocr_role_needs_review:label:original_pixel_legend_completion", after.Warnings);
        Assert.AreNotEqual(before.Cache.CacheKey, after.Cache.CacheKey);
        Assert.IsFalse(after.Cache.RecognitionCacheHit);
        Assert.IsTrue((await completed.RecognizeAsync(request)).Cache.CacheHit);
        Assert.AreEqual(2, recognizer.CallCount);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void FramedWordsAssembleAfterRefinementWithoutChangingOriginalPixels(int scale)
    {
        var fixture = Fixture(scale);
        byte[] before = fixture.Image.Pixels.ToArray();
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 125 * scale, 31 * scale, 84 * scale, 15 * scale);
        var refined = OriginalPixelTextRegionRefiner.Refine(fixture.Image, [fixture.Detection, suffix]);
        OcrDetectedRegion merged = FramedLegendRoleResolver.AssembleDetectedRows(fixture.Image, refined).Single();
        Assert.AreEqual(new OcrRectangle(70 * scale, 31 * scale, 139 * scale, 15 * scale), merged.Polygon.Bounds);
        Assert.StartsWith("framed-legend-row:", merged.RegionId);
        Assert.IsNull(merged.Context);
        Assert.IsNull(merged.Evidence);
        Assert.AreEqual(Math.Min(refined[0].DetectionConfidence, refined[1].DetectionConfidence), merged.DetectionConfidence);
        Assert.AreEqual(merged, FramedLegendRoleResolver.AssembleDetectedRows(fixture.Image, refined.Reverse().ToArray()).Single());
        Assert.AreEqual(merged, FramedLegendRoleResolver.AssembleDetectedRows(fixture.Image, [merged]).Single());
        CollectionAssert.AreEqual(before, fixture.Image.Pixels.ToArray());
    }

    [TestMethod]
    [DataRow("missing-edge")]
    [DataRow("no-symbol")]
    [DataRow("two-symbols")]
    [DataRow("tall-frame")]
    [DataRow("large-gap")]
    [DataRow("frame-connected-stroke")]
    public void FramedWordAssemblyRequiresVisibleRowAndSymbolEvidence(string defect)
    {
        var fixture = Fixture(defect: defect);
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", defect == "large-gap" ? 150 : 125, 31,
            defect == "large-gap" ? 59 : 84, 15);
        var refined = OriginalPixelTextRegionRefiner.Refine(fixture.Image, [fixture.Detection, suffix]);
        CollectionAssert.AreEqual(refined.ToArray(), FramedLegendRoleResolver.AssembleDetectedRows(fixture.Image, refined).ToArray());
    }

    [TestMethod]
    public void FramedWordAssemblyPreservesProtectedSuffixAndSeparateRows()
    {
        var fixture = Fixture();
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 125, 31, 84, 15);
        foreach (OcrRegionContext context in new OcrRegionContext[]
        {
            new(ExplicitRoleHint: OcrTextRole.Annotation), new(NearAnnotationArrow: true),
            new(NearPhaseDivider: true), new(NumericExpected: true), new(AxisTitleExpected: true), new(InParticipantBand: true),
        })
        {
            var refined = OriginalPixelTextRegionRefiner.Refine(fixture.Image, [fixture.Detection, suffix with { Context = context }]);
            CollectionAssert.AreEqual(refined.ToArray(), FramedLegendRoleResolver.AssembleDetectedRows(fixture.Image, refined).ToArray());
        }
        OcrDetectedRegion otherRow = OcrTestFixtures.Region("other-row", 125, 48, 84, 10);
        var existing = OriginalPixelTextRegionRefiner.Refine(fixture.Image, [fixture.Detection, suffix, otherRow]);
        var assembled = FramedLegendRoleResolver.AssembleDetectedRows(fixture.Image, existing);
        Assert.HasCount(2, assembled);
        Assert.AreEqual(otherRow, assembled.Single(r => r.RegionId == otherRow.RegionId));
        var vertical = suffix with { OrientationDegrees = 90 };
        Assert.HasCount(2, FramedLegendRoleResolver.AssembleDetectedRows(fixture.Image,
            OriginalPixelTextRegionRefiner.Refine(fixture.Image, [fixture.Detection, vertical])));
    }

    [TestMethod]
    public async Task FramedWordAssemblyRecognizesOneOriginalCropAndKeepsReviewAndCacheBoundaries()
    {
        var fixture = Fixture();
        OcrDetectedRegion suffix = OcrTestFixtures.Region("suffix", 125, 31, 84, 15);
        var request = OcrTestFixtures.Request([fixture.Detection, suffix]) with
        {
            OriginalImage = fixture.Image,
            PlotBounds = new OcrRectangle(5, 70, 25, 60),
        };
        var seen = new List<OcrCrop>();
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            seen.AddRange(crops);
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop =>
                new OcrRecognition(crop.RegionId, crop.SourceImage,
                    [new OcrRecognitionAlternative(crop.OriginalPolygon.Bounds.Width > 100 ? "Unknown series words" : "Fragment",
                        0.95, crop.SourceImage)], 0.1)).ToArray());
        });
        var cache = new InMemoryOcrResultCache();
        var options = new OcrPipelineOptions { CropPaddingPixels = 0, EnableOriginalPixelBoundsRefinement = true,
            EnableFramedLegendRoleResolution = true };
        var baseline = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache, options);
        var repaired = new OcrPipeline(new StubTextRegionDetector([]), recognizer, cache,
            options with { EnableFramedLegendTextRecovery = true });
        OcrResult before = await baseline.RecognizeAsync(request);
        OcrResult after = await repaired.RecognizeAsync(request);
        Assert.IsTrue(after.Succeeded, after.Failure?.TechnicalMessage);
        Assert.HasCount(2, before.Regions);
        Assert.HasCount(1, after.Regions);
        Assert.AreEqual("Unknown series words", after.Regions[0].Text);
        Assert.AreEqual(OcrTextRole.LegendText, after.Regions[0].Role);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, after.Regions[0].ReviewStatus);
        Assert.AreEqual(new OcrRectangle(70, 31, 139, 15), seen[^1].OriginalPolygon.Bounds);
        Assert.AreEqual(OcrSourceImage.Original, seen[^1].SourceImage);
        Assert.Contains($"ocr_role_needs_review:{after.Regions[0].RegionId}:original_pixel_framed_legend_assembly", after.Warnings);
        Assert.AreNotEqual(before.Cache.CacheKey, after.Cache.CacheKey);
        Assert.IsFalse(after.Cache.RecognitionCacheHit);
        Assert.IsTrue((await repaired.RecognizeAsync(request)).Cache.CacheHit);
        Assert.AreEqual(2, recognizer.CallCount);
    }

    [TestMethod]
    public void FramedWordAssemblyRejectsInvalidCoordinatesAndCancellation()
    {
        var fixture = Fixture();
        Assert.ThrowsExactly<ArgumentException>(() => FramedLegendRoleResolver.AssembleDetectedRows(
            fixture.Image, [fixture.Detection with { CoordinateSpace = "derived" }]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => FramedLegendRoleResolver.AssembleDetectedRows(
            fixture.Image, [fixture.Detection], cancellation.Token));
    }

    private static (OcrImage Image, OcrDetectedRegion Detection) Fixture(int scale = 1, string? defect = null)
    {
        int width = 280 * scale, height = 150 * scale, stride = width + 5;
        byte[] pixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
        void Rectangle(int left, int top, int right, int bottom, bool outline)
        {
            for (int y = top * scale; y < bottom * scale; y++)
            for (int x = left * scale; x < right * scale; x++)
                if (!outline || x < (left + 1) * scale || x >= (right - 1) * scale ||
                    y < (top + 1) * scale || y >= (bottom - 1) * scale)
                    pixels[y * stride + x] = 0;
        }
        int frameBottom = defect == "tall-frame" ? 125 : 61;
        Rectangle(35, 20, 225, frameBottom, true);
        if (defect == "missing-edge")
            for (int y = 21 * scale; y < 60 * scale; y++) pixels[y * stride + 35 * scale] = 255;
        if (defect == "two-symbols")
        {
            Rectangle(42, 33, 48, 41, false);
            Rectangle(55, 33, 61, 41, false);
        }
        else if (defect != "no-symbol") Rectangle(45, 31, 58, 43, false);
        for (int x = 70; x < 118; x += 9) Rectangle(x, 31, x + 3, 43, false);
        if (defect == "frame-connected-stroke") Rectangle(121, 36, 225, 37, false);
        else
        {
            int suffixStart = defect == "large-gap" ? 150 : 125;
            for (int x = suffixStart; x < 210; x += 9) Rectangle(x, 31, x + 3, 43, false);
            Rectangle(206, 40, 209, 46, false);
        }
        var image = new OcrImage(width, height, stride, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
        return (image, OcrTestFixtures.Region("label", 68 * scale, 29 * scale, 52 * scale, 16 * scale));
    }
}
