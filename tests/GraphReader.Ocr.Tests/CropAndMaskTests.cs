// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class CropAndMaskTests
{
    private static readonly float[] ExpectedTwoPixelCrop = [0f, 1f];
    private static readonly string[] ExpectedDynamicRegionOrder = ["ratio-1", "ratio-8"];

    [TestMethod]
    public void TinyRegionIsNormalizedWithoutLosingItsOriginalPolygon()
    {
        OcrDetectedRegion tiny = OcrTestFixtures.Region("tiny", 20, 30, 2, 3);

        IReadOnlyList<IReadOnlyList<OcrCrop>> batches = OcrCropBatcher.CreateBatches(
            OcrTestFixtures.Image(),
            [tiny],
            new OcrCropBatcherOptions { TargetWidth = 64, TargetHeight = 24, BatchSize = 8 });

        Assert.HasCount(1, batches);
        Assert.HasCount(1, batches[0]);
        OcrCrop crop = batches[0][0];
        Assert.AreEqual(64, crop.Width);
        Assert.AreEqual(24, crop.Height);
        Assert.AreEqual(64 * 24, crop.Pixels.Length);
        Assert.AreEqual(64, crop.CropSha256.Length);
        Assert.IsNotNull(crop.SourceCrop);
        Assert.AreEqual(64, crop.SourceCrop.PixelSha256.Length);
        CollectionAssert.AreEqual(tiny.Polygon.Points.ToArray(), crop.OriginalPolygon.Points.ToArray());
        Assert.IsTrue(crop.Pixels.Span.ToArray().All(float.IsFinite));
    }

    [TestMethod]
    public void DefaultResizePreservesAspectRatioAndPadsWithNormalizedZeroSourceValue()
    {
        var pixels = Enumerable.Repeat((byte)255, 20 * 10).ToArray();
        var image = new OcrImage(
            20,
            10,
            20,
            pixels,
            OcrSourceImage.Original,
            OcrFrameTransform.Identity);
        OcrDetectedRegion region = OcrTestFixtures.Region("wide", 0, 0, 20, 10);

        OcrCrop crop = OcrCropBatcher.CreateBatches(
            image,
            [region],
            new OcrCropBatcherOptions
            {
                TargetWidth = 40,
                TargetHeight = 10,
                PaddingPixels = 0,
            })[0][0];

        for (var y = 0; y < crop.Height; y++)
        {
            ReadOnlySpan<float> row = crop.Pixels.Span.Slice(y * crop.Width, crop.Width);
            Assert.IsTrue(row[..20].ToArray().All(static value => value == 1f));
            Assert.IsTrue(row[20..].ToArray().All(static value => value == 0.5f));
        }
    }

    [TestMethod]
    public void StretchModeRemainsAvailableForModelsWithFixedWarpedTrainingInputs()
    {
        var image = new OcrImage(
            20,
            10,
            20,
            Enumerable.Repeat((byte)255, 20 * 10).ToArray(),
            OcrSourceImage.Original,
            OcrFrameTransform.Identity);
        OcrDetectedRegion region = OcrTestFixtures.Region("wide", 0, 0, 20, 10);

        OcrCrop crop = OcrCropBatcher.CreateBatches(
            image,
            [region],
            new OcrCropBatcherOptions
            {
                TargetWidth = 40,
                TargetHeight = 10,
                PaddingPixels = 0,
                ResizeMode = OcrCropResizeMode.Stretch,
            })[0][0];

        Assert.IsTrue(crop.Pixels.Span.ToArray().All(static value => value == 1f));
    }

    [TestMethod]
    public void EqualSizeResizeKeepsSourcePixelCenters()
    {
        var image = new OcrImage(
            2,
            1,
            2,
            new byte[] { 0, 255 },
            OcrSourceImage.Original,
            OcrFrameTransform.Identity);
        OcrDetectedRegion region = OcrTestFixtures.Region("two-pixels", 0, 0, 2, 1);

        OcrCrop crop = OcrCropBatcher.CreateBatches(
            image,
            [region],
            new OcrCropBatcherOptions
            {
                TargetWidth = 2,
                TargetHeight = 1,
                PaddingPixels = 0,
                ResizeMode = OcrCropResizeMode.Stretch,
            })[0][0];

        CollectionAssert.AreEqual(ExpectedTwoPixelCrop, crop.Pixels.ToArray());
    }

    [TestMethod]
    public void EqualSizeResizePreservesInterleavedBgrAndBindsItIntoCropHash()
    {
        var grayscale = new byte[] { 0, 255 };
        var image = new OcrImage(
            2,
            1,
            2,
            grayscale,
            OcrSourceImage.Original,
            OcrFrameTransform.Identity,
            BgrPixels: new OcrBgrBytePixels(6, new byte[] { 10, 20, 30, 40, 50, 60 }));
        OcrDetectedRegion region = OcrTestFixtures.Region("bgr-two-pixels", 0, 0, 2, 1);
        var options = new OcrCropBatcherOptions
        {
            TargetWidth = 2,
            TargetHeight = 1,
            PaddingPixels = 0,
            ResizeMode = OcrCropResizeMode.Stretch,
        };

        OcrCrop colorCrop = OcrCropBatcher.CreateBatches(image, [region], options)[0][0];
        OcrCrop grayscaleCrop = OcrCropBatcher.CreateBatches(
            image with { BgrPixels = null },
            [region],
            options)[0][0];

        Assert.IsNotNull(colorCrop.BgrPixels);
        CollectionAssert.AreEqual(
            new float[] { 10f / 255f, 20f / 255f, 30f / 255f, 40f / 255f, 50f / 255f, 60f / 255f },
            colorCrop.BgrPixels.Pixels.ToArray());
        Assert.AreNotEqual(grayscaleCrop.CropSha256, colorCrop.CropSha256);
    }

    [TestMethod]
    public void RelativeVerticalPaddingKeepsTightGlyphBelowStructuralHeightAndUsesWhitePadding()
    {
        var pixels = Enumerable.Repeat((byte)255, 24 * 24).ToArray();
        for (var y = 6; y < 18; y++)
        {
            for (var x = 8; x < 12; x++)
            {
                pixels[(y * 24) + x] = 0;
            }
        }

        var image = new OcrImage(
            24,
            24,
            24,
            pixels,
            OcrSourceImage.Original,
            OcrFrameTransform.Identity);
        OcrDetectedRegion region = OcrTestFixtures.Region("tight-glyph", 8, 6, 4, 12);

        OcrCrop crop = OcrCropBatcher.CreateBatches(
            image,
            [region],
            new OcrCropBatcherOptions
            {
                TargetWidth = 128,
                TargetHeight = 32,
                PaddingPixels = 1,
                VerticalContentPaddingRatio = 0.25,
                PaddingValue = 1f,
            })[0][0];

        int foregroundRows = Enumerable.Range(0, crop.Height)
            .Count(y => crop.Pixels.Span.Slice(y * crop.Width, crop.Width)
                .ToArray()
                .Any(static value => value < 0.5f));
        Assert.IsLessThan(24, foregroundRows);
        Assert.IsTrue(crop.Pixels.Span.ToArray().TakeLast(64).All(static value => value == 1f));
    }

    [TestMethod]
    public void EnhancedCropMapsOriginalPolygonThroughDeclaredTransform()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("scaled", 20, 30, 12, 8);
        OcrImage enhanced = OcrTestFixtures.Image(
            OcrSourceImage.Enhanced,
            width: 320,
            height: 200,
            transform: new OcrFrameTransform(2, 2, 0, 0));

        OcrCrop crop = OcrCropBatcher.CreateBatches(enhanced, [region])[0][0];

        Assert.AreEqual(OcrSourceImage.Enhanced, crop.SourceImage);
        CollectionAssert.AreEqual(region.Polygon.Points.ToArray(), crop.OriginalPolygon.Points.ToArray());
        Assert.IsNotEmpty(crop.Pixels.Span.ToArray());
    }

    [TestMethod]
    public void CropBatcherHonorsPreCanceledRequest()
    {
        var cancellation = new CancellationToken(canceled: true);

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            OcrCropBatcher.CreateBatches(
                OcrTestFixtures.Image(),
                [OcrTestFixtures.Region("cancel", 20, 30, 12, 8)],
                cancellationToken: cancellation));
    }

    [TestMethod]
    public void PaddleDynamicWidthSortsByAspectRatioAndUsesEachBatchMaximum()
    {
        OcrImage image = OcrTestFixtures.Image(width: 800, height: 180);
        OcrDetectedRegion[] regions =
        [
            OcrTestFixtures.Region("ratio-10", 0, 0, 480, 48),
            OcrTestFixtures.Region("ratio-1", 0, 60, 48, 48),
            OcrTestFixtures.Region("ratio-8", 100, 60, 384, 48),
        ];

        IReadOnlyList<IReadOnlyList<OcrCrop>> batches = OcrCropBatcher.CreateBatches(
            image,
            regions,
            new OcrCropBatcherOptions
            {
                TargetWidth = 320,
                TargetHeight = 48,
                MaximumTargetWidth = 512,
                BatchSize = 2,
                PaddingPixels = 0,
                WidthMode = OcrCropWidthMode.PaddleBatchMaximumAspectRatio,
            });

        Assert.HasCount(2, batches);
        CollectionAssert.AreEqual(
            ExpectedDynamicRegionOrder,
            batches[0].Select(static crop => crop.RegionId).ToArray());
        Assert.IsTrue(batches[0].All(static crop => crop.Width == 384));
        Assert.AreEqual("ratio-10", batches[1][0].RegionId);
        Assert.AreEqual(480, batches[1][0].Width);
    }

    [TestMethod]
    public void PaddleDynamicWidthRejectsRatherThanTruncatingAboveBound()
    {
        OcrImage image = OcrTestFixtures.Image(width: 800, height: 100);
        OcrDetectedRegion region = OcrTestFixtures.Region("too-wide", 0, 0, 500, 48);

        InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            OcrCropBatcher.CreateBatches(
                image,
                [region],
                new OcrCropBatcherOptions
                {
                    TargetWidth = 320,
                    TargetHeight = 48,
                    MaximumTargetWidth = 400,
                    PaddingPixels = 0,
                    WidthMode = OcrCropWidthMode.PaddleBatchMaximumAspectRatio,
                }));

        StringAssert.Contains(exception.Message, "above the reviewed bound 400");
    }

    [TestMethod]
    public void OptionalPaddingBoundSplitsWideCaptionWithoutDroppingTickEvidence()
    {
        OcrImage image = OcrTestFixtures.Image(width: 2000, height: 120);
        OcrDetectedRegion[] regions = Enumerable.Range(0, 7)
            .Select(index => OcrTestFixtures.Region($"tick-{index}", index * 50, 0, 48, 48))
            .Append(OcrTestFixtures.Region("caption", 0, 60, 1920, 48)).ToArray();
        var options = new OcrCropBatcherOptions
        {
            TargetWidth = 320, TargetHeight = 48, MaximumTargetWidth = 2048,
            BatchSize = 16, PaddingPixels = 0, WidthMode = OcrCropWidthMode.PaddleBatchMaximumAspectRatio,
        };
        var legacy = OcrCropBatcher.CreateBatches(image, regions, options);
        var bounded = OcrCropBatcher.CreateBatches(image, regions, options with { MaximumPaddingOverhead = 2 });
        Assert.HasCount(1, legacy);
        Assert.HasCount(2, bounded);
        Assert.AreEqual(737280, legacy.Sum(batch => batch.Sum(crop => crop.Width * crop.Height)));
        Assert.AreEqual(199680, bounded.Sum(batch => batch.Sum(crop => crop.Width * crop.Height)));
        OcrCrop[] oldCrops = legacy.SelectMany(batch => batch).ToArray();
        OcrCrop[] newCrops = bounded.SelectMany(batch => batch).ToArray();
        CollectionAssert.AreEqual(oldCrops.Select(crop => crop.RegionId).ToArray(), newCrops.Select(crop => crop.RegionId).ToArray());
        for (int i = 0; i < oldCrops.Length; i++)
        {
            Assert.AreEqual(oldCrops[i].OriginalPolygon, newCrops[i].OriginalPolygon);
            Assert.AreEqual(oldCrops[i].SourceImage, newCrops[i].SourceImage);
            for (int y = 0; y < newCrops[i].Height; y++)
                CollectionAssert.AreEqual(oldCrops[i].Pixels.Slice(y * oldCrops[i].Width, newCrops[i].Width).ToArray(),
                    newCrops[i].Pixels.Slice(y * newCrops[i].Width, newCrops[i].Width).ToArray());
        }
    }

    [TestMethod]
    [DataRow(1600d, 48d)]
    [DataRow(1901d, 57d)]
    public void PaddingSplitPreservesFractionalContentAndLegacyMaximumClamp(
        double captionWidth,
        double captionHeight)
    {
        OcrImage grayscale = OcrTestFixtures.Image(width: 2000, height: 160);
        OcrImage image = grayscale with
        {
            BgrPixels = new OcrBgrBytePixels(grayscale.Width * 3,
                Enumerable.Range(0, grayscale.Width * grayscale.Height * 3)
                    .Select(static index => (byte)((index * 29) % 253)).ToArray()),
        };
        OcrDetectedRegion[] regions = Enumerable.Range(0, 7)
            .Select(index => OcrTestFixtures.Region($"tick-{index}", index * 50, 0, 200, 19))
            .Append(OcrTestFixtures.Region("caption", 0, 80, captionWidth, captionHeight))
            .ToArray();
        var options = new OcrCropBatcherOptions
        {
            TargetWidth = 320,
            TargetHeight = 48,
            MaximumTargetWidth = 1601,
            BatchSize = 16,
            PaddingPixels = 0,
            WidthMode = OcrCropWidthMode.PaddleBatchMaximumAspectRatio,
        };

        var legacy = OcrCropBatcher.CreateBatches(image, regions, options);
        var bounded = OcrCropBatcher.CreateBatches(image, regions,
            options with { MaximumPaddingOverhead = 2 });

        Assert.HasCount(1, legacy);
        Assert.IsTrue(legacy[0].All(static crop => crop.Width == 1600));
        Assert.HasCount(2, bounded);
        Assert.HasCount(7, bounded[0]);
        // 48 * 200 / 19 needs 506 content samples, despite a floored
        // individual target width of 505. The widest fractional caption must
        // retain its original 1600-sample clamp rather than grow to 1601.
        Assert.IsTrue(bounded[0].All(static crop => crop.Width == 506));
        Assert.AreEqual(1600, bounded[1][0].Width);
        Assert.AreEqual(614400, legacy.Sum(batch => batch.Sum(crop => crop.Pixels.Length)));
        Assert.AreEqual(246816, bounded.Sum(batch => batch.Sum(crop => crop.Pixels.Length)));
        AssertPreservedCropSamples(legacy, bounded);
    }

    [TestMethod]
    public void PaddingSplitKeepsOriginalBatchBoundariesAndMinimumWidthCostBound()
    {
        OcrImage image = OcrTestFixtures.Image(width: 2400, height: 120);
        OcrDetectedRegion[] regions = Enumerable.Range(0, 3)
            .Select(index => OcrTestFixtures.Region($"small-{index}", index * 50, 0, 48, 48))
            .Append(OcrTestFixtures.Region("medium", 0, 60, 1000, 48))
            .Concat(Enumerable.Range(0, 4)
                .Select(index => OcrTestFixtures.Region($"wide-{index}", 0, 60, 2000, 48)))
            .ToArray();
        var options = new OcrCropBatcherOptions
        {
            TargetWidth = 320,
            TargetHeight = 48,
            MaximumTargetWidth = 2048,
            BatchSize = 4,
            PaddingPixels = 0,
            WidthMode = OcrCropWidthMode.PaddleBatchMaximumAspectRatio,
        };

        var legacy = OcrCropBatcher.CreateBatches(image, regions, options);
        var bounded = OcrCropBatcher.CreateBatches(image, regions,
            options with { MaximumPaddingOverhead = 2 });

        Assert.HasCount(2, legacy);
        Assert.HasCount(3, bounded);
        Assert.HasCount(3, bounded[0]);
        Assert.IsTrue(bounded[0].All(static crop => crop.Width == 320));
        Assert.HasCount(1, bounded[1]);
        Assert.AreEqual("medium", bounded[1][0].RegionId);
        CollectionAssert.AreEqual(
            legacy[1].Select(static crop => crop.RegionId).ToArray(),
            bounded[2].Select(static crop => crop.RegionId).ToArray());
        Assert.AreEqual(576000, legacy.Sum(batch => batch.Sum(crop => crop.Pixels.Length)));
        Assert.AreEqual(478080, bounded.Sum(batch => batch.Sum(crop => crop.Pixels.Length)));
        foreach (IReadOnlyList<OcrCrop> batch in bounded)
        {
            int individualWidthSum = batch.Sum(crop => Math.Max(options.TargetWidth,
                (int)crop.OriginalPolygon.Bounds.Width));
            Assert.IsTrue(batch.Count * batch[0].Width <= 2 * individualWidthSum);
        }

        AssertPreservedCropSamples(legacy, bounded);
    }

    private static void AssertPreservedCropSamples(
        IReadOnlyList<IReadOnlyList<OcrCrop>> legacy,
        IReadOnlyList<IReadOnlyList<OcrCrop>> bounded)
    {
        OcrCrop[] oldCrops = legacy.SelectMany(static batch => batch).ToArray();
        OcrCrop[] newCrops = bounded.SelectMany(static batch => batch).ToArray();
        CollectionAssert.AreEqual(
            oldCrops.Select(static crop => crop.RegionId).ToArray(),
            newCrops.Select(static crop => crop.RegionId).ToArray());
        for (int index = 0; index < oldCrops.Length; index++)
        {
            OcrCrop original = oldCrops[index];
            OcrCrop split = newCrops[index];
            Assert.AreEqual(original.OriginalPolygon, split.OriginalPolygon);
            Assert.AreEqual(original.SourceImage, split.SourceImage);
            Assert.AreEqual(original.Height, split.Height);
            Assert.AreEqual(original.SourceCrop?.PixelSha256, split.SourceCrop?.PixelSha256);
            for (int y = 0; y < split.Height; y++)
            {
                CollectionAssert.AreEqual(
                    original.Pixels.Slice(y * original.Width, split.Width).ToArray(),
                    split.Pixels.Slice(y * split.Width, split.Width).ToArray());
                if (original.BgrPixels is { } originalBgr)
                {
                    Assert.IsNotNull(split.BgrPixels);
                    CollectionAssert.AreEqual(
                        originalBgr.Pixels.Slice(y * originalBgr.Stride, split.Width * 3).ToArray(),
                        split.BgrPixels.Pixels.Slice(y * split.BgrPixels.Stride, split.Width * 3).ToArray());
                }
            }
        }
    }

    [TestMethod]
    public void PaddingBoundRejectsInvalidPolicyOversizeAndCancellation()
    {
        OcrImage image = OcrTestFixtures.Image(width: 800, height: 100);
        OcrDetectedRegion region = OcrTestFixtures.Region("wide", 0, 0, 500, 48);
        var options = new OcrCropBatcherOptions { TargetWidth = 320, TargetHeight = 48, MaximumTargetWidth = 400,
            PaddingPixels = 0, WidthMode = OcrCropWidthMode.PaddleBatchMaximumAspectRatio, MaximumPaddingOverhead = 2 };
        Assert.ThrowsExactly<InvalidDataException>(() => OcrCropBatcher.CreateBatches(image, [region], options));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => OcrCropBatcher.CreateBatches(image, [], options with { MaximumPaddingOverhead = .99 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => OcrCropBatcher.CreateBatches(image, [], options with { MaximumPaddingOverhead = double.NaN }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => OcrCropBatcher.CreateBatches(image, [], options with { WidthMode = OcrCropWidthMode.Fixed }));
        Assert.ThrowsExactly<OperationCanceledException>(() => OcrCropBatcher.CreateBatches(image, [region], options,
            new CancellationToken(canceled: true)));
    }

    [TestMethod]
    public void MarkerExclusionMasksArePaddedClippedAndOriginalPixelBased()
    {
        OcrDetectedRegion[] textRegions =
        [
            OcrTestFixtures.Region("left-edge", 0, 10, 8, 6, confidence: 0.81),
            OcrTestFixtures.Region("generalization", 70, 40, 28, 8, confidence: 0.94),
        ];

        IReadOnlyList<OcrMask> masks = OcrMaskBuilder.Build(
            textRegions,
            originalWidth: 100,
            originalHeight: 80,
            paddingPixels: 2);

        Assert.HasCount(2, masks);
        Assert.AreEqual(new OcrRectangle(0, 8, 10, 10), masks[0].Polygon.Bounds);
        Assert.AreEqual(new OcrRectangle(68, 38, 32, 12), masks[1].Polygon.Bounds);
        Assert.IsTrue(masks.All(static mask => mask.CoordinateSpace == OcrContract.CoordinateSpace));
        Assert.AreEqual(0.94d, masks[1].Confidence, 1e-9);
    }

    [TestMethod]
    public void NonOriginalRegionCannotBecomeAMarkerMask()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("derived", 20, 30, 12, 8) with
        {
            CoordinateSpace = "enhanced_pixels",
        };

        Assert.Throws<ArgumentException>(() => OcrMaskBuilder.Build([region], 100, 80));
    }

    [TestMethod]
    public async Task OffsetAndScaledFrameKeepsMaskInCanonicalOriginalPixels()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("offset", 520, 120, 20, 8);
        OcrImage offsetFrame = OcrTestFixtures.Image(
            OcrSourceImage.Original,
            width: 200,
            height: 100,
            transform: new OcrFrameTransform(2, 2, -1000, -220)) with
        {
            CanonicalOriginalWidth = 1000,
            CanonicalOriginalHeight = 800,
        };
        var alternatives = new Dictionary<
            (string RegionId, OcrSourceImage Source),
            IReadOnlyList<OcrRecognitionAlternative>>
        {
            [("offset", OcrSourceImage.Original)] =
                [new OcrRecognitionAlternative("100", 0.95, OcrSourceImage.Original)],
        };
        var pipeline = new OcrPipeline(
            new StubTextRegionDetector([]),
            new StubTextRecognizer(alternatives),
            new InMemoryOcrResultCache(),
            batchSize: 4);
        var request = new OcrRequest(
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            offsetFrame,
            new OcrRectangle(500, 100, 100, 50),
            DetectedRegions: [region],
            TransformChain: "crop:500,110;scale:2");

        OcrResult result = await pipeline.RecognizeAsync(request, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Failure?.TechnicalMessage);
        Assert.HasCount(1, result.Masks);
        Assert.AreEqual(new OcrRectangle(519, 119, 22, 10), result.Masks[0].Polygon.Bounds);
        Assert.AreEqual(OcrContract.CoordinateSpace, result.Masks[0].CoordinateSpace);
    }
}
