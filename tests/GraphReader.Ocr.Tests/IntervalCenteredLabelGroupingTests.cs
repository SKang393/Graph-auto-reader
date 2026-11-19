// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class IntervalCenteredLabelGroupingTests
{
    private static readonly int[] ExpectedMixedComponentCounts = [2, 1, 1, 1];

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
    public async Task StrictlyAmbiguousIntervalPairStaysMergedWhileSafeLaterIntervalsSplit()
    {
        int[] ticks = [20, 40, 60, 80, 100, 120, 140];
        (int X, int Y, int Width, int Height)[] components =
        [
            (36, 75, 3, 8), // interval 20..40, right edge 38
            (42, 75, 3, 8), // interval 40..60, left edge 42: gap is exactly 3
            (61, 75, 3, 8), // later interval groups have strictly safe gaps
            (81, 75, 3, 8),
            (101, 75, 3, 8),
        ];
        OcrImage image = Raster(ticks, components);
        byte[] original = image.Pixels.ToArray();

        IReadOnlyList<OcrDetectedRegion> regions = await new ConnectedComponentTextRegionDetector().DetectAsync(image,
            new OcrMeasuredTickGrouping(ticks, 65), CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { new OcrRectangle(36, 75, 9, 8), new OcrRectangle(61, 75, 3, 8),
                new OcrRectangle(81, 75, 3, 8), new OcrRectangle(101, 75, 3, 8) },
            regions.Select(region => region.Polygon.Bounds).ToArray());
        CollectionAssert.AreEqual(ExpectedMixedComponentCounts, regions.Select(region => region.Evidence!.ComponentCount).ToArray());
        Assert.IsTrue(HasAmbiguousIntervalProvenance(regions[0]), "The two-interval cluster needs its distinct ambiguity provenance.");
        Assert.IsFalse(HasIntervalProvenance(regions[0]), "An ambiguous cluster is not an eligible interval label.");
        Assert.IsTrue(regions.Skip(1).All(HasIntervalProvenance), "Only independently safe interval groups receive provenance.");
        Assert.AreEqual(components.Length, regions.Sum(region => region.Evidence!.ComponentCount),
            "Every original component must be represented exactly once.");
        CollectionAssert.AreEqual(original, image.Pixels.ToArray());
    }

    [TestMethod]
    public async Task AmbiguousPairWithOnlyTwoSafeSingletonGroupsPreservesWholeRowAsAmbiguous()
    {
        int[] ticks = [20, 40, 60, 80, 100];
        OcrImage image = Raster(ticks,
        [
            (36, 75, 3, 8),
            (42, 75, 3, 8),
            (61, 75, 3, 8),
            (81, 75, 3, 8),
        ]);
        byte[] original = image.Pixels.ToArray();

        IReadOnlyList<OcrDetectedRegion> regions = await new ConnectedComponentTextRegionDetector().DetectAsync(image,
            new OcrMeasuredTickGrouping(ticks, 65), CancellationToken.None);

        Assert.AreEqual(new OcrRectangle(36, 75, 48, 8), regions.Single().Polygon.Bounds);
        Assert.AreEqual(4, regions[0].Evidence!.ComponentCount);
        Assert.IsTrue(HasAmbiguousIntervalProvenance(regions[0]));
        Assert.IsFalse(HasIntervalProvenance(regions[0]));
        CollectionAssert.AreEqual(original, image.Pixels.ToArray());
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
        Assert.IsFalse(HasAmbiguousIntervalProvenance(regions[0]));
    }

    [TestMethod]
    public async Task EntirelyAmbiguousIntervalRunStaysMergedWithDistinctProvenance()
    {
        int[] ticks = [20, 25, 30, 35, 40];
        OcrImage image = Raster(ticks,
        [
            (21, 75, 3, 8),
            (26, 75, 3, 8),
            (31, 75, 3, 8),
            (36, 75, 3, 8),
        ]);
        byte[] original = image.Pixels.ToArray();

        IReadOnlyList<OcrDetectedRegion> regions = await new ConnectedComponentTextRegionDetector().DetectAsync(image,
            new OcrMeasuredTickGrouping(ticks, 65), CancellationToken.None);

        Assert.AreEqual(new OcrRectangle(21, 75, 18, 8), regions.Single().Polygon.Bounds);
        Assert.AreEqual(4, regions[0].Evidence!.ComponentCount);
        Assert.IsTrue(HasAmbiguousIntervalProvenance(regions[0]));
        Assert.IsFalse(HasIntervalProvenance(regions[0]));
        CollectionAssert.AreEqual(original, image.Pixels.ToArray());
    }

    [TestMethod]
    public async Task TwoSafeSingletonIntervalsDoNotTriggerIntervalClassification()
    {
        int[] ticks = [30, 50, 70];
        OcrImage image = Raster(ticks, [(34, 75, 3, 8), (54, 75, 3, 8)]);

        IReadOnlyList<OcrDetectedRegion> regions = await new ConnectedComponentTextRegionDetector().DetectAsync(image,
            new OcrMeasuredTickGrouping(ticks, 65), CancellationToken.None);

        Assert.AreEqual(new OcrRectangle(34, 75, 23, 8), regions.Single().Polygon.Bounds);
        Assert.AreEqual(2, regions[0].Evidence!.ComponentCount);
        Assert.IsFalse(HasIntervalProvenance(regions[0]));
        Assert.IsFalse(HasAmbiguousIntervalProvenance(regions[0]));
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

    [TestMethod]
    public async Task OnlyCenteredNeighborsOfferSeparateReviewCropsAndPreserveTheParent()
    {
        int[] ticks = [20, 40, 60, 80, 100, 120];
        var grouping = new OcrMeasuredTickGrouping(ticks, 65);
        (int, int, int, int)[] ink =
        [
            (26, 75, 4, 8), (32, 75, 6, 8), (42, 75, 6, 8), (50, 75, 5, 8),
            (69, 75, 3, 8), (89, 75, 3, 8), (109, 75, 3, 8),
        ];
        var detector = new ConnectedComponentTextRegionDetector();
        var pairs = new Dictionary<string, OcrDetectedRegion[]>();
        OcrImage image = Raster(ticks, ink);
        var ordinary = await detector.DetectAsync(image, grouping, CancellationToken.None);
        var review = await detector.DetectAsync(image, grouping, pairs, CancellationToken.None);
        CollectionAssert.AreEqual(ordinary.Select(r => r.RegionId).ToArray(), review.Select(r => r.RegionId).ToArray());
        var parent = review.Single(HasAmbiguousIntervalProvenance);
        Assert.HasCount(1, pairs);
        CollectionAssert.AreEqual(new[] { new OcrRectangle(26, 75, 12, 8), new OcrRectangle(42, 75, 13, 8) },
            pairs[parent.RegionId].Select(r => r.Polygon.Bounds).ToArray());
        Assert.IsTrue(pairs[parent.RegionId].All(HasIntervalProvenance));

        // A compact number straddling a tick leaves white space at the tick, but does not occupy both midpoints.
        pairs.Clear();
        await detector.DetectAsync(Raster(ticks, [(36, 75, 3, 8), (42, 75, 3, 8), .. ink[4..]]),
            grouping, pairs, CancellationToken.None);
        Assert.IsEmpty(pairs);
        await detector.DetectAsync(Raster(ticks, [.. ink, (39, 75, 3, 8)]), grouping, pairs, CancellationToken.None);
        Assert.IsEmpty(pairs);
    }

    private static bool HasIntervalProvenance(OcrDetectedRegion region) =>
        region.Evidence?.Reasons.Contains(ConnectedComponentTextRegionDetector.IntervalXLabelReason) == true;

    private static bool HasAmbiguousIntervalProvenance(OcrDetectedRegion region) =>
        region.Evidence?.Reasons.Contains("x_label_spans_measured_tick_intervals") == true;

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
        Fill(10, 65, 141, 1);
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
