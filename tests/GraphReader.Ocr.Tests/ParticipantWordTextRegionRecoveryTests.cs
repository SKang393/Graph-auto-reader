// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class ParticipantWordTextRegionRecoveryTests
{
    private static readonly OcrRectangle Plot = new(120, 60, 100, 70);

    [TestMethod]
    public void CompletesBothClippedEdgesAndDetachedDotsWithoutSupplyingText()
    {
        var (image, word, recognized, components) = Fixture();
        var result = ParticipantWordTextRegionRecovery.SelectCandidates(image, components, [word], [recognized], Plot);
        Assert.HasCount(1, result);
        Assert.AreEqual(new OcrRectangle(24, 23, 77, 14), result[0].Polygon.Bounds);
        Assert.AreEqual(word.Context, result[0].Context);
        Assert.IsNull(result[0].Evidence);
        Assert.AreEqual(.7, result[0].DetectionConfidence);
        var reordered = ParticipantWordTextRegionRecovery.SelectCandidates(
            image, components.Reverse().ToArray(), [word], [recognized], Plot);
        Assert.AreEqual(result[0].RegionId, reordered[0].RegionId);
        Assert.AreEqual(result[0].Polygon.Bounds, reordered[0].Polygon.Bounds);
    }

    [TestMethod]
    [DataRow(OcrReviewStatus.Accepted)]
    [DataRow(OcrReviewStatus.Corrected)]
    [DataRow(OcrReviewStatus.Rejected)]
    public void HumanDecisionsRemainUntouched(OcrReviewStatus status)
    {
        var (image, word, recognized, components) = Fixture();
        Assert.IsEmpty(ParticipantWordTextRegionRecovery.SelectCandidates(
            image, components, [word], [recognized with { ReviewStatus = status }], Plot));
    }

    [TestMethod]
    public void RequiresBoundOriginalHorizontalNonnumericPeripheralWords()
    {
        var (image, word, recognized, components) = Fixture();
        foreach (var changed in new[]
        {
            recognized with { RegionId = "unbound" },
            recognized with { Text = "150" },
            recognized with { Text = "T" },
            recognized with { Role = OcrTextRole.YTick },
            recognized with { Role = OcrTextRole.PhaseHeading },
            recognized with { SourceImage = OcrSourceImage.Enhanced },
            recognized with { Polygon = OcrPolygon.FromRectangle(new OcrRectangle(31, 25, 39, 12)) },
        })
            Assert.IsEmpty(ParticipantWordTextRegionRecovery.SelectCandidates(image, components, [word], [changed], Plot));
        Assert.IsEmpty(ParticipantWordTextRegionRecovery.SelectCandidates(image, components,
            [word with { OrientationDegrees = 90 }], [recognized], Plot));
        Assert.IsEmpty(ParticipantWordTextRegionRecovery.SelectCandidates(image, components, [word], [recognized],
            new OcrRectangle(20, 60, 200, 70)));
        Assert.IsEmpty(ParticipantWordTextRegionRecovery.SelectCandidates(image, components, [word], [recognized],
            new OcrRectangle(120, 30, 100, 100)));
    }

    [TestMethod]
    [DataRow(85, 25, 8, 12)] // detached word
    [DataRow(72, 44, 8, 12)] // different line
    [DataRow(72, 25, 30, 12)] // horizontal graph stroke
    [DataRow(72, 22, 3, 30)] // tall graph stroke
    [DataRow(68, 25, 4, 12)] // minor edge padding only
    public void RejectsDetachedMisalignedStructuralAndPaddingOnlyExtensions(int x, int y, int width, int height)
    {
        var (image, word, recognized, _) = Fixture();
        var component = OcrTestFixtures.Region("other", x, y, width, height);
        Assert.IsEmpty(ParticipantWordTextRegionRecovery.SelectCandidates(image, [component], [word], [recognized], Plot));
    }

    [TestMethod]
    public void DoesNotAbsorbAnotherDetectedLabel()
    {
        var (image, word, recognized, _) = Fixture();
        var other = OcrTestFixtures.Region("other", 72, 25, 8, 12);
        Assert.IsEmpty(ParticipantWordTextRegionRecovery.SelectCandidates(image, [other], [word, other], [recognized], Plot));
    }

    [TestMethod]
    public async Task RequiresOriginalPixelsValidGeometryAndCancellation()
    {
        var (image, word, recognized, components) = Fixture();
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await ParticipantWordTextRegionRecovery.FindAsync(
            image with { SourceImage = OcrSourceImage.Enhanced }, [word], [recognized], Plot));
        Assert.ThrowsExactly<ArgumentException>(() => ParticipantWordTextRegionRecovery.SelectCandidates(
            image, components, [word], [recognized], Plot with { Width = double.NaN }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => ParticipantWordTextRegionRecovery.SelectCandidates(
            image, components, [word], [recognized], Plot, cancellation.Token));
    }

    [TestMethod]
    [DataRow("Reader 7", 1)]
    [DataRow("Unclear", 2)]
    public async Task PipelineRecognizesTheOriginalCropAndRetainsDisagreement(string reading, int count)
    {
        var (image, word, _, _) = Fixture();
        byte[] original = image.Pixels.ToArray();
        OcrCrop? completedCrop = null;
        var recognizer = new StubTextRecognizer((crops, _) =>
        {
            completedCrop = crops.FirstOrDefault(crop => crop.RegionId.StartsWith("participant-word:", StringComparison.Ordinal)) ?? completedCrop;
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop => new OcrRecognition(
                crop.RegionId, crop.SourceImage,
                [new OcrRecognitionAlternative(crop.RegionId == "word" ? "Read" : reading, .95, crop.SourceImage)], .1)).ToArray());
        });
        var request = OcrTestFixtures.Request([word]) with { OriginalImage = image, PlotBounds = Plot };
        var cache = new InMemoryOcrResultCache();
        var detector = new StubTextRegionDetector([]);
        var baseline = await new OcrPipeline(detector, recognizer, cache).RecognizeAsync(request);
        var pipeline = new OcrPipeline(detector, recognizer, cache,
            new OcrPipelineOptions { EnableParticipantLaneAssembly = true });
        var result = await pipeline.RecognizeAsync(request);
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(count, result.Regions);
        Assert.IsNotNull(completedCrop);
        Assert.AreEqual(OcrSourceImage.Original, completedCrop.SourceImage);
        Assert.AreEqual(new OcrRectangle(24, 23, 77, 14), completedCrop.OriginalPolygon.Bounds);
        Assert.IsTrue(result.Regions.All(static region => region.ReviewStatus == OcrReviewStatus.Unreviewed));
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Contains("original_pixel_participant_word_recovery", StringComparison.Ordinal)));
        if (count == 2) Assert.IsTrue(result.Regions.Any(static region => region.Text == "Read"));
        else Assert.AreEqual(reading, result.Regions.Single().Text);
        Assert.AreNotEqual(baseline.Cache.CacheKey, result.Cache.CacheKey);
        Assert.IsTrue((await pipeline.RecognizeAsync(request)).Cache.CacheHit);
        Assert.AreEqual(2, recognizer.CallCount);
        CollectionAssert.AreEqual(original, image.Pixels.ToArray());
    }

    [TestMethod]
    public async Task NumericWordMisreadStaysVisibleWithoutBecomingAnAxisTick()
    {
        var (image, word, _, _) = Fixture();
        byte[] pixels = Enumerable.Repeat((byte)255, image.Width * 600).ToArray();
        image.Pixels.Span.CopyTo(pixels);
        var plot = new OcrRectangle(120, 40, 100, 500);
        var recognizer = new StubTextRecognizer((crops, _) =>
            ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop => new OcrRecognition(
                crop.RegionId, crop.SourceImage,
                [new OcrRecognitionAlternative(crop.RegionId == "word" ? "Read" : "150", .95, crop.SourceImage)], .1)).ToArray()));
        var request = OcrTestFixtures.Request([word]) with
        {
            OriginalImage = image with { Height = 600, Pixels = pixels }, PlotBounds = plot,
        };
        var result = await new OcrPipeline(new StubTextRegionDetector([]), recognizer, new InMemoryOcrResultCache(),
            new OcrPipelineOptions { EnableParticipantLaneAssembly = true }).RecognizeAsync(request);
        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        OcrRegion recovered = result.Regions.Single(static region => region.RegionId.StartsWith("participant-word:", StringComparison.Ordinal));
        Assert.AreEqual("150", recovered.Text);
        // Prove the unguarded geometric classifier really would produce a tick.
        Assert.AreEqual(OcrTextRole.YTick, GraphTextRoleClassifier.Classify(
            new OcrDetectedRegion(recovered.RegionId, recovered.Polygon, 0, .9), recovered.Text, plot).Role);
        Assert.AreEqual(OcrTextRole.Other, recovered.Role);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, recovered.ReviewStatus);
        Assert.IsTrue(result.Regions.Any(static region => region.RegionId == "word" && region.Text == "Read"));
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Contains("original_pixel_participant_word_recovery", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SeparateWordBatchKeepsExistingRecoveredTickPixelsIdentical()
    {
        var (image, word, _, _) = Fixture();
        OcrDetectedRegion[] detected = [word,
            OcrTestFixtures.Region("one", 130, 133, 5, 6, context: new OcrRegionContext(ExplicitRoleHint: OcrTextRole.XTick)),
            OcrTestFixtures.Region("two", 157, 133, 5, 6, context: new OcrRegionContext(ExplicitRoleHint: OcrTextRole.XTick)),
            OcrTestFixtures.Region("four", 210, 133, 5, 6, context: new OcrRegionContext(ExplicitRoleHint: OcrTextRole.XTick))];
        byte[] pixels = image.Pixels.ToArray();
        foreach (int start in new[] { 130, 157, 184, 210 })
            for (int y = 133; y < 139; y++)
                for (int x = start; x < start + 5; x++) pixels[y * image.Stride + x] = 0;
        var request = OcrTestFixtures.Request(detected) with { OriginalImage = image with { Pixels = pixels }, PlotBounds = Plot };
        var before = new List<OcrCrop>();
        var after = new List<OcrCrop>();
        foreach (bool withCompletion in new[] { false, true })
        {
            var recognizer = new StubTextRecognizer((crops, _) =>
            {
                (withCompletion ? after : before).AddRange(crops);
                return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select(crop => new OcrRecognition(
                    crop.RegionId, crop.SourceImage, [new OcrRecognitionAlternative(crop.RegionId == "word" ? "Read" :
                        crop.RegionId == "one" ? "1" : crop.RegionId == "two" ? "2" : crop.RegionId == "four" ? "4" :
                        crop.RegionId.StartsWith("participant-word:", StringComparison.Ordinal) ? "Reader 7" : "3", .95, crop.SourceImage)], .1)).ToArray());
            });
            var result = await new OcrPipeline(new StubTextRegionDetector([]), recognizer, new InMemoryOcrResultCache(),
                new OcrPipelineOptions { EnableParticipantLaneAssembly = withCompletion, EnableTickLaneRecovery = true,
                    CropWidthMode = OcrCropWidthMode.PaddleBatchMaximumAspectRatio, CropWidth = 32, CropHeight = 32,
                    CropPaddingPixels = 0, InferVerticalOrientationForTallRegions = false }).RecognizeAsync(request);
            Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        }
        var originalTick = before.Single(crop => crop.RegionId.StartsWith("tick-lane:", StringComparison.Ordinal));
        var unchangedTick = after.Single(crop => crop.RegionId == originalTick.RegionId);
        Assert.IsTrue(after.Single(crop => crop.RegionId.StartsWith("participant-word:", StringComparison.Ordinal)).Width > originalTick.Width);
        Assert.AreEqual(originalTick.CropSha256, unchangedTick.CropSha256);
        CollectionAssert.AreEqual(originalTick.Pixels.ToArray(), unchangedTick.Pixels.ToArray());
    }

    private static (OcrImage Image, OcrDetectedRegion Word, OcrRegion Recognized, OcrDetectedRegion[] Components) Fixture()
    {
        var word = OcrTestFixtures.Region("word", 30, 25, 40, 12);
        OcrDetectedRegion[] components = [
            OcrTestFixtures.Region("left", 24, 25, 10, 12, confidence: .7),
            OcrTestFixtures.Region("middle1", 38, 25, 8, 12),
            OcrTestFixtures.Region("middle2", 50, 25, 8, 12),
            OcrTestFixtures.Region("edge", 66, 25, 12, 12),
            OcrTestFixtures.Region("suffix", 82, 27, 8, 10),
            OcrTestFixtures.Region("number", 94, 27, 7, 10),
            OcrTestFixtures.Region("dot", 95, 23, 2, 2),
            OcrTestFixtures.Region("detached", 118, 25, 8, 12)];
        byte[] pixels = Enumerable.Repeat((byte)255, 240 * 140).ToArray();
        foreach (var bounds in components.Select(static region => region.Polygon.Bounds))
            for (int y = (int)bounds.Top; y < bounds.Bottom; y++)
                for (int x = (int)bounds.Left; x < bounds.Right; x++) pixels[y * 240 + x] = 0;
        var image = new OcrImage(240, 140, 240, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
        var recognized = new OcrRegion(word.RegionId, word.Polygon, "Read", [], OcrTextRole.Other,
            .9, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);
        return (image, word, recognized, components);
    }
}
