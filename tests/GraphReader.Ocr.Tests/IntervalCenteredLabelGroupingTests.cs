// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class IntervalCenteredLabelGroupingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task IntervalCenteredGlyphsAreRecordedBesideTickCenteredControl()
    {
        int[] ticks = [30, 50, 70, 90];
        var grouping = new OcrMeasuredTickGrouping(ticks, PlotBottom: 65);
        OcrImage intervalCentered = Raster(ticks, IntervalLabelComponents());
        OcrImage tickCentered = Raster(ticks, [new(29, 75, 3, 8), new(49, 75, 3, 8), new(69, 75, 3, 8)]);
        byte[] intervalPixels = intervalCentered.Pixels.ToArray();
        byte[] tickPixels = tickCentered.Pixels.ToArray();
        var detector = new ConnectedComponentTextRegionDetector();

        IReadOnlyList<OcrDetectedRegion> intervalRegions = await detector.DetectAsync(intervalCentered, grouping, CancellationToken.None);
        IReadOnlyList<OcrDetectedRegion> tickRegions = await detector.DetectAsync(tickCentered, grouping, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { new OcrRectangle(29, 75, 3, 8), new OcrRectangle(49, 75, 3, 8), new OcrRectangle(69, 75, 3, 8) },
            tickRegions.Select(region => region.Polygon.Bounds).ToArray());
        CollectionAssert.AreEqual(
            new[] { new OcrRectangle(34, 75, 14, 8), new OcrRectangle(54, 75, 14, 8), new OcrRectangle(74, 75, 14, 8) },
            intervalRegions.Select(region => region.Polygon.Bounds).ToArray());
        Assert.IsTrue(intervalRegions.All(HasIntervalProvenance));
        Assert.IsTrue(tickRegions.All(region => !HasIntervalProvenance(region)));
        CollectionAssert.AreEqual(intervalPixels, intervalCentered.Pixels.ToArray());
        CollectionAssert.AreEqual(tickPixels, tickCentered.Pixels.ToArray());
        TestContext.WriteLine("ticks=" + string.Join(",", ticks) + "; plotBottom=65");
        TestContext.WriteLine("tick_centered_control=" + Format(tickRegions));
        TestContext.WriteLine("interval_centered_actual=" + Format(intervalRegions));
    }

    [TestMethod]
    public async Task CrossingInkAndDigitsStraddlingAMeasuredTickStayTogether()
    {
        var detector = new ConnectedComponentTextRegionDetector();
        OcrImage crossing = Raster([31, 51, 71], [new(30, 75, 3, 8), new(38, 75, 7, 8), new(50, 75, 3, 8)]);
        IReadOnlyList<OcrDetectedRegion> crossingRegions = await detector.DetectAsync(crossing,
            new OcrMeasuredTickGrouping([31, 51, 71], 65), CancellationToken.None);
        Assert.AreEqual(new OcrRectangle(30, 75, 23, 8), crossingRegions.Single().Polygon.Bounds);
        Assert.IsFalse(HasIntervalProvenance(crossingRegions[0]));

        int[] ticks = [30, 40, 50, 70];
        OcrImage adjacentDigits = Raster(ticks, [new(37, 75, 3, 8), new(42, 75, 3, 8)]);
        IReadOnlyList<OcrDetectedRegion> digitRegions = await detector.DetectAsync(adjacentDigits,
            new OcrMeasuredTickGrouping(ticks, 65), CancellationToken.None);
        Assert.AreEqual(new OcrRectangle(37, 75, 8, 8), digitRegions.Single().Polygon.Bounds);
        Assert.AreEqual(2, digitRegions[0].Evidence!.ComponentCount);
        Assert.IsFalse(HasIntervalProvenance(digitRegions[0]));
    }

    [TestMethod]
    public async Task DenseSingleNumberCenteredInOneIntervalIsNotMarkedAsIntervalLabels()
    {
        int[] ticks = [30, 50, 70, 90];
        OcrImage image = Raster(ticks, IntervalLabelComponents()[..3]);

        IReadOnlyList<OcrDetectedRegion> regions = await new ConnectedComponentTextRegionDetector().DetectAsync(image,
            new OcrMeasuredTickGrouping(ticks, 65), CancellationToken.None);

        Assert.AreEqual(new OcrRectangle(34, 75, 14, 8), regions.Single().Polygon.Bounds);
        Assert.AreEqual(3, regions[0].Evidence!.ComponentCount);
        Assert.IsFalse(HasIntervalProvenance(regions[0]));
    }

    [TestMethod]
    public async Task BoundaryCrossingMemberRejectsTheWholeIntervalRow()
    {
        int[] ticks = [30, 50, 70, 90];
        (int X, int Y, int Width, int Height)[] components =
            [.. IntervalLabelComponents(), (89, 75, 3, 8)];
        OcrImage image = Raster(ticks, components);

        IReadOnlyList<OcrDetectedRegion> regions = await new ConnectedComponentTextRegionDetector().DetectAsync(image,
            new OcrMeasuredTickGrouping(ticks, 65), CancellationToken.None);

        Assert.AreEqual(new OcrRectangle(34, 75, 58, 8), regions.Single().Polygon.Bounds);
        Assert.IsFalse(HasIntervalProvenance(regions[0]));
    }

    [TestMethod]
    public async Task TwoIntervalsDoNotQualifyAsTheIntervalCenteredControl()
    {
        int[] ticks = [30, 50, 70];
        OcrImage image = Raster(ticks, IntervalLabelComponents()[..6]);

        IReadOnlyList<OcrDetectedRegion> regions = await new ConnectedComponentTextRegionDetector().DetectAsync(image,
            new OcrMeasuredTickGrouping(ticks, 65), CancellationToken.None);

        Assert.AreEqual(new OcrRectangle(34, 75, 34, 8), regions.Single().Polygon.Bounds);
        Assert.AreEqual(6, regions[0].Evidence!.ComponentCount);
        Assert.IsFalse(HasIntervalProvenance(regions[0]));
    }

    [TestMethod]
    public async Task NonIdentityPixelsIgnoreMeasuredTickGroupingContext()
    {
        int[] ticks = [30, 50, 70, 90];
        OcrImage image = Raster(ticks, [new(29, 75, 3, 8), new(49, 75, 3, 8), new(69, 75, 3, 8)]) with
        {
            OriginalToImage = new(2, 2, 0, 0)
        };
        var detector = new ConnectedComponentTextRegionDetector();
        IReadOnlyList<OcrDetectedRegion> baseline = await detector.DetectAsync(image, CancellationToken.None);
        IReadOnlyList<OcrDetectedRegion> grouped = await detector.DetectAsync(image,
            new OcrMeasuredTickGrouping(ticks, 65), CancellationToken.None);

        CollectionAssert.AreEqual(baseline.Select(region => region.Polygon.Bounds).ToArray(),
            grouped.Select(region => region.Polygon.Bounds).ToArray());
        Assert.IsTrue(grouped.All(region => !HasIntervalProvenance(region)));
    }

    private static bool HasIntervalProvenance(OcrDetectedRegion region) =>
        region.Evidence?.Reasons.Contains(ConnectedComponentTextRegionDetector.IntervalXLabelReason) == true;

    private static string Format(IReadOnlyList<OcrDetectedRegion> regions) =>
        $"count={regions.Count}; " + string.Join("; ", regions.Select(region =>
            $"bounds={region.Polygon.Bounds.X:R},{region.Polygon.Bounds.Y:R},{region.Polygon.Bounds.Width:R},{region.Polygon.Bounds.Height:R},components={region.Evidence?.ComponentCount ?? 0}"));

    private static (int X, int Y, int Width, int Height)[] IntervalLabelComponents() =>
    [
        // Bounding boxes are 14x8 and centered at 41, 61, and 81. The last two
        // are shifted slightly within their intervals; the low pixel is a decimal.
        (34, 75, 3, 8), (40, 75, 3, 8), (46, 81, 2, 2),
        (54, 75, 3, 8), (60, 75, 3, 8), (66, 81, 2, 2),
        (74, 75, 3, 8), (80, 75, 3, 8), (86, 81, 2, 2),
    ];

    private static OcrImage Raster(int[] ticks, (int X, int Y, int Width, int Height)[] components)
    {
        const int width = 160, height = 100;
        byte[] pixels = Enumerable.Repeat(byte.MaxValue, width * height).ToArray();
        Fill(20, 65, 81, 1);
        foreach (int tick in ticks)
            for (int y = 65; y <= 70; y++) pixels[y * width + tick] = 0;
        foreach ((int x, int y, int componentWidth, int componentHeight) in components)
            Fill(x, y, componentWidth, componentHeight);

        return new(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);

        void Fill(int x, int y, int componentWidth, int componentHeight)
        {
            for (int row = y; row < y + componentHeight; row++)
                for (int column = x; column < x + componentWidth; column++)
                    pixels[row * width + column] = 0;
        }
    }
}
