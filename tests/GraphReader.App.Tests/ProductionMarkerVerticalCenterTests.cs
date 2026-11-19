// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionMarkerVerticalCenterTests
{
    private static ClassifiedMarker Candidate(double x = 25.5, double y = 20.5, MarkerFill fill = MarkerFill.Filled,
        MarkerSourceImage source = MarkerSourceImage.Original) => new(
        new MarkerCenter("detected", new(x, y), 6, 0.01, 0.9, source),
        MarkerShape.Diamond, fill, "diamond", "Diamond", 0.02, 0.8, 0.9, Enumerable.Repeat(0f, 12));

    private static OcrImage Image(Func<int, int, bool> ink)
    {
        const int width = 51, height = 47, stride = 56;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y * stride + x] = ink(x, y) ? (byte)0 : (byte)255;
        return new(width, height, stride, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
    }

    [TestMethod]
    [DataRow(-2.0)]
    [DataRow(0.0)]
    [DataRow(2.0)]
    public void FilledGlyphOnHorizontalAndSlopingTracesUsesOpposedOriginalEdges(double offset)
    {
        OcrImage image = Image((x, y) => Math.Abs(x - 25) * 5 + Math.Abs(y - 22) * 6 <= 30 ||
            (x < 25 && Math.Abs(y - 22) <= 1) || (x > 25 && Math.Abs(y - 22 + (x - 25)) <= 1));
        ClassifiedMarker marker = Candidate(y: 22 + offset);
        Assert.AreEqual(22d, ProductionMarkerVerticalCenter.Measure(image, marker));
        Assert.AreEqual(22 + offset, marker.Marker.Center.Y);
    }

    [TestMethod]
    [DataRow(22, 22.0)]
    [DataRow(23, 22.5)]
    public void MidpointUsesFirstAndLastForegroundPixelCenters(int secondCenter, double expected)
    {
        // The marker tensor maps an identity sample to its integer pixel index.
        // Reflecting the ink rows around the known center leaves the glyph unchanged.
        OcrImage image = Image((x, y) => Math.Abs(x - 25) + Math.Min(Math.Abs(y - 22), Math.Abs(y - secondCenter)) <= 5);
        Assert.AreEqual(expected, ProductionMarkerVerticalCenter.Measure(image, Candidate(y: 21)));
    }

    [TestMethod]
    public void MissingOrCompetingEdgesLeaveTheProposalAlone()
    {
        Func<int, int, bool>[] negatives =
        [
            (x, y) => Math.Abs(y - 22) <= 1,
            (x, y) => Math.Abs(x - 25) <= 1 || Math.Abs(y - 22) <= 1,
            (x, y) => Math.Abs(y - 22 - (x - 25)) <= 1 || Math.Abs(y - 22 + (x - 25)) <= 1,
            (x, y) => y >= 17 && y <= 26 && Math.Abs(x - 25) <= y - 17,
            (x, y) => Math.Abs(Math.Abs(x - 25) + Math.Abs(y - 22) - 6) <= 1,
            (x, y) => Math.Min(Math.Abs(x - 22), Math.Abs(x - 28)) + Math.Abs(y - 22) <= 4,
        ];
        foreach (var ink in negatives)
            Assert.IsNull(ProductionMarkerVerticalCenter.Measure(Image(ink), Candidate(y: 22.5)));
    }

    [TestMethod]
    public void ClippedOpenAndEnhancedCandidatesCannotAcquireAnOriginalFilledCenter()
    {
        var image = Image((x, y) => Math.Abs(x - 25) + Math.Abs(y - 22) <= 6);
        Assert.IsNull(ProductionMarkerVerticalCenter.Measure(image, Candidate(x: 1)));
        Assert.IsNull(ProductionMarkerVerticalCenter.Measure(image, Candidate(fill: MarkerFill.Open)));
        Assert.IsNull(ProductionMarkerVerticalCenter.Measure(image, Candidate(source: MarkerSourceImage.Enhanced)));
        Assert.IsNull(ProductionMarkerVerticalCenter.Measure(image with { SourceImage = OcrSourceImage.Enhanced }, Candidate()));
        Assert.IsNull(ProductionMarkerVerticalCenter.Measure(image with { Stride = 1 }, Candidate()));
        Assert.IsNull(ProductionMarkerVerticalCenter.Measure(image with { Pixels = ReadOnlyMemory<byte>.Empty }, Candidate()));
    }
}
