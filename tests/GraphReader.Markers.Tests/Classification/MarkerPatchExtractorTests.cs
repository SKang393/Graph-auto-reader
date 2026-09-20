// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Markers.Tests.Classification;

[TestClass]
public sealed class MarkerPatchExtractorTests
{
    [TestMethod]
    public void IsolatedSymbolMatchesCleanPixelsAndLeavesOtherMarkerCropsUnchanged()
    {
        float[] dirty = Enumerable.Repeat(1f, ClassificationTestSupport.FrameSizeSquared).ToArray();
        float[] clean = [.. dirty];
        for (int y = 14; y < 19; y++)
            for (int x = 14; x < 19; x++)
                dirty[y * 32 + x] = clean[y * 32 + x] = (x + y) % 2 == 0 ? 0 : 0.5f;
        for (int y = 9; y < 24; y++)
            for (int x = 9; x < 24; x++)
                if (x < 14 || x >= 19 || y < 14 || y >= 19) dirty[y * 32 + x] = 0;
        float[] unchanged = [.. dirty];
        MarkerCenter[] markers = [ClassificationTestSupport.Marker("symbol", 16, 16, 3),
            ClassificationTestSupport.Marker("point", 8, 8, 3)];
        var options = new MarkerPatchExtractionOptions(32, 32, 1);
        var extractor = new MarkerPatchExtractor();
        var baseline = extractor.Extract(ClassificationTestSupport.Frame(dirty), markers, options, CancellationToken.None);
        var expected = extractor.Extract(ClassificationTestSupport.Frame(clean), markers, options, CancellationToken.None);
        var actual = extractor.Extract(ClassificationTestSupport.Frame(dirty), markers, options with
        {
            OriginalPixelContentBounds = new Dictionary<string, MarkerRectangle>
            { ["symbol"] = new(14, 14, 5, 5) },
        }, CancellationToken.None);
        CollectionAssert.AreEqual(expected[0].ChannelsFirstPixels.ToArray(), actual[0].ChannelsFirstPixels.ToArray());
        CollectionAssert.AreEqual(baseline[1].ChannelsFirstPixels.ToArray(), actual[1].ChannelsFirstPixels.ToArray());
        CollectionAssert.AreEqual(unchanged, dirty);
        Assert.AreEqual(markers[0], actual[0].Marker);
        Assert.IsFalse(baseline[0].ChannelsFirstPixels.Span.SequenceEqual(actual[0].ChannelsFirstPixels.Span));
    }

    [TestMethod]
    [DataRow("missing", 14, 14, 5, 5)]
    [DataRow("symbol", -1, 14, 20, 5)]
    [DataRow("symbol", 14, 14, 25, 5)]
    [DataRow("symbol", 20, 20, 5, 5)]
    public void IsolationRejectsUnknownOrInvalidContentBounds(string id, int x, int y, int width, int height)
    {
        var options = new MarkerPatchExtractionOptions(32, 32, 1)
        {
            OriginalPixelContentBounds = new Dictionary<string, MarkerRectangle> { [id] = new(x, y, width, height) },
        };
        Assert.Throws<ArgumentException>(() => new MarkerPatchExtractor().Extract(ClassificationTestSupport.Frame(),
            [ClassificationTestSupport.Marker("symbol", 16, 16, 3)], options, CancellationToken.None));
    }

    [TestMethod]
    public void IsolationRejectsNonOriginalFrames()
    {
        MarkerImageFrame frame = ClassificationTestSupport.Frame() with { SourceImage = MarkerSourceImage.Enhanced };
        var options = new MarkerPatchExtractionOptions(32, 32, 1)
        {
            OriginalPixelContentBounds = new Dictionary<string, MarkerRectangle> { ["symbol"] = new(14, 14, 5, 5) },
        };
        Assert.Throws<ArgumentException>(() => new MarkerPatchExtractor().Extract(frame,
            [ClassificationTestSupport.Marker("symbol", 16, 16, 3)], options, CancellationToken.None));
    }

    [TestMethod]
    public void ExtractIsDeterministicAndDoesNotMutateTheSourceImage()
    {
        float[] pixels = Enumerable.Range(0, ClassificationTestSupport.FrameSizeSquared)
            .Select(index => index / (float)ClassificationTestSupport.FrameSizeSquared)
            .ToArray();
        float[] original = [.. pixels];
        MarkerImageFrame frame = ClassificationTestSupport.Frame(pixels);
        IReadOnlyList<MarkerCenter> markers =
        [
            ClassificationTestSupport.Marker("circle", 8, 9),
            ClassificationTestSupport.Marker("square", 22, 20, 4),
        ];
        var options = new MarkerPatchExtractionOptions(12, 12, 1);
        var extractor = new MarkerPatchExtractor();

        IReadOnlyList<MarkerPatch> first = extractor.Extract(
            frame,
            markers,
            options,
            CancellationToken.None);
        IReadOnlyList<MarkerPatch> second = extractor.Extract(
            frame,
            markers,
            options,
            CancellationToken.None);

        Assert.HasCount(2, first);
        CollectionAssert.AreEqual(
            first[0].ChannelsFirstPixels.ToArray(),
            second[0].ChannelsFirstPixels.ToArray());
        CollectionAssert.AreEqual(original, frame.ChannelsFirstPixels.ToArray());
        Assert.AreEqual("circle", first[0].Marker.MarkerId);
        Assert.AreEqual(12, first[0].Width);
        Assert.AreEqual(12, first[0].Height);
    }

    [TestMethod]
    public void ExtractMapsOriginalCentersThroughTheDeclaredFrameTransform()
    {
        float[] pixels = Enumerable.Repeat(1f, ClassificationTestSupport.FrameSizeSquared).ToArray();
        pixels[(16 * ClassificationTestSupport.FrameSize) + 16] = 0;
        MarkerImageFrame transformed = new(
            ClassificationTestSupport.FrameSize,
            ClassificationTestSupport.FrameSize,
            1,
            pixels,
            MarkerSourceImage.Enhanced,
            new MarkerAffineTransform(2, 0, 0, 0, 2, 0),
            MarkerMask.Empty(ClassificationTestSupport.FrameSize, ClassificationTestSupport.FrameSize),
            MarkerMask.Empty(ClassificationTestSupport.FrameSize, ClassificationTestSupport.FrameSize));
        MarkerCenter marker = ClassificationTestSupport.Marker("mapped", 8, 8, 2);
        var options = new MarkerPatchExtractionOptions(9, 9, 1)
        {
            RadiusScale = 2,
            MinimumHalfExtentFramePixels = 1,
        };

        MarkerPatch patch = new MarkerPatchExtractor().Extract(
            transformed,
            [marker],
            options,
            CancellationToken.None).Single();

        Assert.AreEqual(1f, patch.ChannelsFirstPixels.Span[(4 * 9) + 4], 1e-6);
        Assert.AreEqual(MarkerContract.CoordinateSpace, patch.Marker.CoordinateSpace);
    }

    [TestMethod]
    public void GrayscaleAndRgbBrightnessBecomeOneInkProbabilityChannel()
    {
        float[] grayscaleWhite = Enumerable.Repeat(1f, ClassificationTestSupport.FrameSizeSquared).ToArray();
        float[] grayscaleBlack = [.. grayscaleWhite];
        grayscaleBlack[(16 * ClassificationTestSupport.FrameSize) + 16] = 0;
        float[] rgb = Enumerable.Repeat(1f, ClassificationTestSupport.FrameSizeSquared * 3).ToArray();
        int center = (16 * ClassificationTestSupport.FrameSize) + 16;
        rgb[center] = 0;
        rgb[ClassificationTestSupport.FrameSizeSquared + center] = 0.5f;
        rgb[(2 * ClassificationTestSupport.FrameSizeSquared) + center] = 1;
        var options = new MarkerPatchExtractionOptions(1, 1, 1)
        {
            RadiusScale = 1,
            MinimumHalfExtentFramePixels = 1,
        };
        MarkerCenter marker = ClassificationTestSupport.Marker("center", 16, 16, 1);
        var extractor = new MarkerPatchExtractor();

        MarkerPatch white = extractor.Extract(
            ClassificationTestSupport.Frame(grayscaleWhite),
            [marker],
            options,
            CancellationToken.None).Single();
        MarkerPatch black = extractor.Extract(
            ClassificationTestSupport.Frame(grayscaleBlack),
            [marker],
            options,
            CancellationToken.None).Single();
        MarkerPatch averagedRgb = extractor.Extract(
            ClassificationTestSupport.Frame(3, rgb),
            [marker],
            options,
            CancellationToken.None).Single();

        Assert.AreEqual(1, white.ChannelCount);
        Assert.AreEqual(1, black.ChannelCount);
        Assert.AreEqual(1, averagedRgb.ChannelCount);
        Assert.AreEqual(0f, white.ChannelsFirstPixels.Span[0], 1e-6);
        Assert.AreEqual(1f, black.ChannelsFirstPixels.Span[0], 1e-6);
        Assert.AreEqual(0.5f, averagedRgb.ChannelsFirstPixels.Span[0], 1e-6);
    }

    [TestMethod]
    public void ExtractPadsOutOfBoundsWithZeroInkWithoutInventingContent()
    {
        MarkerImageFrame frame = ClassificationTestSupport.Frame(
            Enumerable.Repeat(0.25f, ClassificationTestSupport.FrameSizeSquared).ToArray());
        var options = new MarkerPatchExtractionOptions(10, 10, 1)
        {
            RadiusScale = 3,
            MinimumHalfExtentFramePixels = 6,
        };

        MarkerPatch patch = new MarkerPatchExtractor().Extract(
            frame,
            [ClassificationTestSupport.Marker("edge", -20, -20, 3)],
            options,
            CancellationToken.None).Single();

        Assert.IsTrue(patch.ChannelsFirstPixels.Span.ToArray().All(value => value == 0));
    }

    [TestMethod]
    public void ExtractRejectsInvalidGeometryChannelsAndCoordinateSpace()
    {
        var extractor = new MarkerPatchExtractor();
        MarkerImageFrame frame = ClassificationTestSupport.Frame();
        MarkerCenter wrongSpace = ClassificationTestSupport.Marker("wrong-space", 8, 8) with
        {
            CoordinateSpace = "enhanced_pixels",
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => extractor.Extract(
                frame,
                [wrongSpace],
                new MarkerPatchExtractionOptions(8, 8, 1),
                CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(
            () => extractor.Extract(
                frame,
                [ClassificationTestSupport.Marker("valid", 8, 8)],
                new MarkerPatchExtractionOptions(8, 8, 3),
                CancellationToken.None));

        float[] invalidPixels = Enumerable.Repeat(1f, ClassificationTestSupport.FrameSizeSquared).ToArray();
        invalidPixels[0] = 1.01f;
        Assert.ThrowsExactly<ArgumentException>(
            () => extractor.Extract(
                ClassificationTestSupport.Frame(invalidPixels),
                [ClassificationTestSupport.Marker("valid", 8, 8)],
                new MarkerPatchExtractionOptions(8, 8, 1),
                CancellationToken.None));
    }

    [TestMethod]
    public void ExtractPropagatesCancellationBeforeAllocatingPatches()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => new MarkerPatchExtractor().Extract(
                ClassificationTestSupport.Frame(),
                [ClassificationTestSupport.Marker("m1", 8, 8)],
                new MarkerPatchExtractionOptions(8, 8, 1),
                cancellation.Token));
    }
}
