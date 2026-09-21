// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class HeaderWordSuffixRecoveryTests
{
    private static readonly OcrRectangle Plot = new(30, 60, 170, 70);

    [TestMethod]
    public void CompletesVisibleSuffixesInOrderWithoutSupplyingText()
    {
        var (image, word, recognized, suffix) = Fixture();
        var second = OcrTestFixtures.Region("second", 142, 25, 7, 10);
        var result = HeaderWordSuffixRecovery.SelectCandidates(image, [second, suffix], [word], [recognized], Plot, []);
        Assert.HasCount(1, result);
        Assert.AreEqual(new OcrRectangle(65, 25, 84, 10), result[0].Polygon.Bounds);
        Assert.AreEqual(word.Context, result[0].Context);
        Assert.IsNull(result[0].Evidence);
        Assert.AreEqual(.7, result[0].DetectionConfidence);
        var reordered = HeaderWordSuffixRecovery.SelectCandidates(image, [suffix, second], [word], [recognized], Plot, []);
        Assert.AreEqual(result[0].RegionId, reordered[0].RegionId);
    }

    [TestMethod]
    [DataRow(OcrReviewStatus.Accepted)]
    [DataRow(OcrReviewStatus.Corrected)]
    [DataRow(OcrReviewStatus.Rejected)]
    public void HumanReviewDecisionsRemainUntouched(OcrReviewStatus review)
    {
        var (image, word, recognized, suffix) = Fixture();
        Assert.IsEmpty(HeaderWordSuffixRecovery.SelectCandidates(image, [suffix], [word],
            [recognized with { ReviewStatus = review }], Plot, []));
    }

    [TestMethod]
    public void RequiresBoundNonnumericHorizontalHeadingEvidence()
    {
        var (image, word, recognized, suffix) = Fixture();
        foreach (var changed in new[]
        {
            recognized with { RegionId = "unbound" },
            recognized with { Text = "150" },
            recognized with { Role = OcrTextRole.YTick },
            recognized with { Role = OcrTextRole.Annotation },
            recognized with { Polygon = OcrPolygon.FromRectangle(new OcrRectangle(65, 25, 59, 10)) },
        })
            Assert.IsEmpty(HeaderWordSuffixRecovery.SelectCandidates(image, [suffix], [word], [changed], Plot, []));
        Assert.IsEmpty(HeaderWordSuffixRecovery.SelectCandidates(image, [suffix],
            [word with { OrientationDegrees = 90 }], [recognized], Plot, []));
    }

    [TestMethod]
    [DataRow(136, 25, 8, 10)]
    [DataRow(130, 28, 8, 10)]
    [DataRow(50, 25, 8, 10)]
    [DataRow(130, 65, 8, 10)]
    [DataRow(130, 25, 1, 10)]
    [DataRow(130, 25, 25, 10)]
    public void RejectsDetachedMisalignedLeftPlotAndStructuralComponents(int x, int y, int width, int height)
    {
        var (image, word, recognized, _) = Fixture();
        var other = OcrTestFixtures.Region("other", x, y, width, height);
        Assert.IsEmpty(HeaderWordSuffixRecovery.SelectCandidates(image, [other], [word], [recognized], Plot, []));
    }

    [TestMethod]
    public void PreservesMeasuredPhaseBoundariesAndOtherDetectedLabels()
    {
        var (image, word, recognized, suffix) = Fixture();
        Assert.IsEmpty(HeaderWordSuffixRecovery.SelectCandidates(image, [suffix], [word], [recognized], Plot, [128]));
        Assert.IsEmpty(HeaderWordSuffixRecovery.SelectCandidates(image, [suffix], [word, suffix], [recognized], Plot, []));
    }

    [TestMethod]
    public void CaptionBracketBlocksAnExtensionBeyondItsEnd()
    {
        var (image, word, recognized, suffix) = Fixture();
        byte[] pixels = image.Pixels.ToArray();
        for (int x = 60; x <= 128; x++) pixels[40 * image.Stride + x] = 0;
        for (int y = 41; y <= 45; y++)
        {
            pixels[y * image.Stride + 60] = 0;
            pixels[y * image.Stride + 128] = 0;
        }
        var bracketed = image with { Pixels = pixels };
        Assert.IsTrue(new HeaderBracketEvidence(bracketed).HasBracketBelow(word.Polygon.Bounds, Plot.Top));
        Assert.IsEmpty(HeaderWordSuffixRecovery.SelectCandidates(bracketed, [suffix], [word], [recognized], Plot, []));
    }

    [TestMethod]
    public async Task RequiresOriginalPixelsValidGeometryAndCancellation()
    {
        var (image, word, recognized, suffix) = Fixture();
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await HeaderWordSuffixRecovery.FindAsync(
            image with { SourceImage = OcrSourceImage.Enhanced }, [word], [recognized], Plot, []));
        Assert.ThrowsExactly<ArgumentException>(() => HeaderWordSuffixRecovery.SelectCandidates(
            image, [suffix], [word], [recognized], Plot, [double.NaN]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => HeaderWordSuffixRecovery.SelectCandidates(
            image, [suffix], [word], [recognized], Plot, [], cancellation.Token));
    }

    [TestMethod]
    [DataRow("Training 9", 1)]
    [DataRow("Unreadable", 2)]
    public async Task PipelineRecognizesOriginalCompletionAndPreservesUncertainEvidence(string reading, int count)
    {
        var (image, word, _, _) = Fixture();
        byte[] original = image.Pixels.ToArray();
        OcrCrop? completedCrop = null;
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            completedCrop = crops.FirstOrDefault(crop => crop.RegionId.StartsWith("header-word-suffix:", StringComparison.Ordinal)) ?? completedCrop;
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop => new OcrRecognition(
                crop.RegionId, crop.SourceImage,
                [new OcrRecognitionAlternative(crop.RegionId == "word" ? "Training" : reading, .95, crop.SourceImage)], .1)).ToArray());
        });
        var request = OcrTestFixtures.Request([word]) with { OriginalImage = image, PlotBounds = Plot, PhaseDividerXs = [] };
        var cache = new InMemoryOcrResultCache();
        var detector = new StubTextRegionDetector([]);
        var baseline = await new OcrPipeline(detector, recognizer, cache).RecognizeAsync(request);
        var pipeline = new OcrPipeline(detector, recognizer, cache, new OcrPipelineOptions { EnableHeaderGlyphRecovery = true });
        var result = await pipeline.RecognizeAsync(request);
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(count, result.Regions);
        Assert.IsNotNull(completedCrop);
        Assert.AreEqual(OcrSourceImage.Original, completedCrop.SourceImage);
        Assert.AreEqual(new OcrRectangle(65, 25, 73, 10), completedCrop.OriginalPolygon.Bounds);
        Assert.IsTrue(result.Regions.All(static region => region.ReviewStatus == OcrReviewStatus.Unreviewed));
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Contains("original_pixel_header_word_suffix_recovery", StringComparison.Ordinal)));
        if (count == 2) Assert.IsTrue(result.Regions.Any(static region => region.Text == "Training"));
        else Assert.AreEqual(reading, result.Regions.Single().Text);
        Assert.AreNotEqual(baseline.Cache.CacheKey, result.Cache.CacheKey);
        Assert.IsTrue((await pipeline.RecognizeAsync(request)).Cache.CacheHit);
        Assert.AreEqual(2, recognizer.CallCount);
        CollectionAssert.AreEqual(original, image.Pixels.ToArray());
    }

    [TestMethod]
    public async Task WiderHeadingCompletionKeepsExistingRecoveredTickTensorIdentical()
    {
        var plot = new OcrRectangle(30, 15, 210, 70);
        OcrDetectedRegion[] detected =
        [
            OcrTestFixtures.Region("word", 40, 2, 40, 10,
                context: new OcrRegionContext(ExplicitRoleHint: OcrTextRole.PhaseHeading)),
            OcrTestFixtures.Region("one", 40, 91, 5, 8,
                context: new OcrRegionContext(ExplicitRoleHint: OcrTextRole.XTick)),
            OcrTestFixtures.Region("two", 110, 91, 5, 8,
                context: new OcrRegionContext(ExplicitRoleHint: OcrTextRole.XTick)),
            OcrTestFixtures.Region("three", 210, 91, 5, 8,
                context: new OcrRegionContext(ExplicitRoleHint: OcrTextRole.XTick)),
        ];
        // Keep distinct tick labels farther apart than component word grouping.
        byte[] pixels = Enumerable.Repeat((byte)255, 260 * 110).ToArray();
        foreach (var bounds in detected.Select(static region => region.Polygon.Bounds).Concat(
                     [new OcrRectangle(85, 2, 8, 10), new OcrRectangle(160, 91, 5, 8)]))
            for (int y = (int)bounds.Top; y < bounds.Bottom; y++)
                for (int x = (int)bounds.Left; x < bounds.Right; x++) pixels[y * 260 + x] = 0;
        var request = OcrTestFixtures.Request(detected) with
        {
            OriginalImage = new OcrImage(260, 110, 260, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity),
            PlotBounds = plot,
        };
        var existing = new List<OcrCrop>();
        var completed = new List<OcrCrop>();
        var options = new OcrPipelineOptions
        {
            EnableTickLaneRecovery = true, EnableHeaderGlyphRecovery = true,
            CropWidthMode = OcrCropWidthMode.PaddleBatchMaximumAspectRatio,
            CropWidth = 32, CropHeight = 32, CropPaddingPixels = 0,
            InferVerticalOrientationForTallRegions = false,
        };
        foreach (bool withSuffix in new[] { false, true })
        {
            var recognizer = new StubTextRecognizer((crops, _) =>
            {
                (withSuffix ? completed : existing).AddRange(crops);
                return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop => new OcrRecognition(
                    crop.RegionId, crop.SourceImage,
                    [new OcrRecognitionAlternative(crop.RegionId == "word" ? "Training" :
                        crop.RegionId.StartsWith("header-word-suffix:", StringComparison.Ordinal) ? "Training 9" : "3",
                        .95, crop.SourceImage)], .1)).ToArray());
            });
            var result = await new OcrPipeline(new StubTextRegionDetector([]), recognizer,
                new InMemoryOcrResultCache(), options).RecognizeAsync(
                    request with { PhaseDividerXs = withSuffix ? [] : null });
            Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        }
        var before = existing.Single(crop => crop.RegionId.StartsWith("tick-lane:", StringComparison.Ordinal));
        var after = completed.Single(crop => crop.RegionId == before.RegionId);
        var heading = completed.Single(crop => crop.RegionId.StartsWith("header-word-suffix:", StringComparison.Ordinal));
        Assert.IsTrue(heading.Width > before.Width);
        Assert.AreEqual(before.Width, after.Width);
        Assert.AreEqual(before.Height, after.Height);
        Assert.AreEqual(before.CropSha256, after.CropSha256);
        CollectionAssert.AreEqual(before.Pixels.ToArray(), after.Pixels.ToArray());
    }

    private static (OcrImage Image, OcrDetectedRegion Word, OcrRegion Recognized, OcrDetectedRegion Suffix) Fixture()
    {
        var word = OcrTestFixtures.Region("word", 65, 25, 60, 10,
            context: new OcrRegionContext(ExplicitRoleHint: OcrTextRole.PhaseHeading));
        var suffix = OcrTestFixtures.Region("suffix", 130, 25, 8, 10, confidence: .7);
        byte[] pixels = Enumerable.Repeat((byte)255, 220 * 140).ToArray();
        foreach (var bounds in new[] { word.Polygon.Bounds, suffix.Polygon.Bounds })
            for (int y = (int)bounds.Top; y < bounds.Bottom; y++)
                for (int x = (int)bounds.Left; x < bounds.Right; x++) pixels[y * 220 + x] = 0;
        var image = new OcrImage(220, 140, 220, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
        var recognized = new OcrRegion(word.RegionId, word.Polygon, "Training", [], OcrTextRole.PhaseHeading,
            .9, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);
        return (image, word, recognized, suffix);
    }
}
