// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class SparseTickRecoveryTests
{
    private static readonly OcrRectangle Plot = new(40, 30, 180, 90);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OneReadLabelAndTwoVisibleTicksRecoverOnlyTheMissingPixelCrop(bool vertical)
    {
        var fixture = Fixture(vertical);
        byte[] original = fixture.Image.Pixels.ToArray();
        var result = await TickLaneTextRegionRecovery.FindAsync(
            fixture.Image, [fixture.Anchor], [fixture.Reading], Plot);
        Assert.HasCount(1, result);
        Assert.AreEqual(fixture.Missing, result[0].Polygon.Bounds);
        Assert.AreEqual(0d, result[0].OrientationDegrees);
        Assert.IsNull(result[0].Context);
        CollectionAssert.AreEqual(original, fixture.Image.Pixels.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SparseRecoveryRequiresDistinctAxisConnectedStrokesAndLiteralOriginalAnchor(bool vertical)
    {
        foreach (string invalid in new[] { "missing-tick", "missing-anchor-tick", "detached-ticks", "two-ticks", "no-axis" })
        {
            var fixture = Fixture(vertical, invalid);
            Assert.IsEmpty(await TickLaneTextRegionRecovery.FindAsync(
                fixture.Image, [fixture.Anchor], [fixture.Reading], Plot), invalid);
        }
        var valid = Fixture(vertical);
        foreach (OcrRegion reading in new[]
        {
            valid.Reading with { Text = "O" },
            valid.Reading with { ReviewStatus = OcrReviewStatus.Rejected },
            valid.Reading with { SourceImage = OcrSourceImage.Enhanced },
            valid.Reading with { RegionId = "unbound" },
            valid.Reading with { Polygon = OcrPolygon.FromRectangle(valid.Missing) },
        })
        {
            Assert.IsEmpty(await TickLaneTextRegionRecovery.FindAsync(valid.Image, [valid.Anchor], [reading], Plot));
        }
    }

    private static (OcrImage Image, OcrDetectedRegion Anchor, OcrRegion Reading, OcrRectangle Missing)
        Fixture(bool vertical, string? invalid = null)
    {
        const int width = 260;
        const int height = 160;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        OcrDetectedRegion anchor = vertical ? OcrTestFixtures.Region("read", 24, 40, 8, 8) :
            OcrTestFixtures.Region("read", 192, 126, 5, 8);
        OcrRectangle missing = vertical ? new(24, 100, 8, 8) : new(70, 126, 5, 8);
        foreach (OcrRectangle box in new[] { anchor.Polygon.Bounds, missing })
            for (int y = (int)box.Top; y < box.Bottom; y++)
                for (int x = (int)box.Left; x < box.Right; x++) pixels[y * width + x] = 0;
        if (invalid != "no-axis")
        {
            for (int x = 40; x <= 220; x++) pixels[120 * width + x] = 0;
            for (int y = 30; y <= 120; y++) pixels[y * width + 40] = 0;
        }
        if (invalid != "missing-anchor-tick") Tick(vertical ? 44 : 194);
        if (invalid != "missing-tick") Tick(vertical ? 104 : 72);
        if (invalid == "two-ticks") Tick(vertical ? 108 : 76);
        var image = new OcrImage(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
        var reading = new OcrRegion(anchor.RegionId, anchor.Polygon, "2", [],
            vertical ? OcrTextRole.YTick : OcrTextRole.XTick, 0.95,
            OcrSourceImage.Original, OcrReviewStatus.Unreviewed);
        return (image, anchor, reading, missing);

        void Tick(int along)
        {
            for (int cross = vertical ? 36 : 119; cross <= (vertical ? 41 : 124); cross++)
            {
                if (invalid == "detached-ticks" && (vertical ? cross >= 39 : cross <= 121)) continue;
                if (invalid == "no-axis" && (vertical ? cross == 40 : cross == 120)) continue;
                pixels[(vertical ? along : cross) * width + (vertical ? cross : along)] = 0;
            }
        }
    }
}
