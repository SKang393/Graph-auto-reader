// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class AxisLabelGroupingReproductionTests
{
    private static readonly double[] ExpectedLabelPositions = [30, 50, 70];
    [TestMethod]
    public async Task NearbyAxisLabelsMergeIntoOneRegionBeforeTickAssociation()
    {
        OcrImage image = Ink((30, 75, 3, 8), (50, 75, 3, 8), (70, 75, 3, 8));
        IReadOnlyList<OcrDetectedRegion> regions = await new ConnectedComponentTextRegionDetector()
            .DetectAsync(image, CancellationToken.None);

        // Three separated numeric-glyph-shaped components on a row become one OCR crop.
        Assert.HasCount(1, regions);
        Assert.AreEqual(new OcrRectangle(30, 75, 43, 8), regions[0].Polygon.Bounds);
        Assert.AreEqual(3, regions[0].Evidence!.ComponentCount);
    }

    [TestMethod]
    public async Task AdjacentDigitsAndDecimalRemainOneRegion()
    {
        OcrImage image = Ink((30, 75, 3, 8), (36, 75, 3, 8), (42, 81, 2, 2));
        IReadOnlyList<OcrDetectedRegion> regions = await new ConnectedComponentTextRegionDetector()
            .DetectAsync(image, CancellationToken.None);

        Assert.HasCount(1, regions);
        Assert.AreEqual(new OcrRectangle(30, 75, 14, 8), regions[0].Polygon.Bounds);
        Assert.AreEqual(3, regions[0].Evidence!.ComponentCount);
    }

    [TestMethod]
    public async Task MeasuredBoundariesSeparateLabelsWithoutSplittingDecimalOrCrossingInk()
    {
        var detector = new ConnectedComponentTextRegionDetector();
        var context = new OcrMeasuredTickGrouping([31, 51, 71], 65);
        OcrImage separate = Ink((30, 75, 3, 8), (50, 75, 3, 8), (70, 75, 3, 8));
        var regions = await detector.DetectAsync(separate, context, CancellationToken.None);
        Assert.HasCount(3, regions);
        CollectionAssert.AreEqual(ExpectedLabelPositions, regions.Select(region => region.Polygon.Bounds.X).ToArray());
        OcrImage decimalValue = Ink((30, 75, 3, 8), (36, 75, 3, 8), (42, 81, 2, 2));
        regions = await detector.DetectAsync(decimalValue, new OcrMeasuredTickGrouping([36, 71], 65), CancellationToken.None);
        Assert.HasCount(1, regions);
        OcrImage crossing = Ink((30, 75, 3, 8), (38, 75, 7, 8), (50, 75, 3, 8));
        regions = await detector.DetectAsync(crossing, context, CancellationToken.None);
        Assert.HasCount(1, regions);
        Assert.AreEqual(new OcrRectangle(30, 75, 23, 8), regions[0].Polygon.Bounds);
    }

    [TestMethod]
    public async Task LowerCaptionCrossingDoesNotVetoTickLabels()
    {
        OcrImage image = Ink((30, 75, 3, 8), (50, 75, 3, 8), (70, 75, 3, 8), (37, 90, 9, 2));
        var detector = new ConnectedComponentTextRegionDetector();
        var regions = await detector.DetectAsync(image, new OcrMeasuredTickGrouping([31, 51, 71], 65), CancellationToken.None);
        Assert.AreEqual(3, regions.Count(region => region.Polygon.Bounds.Y == 75));
    }

    [TestMethod]
    public async Task MinorTicksDoNotSplitOneWideDecimalLabel()
    {
        OcrImage image = Ink((30, 75, 7, 20), (42, 75, 7, 20), (51, 92, 3, 3), (60, 75, 11, 20));
        var detector = new ConnectedComponentTextRegionDetector();
        var context = new OcrMeasuredTickGrouping([31, 51, 71], 65);
        var baseline = await detector.DetectAsync(image, CancellationToken.None);
        var actual = await detector.DetectAsync(image, context, CancellationToken.None);
        Assert.HasCount(1, baseline);
        Assert.HasCount(1, actual);
        Assert.AreEqual(baseline[0].Polygon.Bounds, actual[0].Polygon.Bounds);
        Assert.AreEqual(new OcrRectangle(30, 75, 41, 20), actual[0].Polygon.Bounds);
    }

    private static OcrImage Ink(params (int X, int Y, int Width, int Height)[] components)
    {
        const int width = 160, height = 100;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        foreach (var item in components)
            for (int y = item.Y; y < item.Y + item.Height; y++)
                for (int x = item.X; x < item.X + item.Width; x++) pixels[y * width + x] = 0;
        return new(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
    }
}
