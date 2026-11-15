// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class MeasuredYTickGroupingTests
{
    [TestMethod]
    [DataRow(150d)]
    [DataRow(180d)]
    public async Task MeasuredYTicksKeepDistinctWideConnectedRowsUpright(double plotBottom)
    {
        OcrImage image = Ink((20, 46, 20, 8), (20, 96, 20, 8), (20, 146, 20, 8));
        byte[] original = image.Pixels.ToArray();
        var detector = new ConnectedComponentTextRegionDetector(new() { UseObservedVerticalGroupingOrientation = true });
        var baseline = await detector.DetectAsync(image, CancellationToken.None);
        Assert.HasCount(1, baseline);
        Assert.AreEqual(-90d, baseline[0].OrientationDegrees);

        var actual = await detector.DetectAsync(image, Context(plotBottom), CancellationToken.None);

        Assert.HasCount(3, actual, "Separate observed tick lanes must not become one rotated text row.");
        CollectionAssert.AreEqual(new[] { new OcrRectangle(20, 46, 20, 8), new OcrRectangle(20, 96, 20, 8),
            new OcrRectangle(20, 146, 20, 8) }, actual.Select(region => region.Polygon.Bounds).ToArray());
        Assert.IsTrue(actual.All(region => region.OrientationDegrees == 0));
        CollectionAssert.AreEqual(original, image.Pixels.ToArray());
    }

    [TestMethod]
    public async Task CrossingInkCannotEstablishSeparateYLabelRows()
    {
        OcrImage image = Ink((20, 46, 20, 8), (20, 96, 20, 8), (20, 146, 20, 8), (30, 72, 2, 12));
        var detector = new ConnectedComponentTextRegionDetector(new() { UseObservedVerticalGroupingOrientation = true });
        var baseline = await detector.DetectAsync(image, CancellationToken.None);

        var actual = await detector.DetectAsync(image, Context(), CancellationToken.None);

        CollectionAssert.AreEqual(baseline.Select(region => region.Polygon.Bounds).ToArray(),
            actual.Select(region => region.Polygon.Bounds).ToArray());
        Assert.IsTrue(actual.Any(region => region.OrientationDegrees == -90));
    }

    [TestMethod]
    public async Task DecimalAndMinusFragmentsStayWithTheirYLabel()
    {
        OcrImage image = Ink((20, 46, 20, 8), (14, 49, 4, 1), (42, 52, 2, 2),
            (20, 96, 20, 8), (20, 146, 20, 8));
        var actual = await new ConnectedComponentTextRegionDetector(new() { UseObservedVerticalGroupingOrientation = true })
            .DetectAsync(image, Context(), CancellationToken.None);

        Assert.HasCount(3, actual);
        Assert.AreEqual(new OcrRectangle(14, 46, 30, 8), actual[0].Polygon.Bounds);
        Assert.AreEqual(3, actual[0].Evidence!.ComponentCount);
        Assert.IsTrue(actual.All(region => region.OrientationDegrees == 0));
    }

    [TestMethod]
    [DataRow("absent")]
    [DataRow("sparse")]
    [DataRow("duplicate")]
    [DataRow("unsorted")]
    [DataRow("outside-image")]
    [DataRow("below-plot")]
    [DataRow("over-limit")]
    [DataRow("missing-edge")]
    [DataRow("nonfinite-edge")]
    [DataRow("outside-edge")]
    [DataRow("nonidentity")]
    public async Task InvalidOrAbsentYContextPreservesExistingGrouping(string condition)
    {
        OcrMeasuredTickGrouping context = condition switch
        {
            "absent" => new([], 180),
            "sparse" => Context() with { YTicks = new[] { 50, 100 } },
            "duplicate" => Context() with { YTicks = new[] { 50, 50, 150 } },
            "unsorted" => Context() with { YTicks = new[] { 100, 50, 150 } },
            "outside-image" => Context() with { YTicks = new[] { -1, 100, 150 } },
            "below-plot" => Context() with { PlotBottom = 120 },
            "over-limit" => Context() with { YTicks = Enumerable.Range(1, 129).ToArray() },
            "missing-edge" => Context() with { PlotLeft = null },
            "nonfinite-edge" => Context() with { PlotLeft = double.NaN },
            "outside-edge" => Context() with { PlotLeft = 200 },
            "nonidentity" => Context(),
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };
        OcrImage image = Ink((20, 46, 20, 8), (20, 96, 20, 8), (20, 146, 20, 8));
        if (condition == "nonidentity") image = image with { OriginalToImage = new(2, 2, 0, 0) };
        var detector = new ConnectedComponentTextRegionDetector(new() { UseObservedVerticalGroupingOrientation = true });
        var baseline = await detector.DetectAsync(image, CancellationToken.None);

        var actual = await detector.DetectAsync(image, context, CancellationToken.None);

        Assert.HasCount(1, actual);
        Assert.AreEqual(baseline[0].Polygon.Bounds, actual[0].Polygon.Bounds);
        Assert.AreEqual(baseline[0].OrientationDegrees, actual[0].OrientationDegrees);
    }

    [TestMethod]
    public async Task ValidYContextStillHonorsCancellationBeforeScanning()
    {
        OcrImage image = Ink((20, 46, 20, 8), (20, 96, 20, 8), (20, 146, 20, 8));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new ConnectedComponentTextRegionDetector().DetectAsync(image, Context(), new(canceled: true)).AsTask());
    }

    private static OcrMeasuredTickGrouping Context(double plotBottom = 180) => new([], plotBottom)
        { YTicks = new[] { 50, 100, 150 }, PlotLeft = 60 };

    private static OcrImage Ink(params (int X, int Y, int Width, int Height)[] components)
    {
        const int width = 200, height = 200;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        foreach (var component in components)
            for (int y = component.Y; y < component.Y + component.Height; y++)
                for (int x = component.X; x < component.X + component.Width; x++) pixels[y * width + x] = 0;
        return new(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
    }
}
