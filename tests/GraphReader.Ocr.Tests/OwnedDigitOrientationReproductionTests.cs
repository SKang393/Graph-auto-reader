// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class OwnedDigitOrientationReproductionTests
{
    private static readonly string[] Seven =
    [
        "#####", "...##", "...##", "..##.", "..##.", ".##..",
        ".##..", "##...", "##...", "#....", "#....",
    ];

    [TestMethod]
    public async Task UprightNarrowNumericGlyphRetainsItsObservedPixelOrientation()
    {
        byte[] glyph = UprightRow(1);
        OcrImage image = Place(glyph, 5, 11, clockwise: false);
        IReadOnlyList<OcrDetectedRegion> regions = await new ConnectedComponentTextRegionDetector(new()
            { UseObservedVerticalGroupingOrientation = true })
            .DetectAsync(image, CancellationToken.None);
        Assert.HasCount(1, regions);
        Assert.AreEqual(new OcrRectangle(30, 20, 5, 11), regions[0].Polygon.Bounds);

        OcrCrop crop = Crop(image, regions[0], 5, 11);

        CollectionAssert.AreEqual(glyph.Select(pixel => pixel / 255f).ToArray(), crop.Pixels.ToArray(),
            "The upright printed digit must not be quarter-turned solely because its glyph is narrow.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GenuinelyRotatedMultiGlyphRowStillNormalizesToUprightSourcePixels(bool observedGroupingOnly)
    {
        byte[] glyphs = UprightRow(3);
        OcrImage image = Place(glyphs, 21, 11, clockwise: true);
        IReadOnlyList<OcrDetectedRegion> regions = await new ConnectedComponentTextRegionDetector(new()
            { UseObservedVerticalGroupingOrientation = observedGroupingOnly })
            .DetectAsync(image, CancellationToken.None);
        Assert.HasCount(1, regions);
        Assert.AreEqual(new OcrRectangle(30, 20, 11, 21), regions[0].Polygon.Bounds);

        OcrCrop crop = Crop(image, regions[0], 21, 11);

        CollectionAssert.AreEqual(glyphs.Select(pixel => pixel / 255f).ToArray(), crop.Pixels.ToArray());
    }

    [TestMethod]
    public async Task ExistingDefaultKeepsItsOrientationAndCacheIdentity()
    {
        OcrImage image = Place(UprightRow(1), 5, 11, clockwise: false);
        var existing = new ConnectedComponentTextRegionDetector();
        var explicitDefault = new ConnectedComponentTextRegionDetector(new()
            { UseObservedVerticalGroupingOrientation = false });
        var corrected = new ConnectedComponentTextRegionDetector(new()
            { UseObservedVerticalGroupingOrientation = true });
        OcrDetectedRegion before = (await existing.DetectAsync(image, CancellationToken.None)).Single();
        OcrDetectedRegion after = (await corrected.DetectAsync(image, CancellationToken.None)).Single();

        Assert.AreEqual(-90d, before.OrientationDegrees);
        Assert.AreEqual(0d, after.OrientationDegrees);
        Assert.AreEqual(before.RegionId, after.RegionId);
        CollectionAssert.AreEqual(before.Polygon.Points.ToArray(), after.Polygon.Points.ToArray());
        Assert.AreEqual(existing.ConfigurationFingerprint, explicitDefault.ConfigurationFingerprint);
        Assert.AreEqual(existing.ConfigurationFingerprint + ":observed-vertical-orientation-v1", corrected.ConfigurationFingerprint);
    }

    private static OcrCrop Crop(OcrImage image, OcrDetectedRegion region, int width, int height) =>
        OcrCropBatcher.CreateBatches(image, [region], new()
        {
            TargetWidth = width, TargetHeight = height, PaddingPixels = 0,
            ResizeMode = OcrCropResizeMode.Stretch,
        })[0][0];

    private static byte[] UprightRow(int count)
    {
        int width = (count - 1) * 8 + 5;
        byte[] pixels = Enumerable.Repeat((byte)255, width * Seven.Length).ToArray();
        for (int glyph = 0; glyph < count; glyph++)
            for (int y = 0; y < Seven.Length; y++)
                for (int x = 0; x < Seven[y].Length; x++)
                    if (Seven[y][x] == '#') pixels[y * width + glyph * 8 + x] = 0;
        return pixels;
    }

    private static OcrImage Place(byte[] source, int sourceWidth, int sourceHeight, bool clockwise)
    {
        const int width = 200, height = 160;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int y = 0; y < sourceHeight; y++)
            for (int x = 0; x < sourceWidth; x++)
            {
                int targetX = clockwise ? sourceHeight - 1 - y : x;
                int targetY = clockwise ? x : y;
                pixels[(20 + targetY) * width + 30 + targetX] = source[y * sourceWidth + x];
            }
        return new(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
    }
}
