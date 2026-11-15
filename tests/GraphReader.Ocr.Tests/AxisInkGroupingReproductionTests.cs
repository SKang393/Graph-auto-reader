// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class AxisInkGroupingReproductionTests
{
    [TestMethod]
    public async Task FullConnectedBaselineAndTicksAreFilteredWithoutChangingMeasuredLabelGroups()
    {
        var detector = new ConnectedComponentTextRegionDetector();
        var grouping = new OcrMeasuredTickGrouping([31, 51, 71], PlotBottom: 65);
        OcrImage control = Fixture(includeFullAxis: false);
        OcrImage withFullAxis = Fixture(includeFullAxis: true);
        byte[] controlPixels = control.Pixels.ToArray();
        byte[] axisPixels = withFullAxis.Pixels.ToArray();

        IReadOnlyList<OcrDetectedRegion> controlRegions = await detector.DetectAsync(control, grouping, CancellationToken.None);
        IReadOnlyList<OcrDetectedRegion> axisRegions = await detector.DetectAsync(withFullAxis, grouping, CancellationToken.None);

        OcrRectangle[] expected =
        [
            new(30, 75, 3, 8),
            new(50, 75, 3, 8),
            new(70, 75, 3, 8),
        ];
        CollectionAssert.AreEqual(expected, controlRegions.Select(region => region.Polygon.Bounds).ToArray());
        CollectionAssert.AreEqual(expected, axisRegions.Select(region => region.Polygon.Bounds).ToArray());
        Assert.IsTrue(axisRegions.All(region => region.Evidence?.ComponentCount == 1));
        CollectionAssert.AreEqual(controlPixels, control.Pixels.ToArray());
        CollectionAssert.AreEqual(axisPixels, withFullAxis.Pixels.ToArray());
    }

    private static OcrImage Fixture(bool includeFullAxis)
    {
        const int width = 160, height = 100;
        byte[] pixels = Enumerable.Repeat(byte.MaxValue, width * height).ToArray();
        Fill(30, 75, 3, 8);
        Fill(50, 75, 3, 8);
        Fill(70, 75, 3, 8);
        if (includeFullAxis)
        {
            // The complete baseline/tick component is 61 pixels wide, above
            // the detector's 15% component-width limit (24 pixels here).
            Fill(20, 65, 61, 1);
            for (int x = 31; x <= 71; x += 20)
                for (int y = 65; y <= 70; y++) pixels[y * width + x] = 0;
            for (int y = 65; y <= 83; y++) pixels[y * width + 41] = 0;
        }

        return new(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);

        void Fill(int x, int y, int componentWidth, int componentHeight)
        {
            for (int row = y; row < y + componentHeight; row++)
                for (int column = x; column < x + componentWidth; column++)
                    pixels[row * width + column] = 0;
        }
    }
}
