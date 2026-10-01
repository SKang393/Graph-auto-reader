// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionMarkerStrokeGapExclusionTests
{
    [TestMethod]
    [DataRow(MarkerShape.Circle, 0)]
    [DataRow(MarkerShape.Square, 1)]
    [DataRow(MarkerShape.Diamond, 2)]
    [DataRow(MarkerShape.TriangleUp, 0)]
    [DataRow(MarkerShape.TriangleDown, 2)]
    public void WhiteGapBetweenLongThinStrokesIsExcludedWithAuditableId(MarkerShape shape, int rotation)
    {
        float[] pixels = Strokes(rotation);
        float[] before = pixels.ToArray();
        ClassifiedMarker marker = Marker(shape: shape);
        StrokeGapMarkerExclusionBatch result = Find(pixels, marker);
        Assert.HasCount(1, result.ExcludedMarkerIds);
        Assert.AreEqual("candidate", result.ExcludedMarkerIds.Single());
        Assert.HasCount(1, result.Warnings);
        Assert.AreEqual("marker_excluded_by_original_pixel_stroke_gap:candidate", result.Warnings.Single());
        CollectionAssert.AreEqual(before, pixels);
        Assert.AreEqual(new MarkerPoint(32, 32), marker.Marker.Center);
        Assert.AreEqual(0.01, marker.ArtifactProbability);
    }

    [TestMethod]
    [DataRow(MarkerShape.Cross, MarkerFill.Open)]
    [DataRow(MarkerShape.Asterisk, MarkerFill.Open)]
    [DataRow(MarkerShape.Star, MarkerFill.Open)]
    [DataRow(MarkerShape.Other, MarkerFill.Open)]
    [DataRow(MarkerShape.TriangleUp, MarkerFill.Filled)]
    [DataRow(MarkerShape.TriangleUp, MarkerFill.Unknown)]
    public void NonOutlineShapesAndNonOpenFillsRemainEligible(MarkerShape shape, MarkerFill fill) =>
        Assert.IsEmpty(Find(Strokes(), Marker(shape: shape, fill: fill)).ExcludedMarkerIds);

    [TestMethod]
    public void OnePixelClosedOutlineProtectsItsWhiteCenter()
    {
        float[] pixels = Blank();
        for (int n = 27; n <= 37; n++)
        {
            pixels[27 * 64 + n] = pixels[37 * 64 + n] = 0;
            pixels[n * 64 + 27] = pixels[n * 64 + 37] = 0;
        }
        Assert.IsEmpty(Find(pixels, Marker(shape: MarkerShape.Square)).ExcludedMarkerIds);
    }

    [TestMethod]
    public void OffCenterCrossMisclassifiedAsOpenDiamondKeepsItsTwoDimensionalInk()
    {
        float[] pixels = Blank();
        for (int n = 28; n <= 40; n++)
        {
            pixels[35 * 64 + n] = 0;
            pixels[n * 64 + 35] = 0;
        }
        Assert.AreEqual(1f, pixels[32 * 64 + 32]);
        Assert.IsFalse(ProductionMarkerEnclosedSupport.IsSupported(Frame(pixels), 32, 32));
        Assert.IsEmpty(Find(pixels, Marker(shape: MarkerShape.Diamond)).ExcludedMarkerIds);
    }

    [TestMethod]
    [DataRow(224, false)]
    [DataRow(225, true)]
    public void CenterInkUsesExistingEnclosureDefinition(int gray, bool excluded)
    {
        float[] pixels = Strokes();
        pixels[32 * 64 + 32] = gray / 255f;
        Assert.AreEqual(excluded, Find(pixels, Marker()).ExcludedMarkerIds.Contains("candidate"));
    }

    [TestMethod]
    public void EmptyPixelsAndShortFragmentsAreInsufficientToExclude()
    {
        Assert.IsEmpty(Find(Blank(), Marker()).ExcludedMarkerIds);
        float[] pixels = Blank();
        for (int x = 30; x <= 34; x++) pixels[29 * 64 + x] = 0;
        Assert.IsEmpty(Find(pixels, Marker()).ExcludedMarkerIds);
    }

    [TestMethod]
    public void ASeparateTwoDimensionalComponentProtectsTheCandidate()
    {
        float[] pixels = Strokes();
        for (int y = 30; y <= 34; y++)
        for (int x = 34; x <= 38; x++) pixels[y * 64 + x] = 0;
        Assert.IsEmpty(Find(pixels, Marker()).ExcludedMarkerIds);
    }

    [TestMethod]
    public void LargeAndClippedSupportWindowsRemainEligible()
    {
        Assert.IsEmpty(Find(Strokes(), Marker(radius: 13)).ExcludedMarkerIds);
        Assert.IsEmpty(Find(Strokes(), Marker(x: 2)).ExcludedMarkerIds);
        Assert.IsEmpty(Find(Strokes(), Marker(x: 60)).ExcludedMarkerIds);
    }

    [TestMethod]
    public void MasksCannotSupplyOrEraseOriginalPixelEvidence()
    {
        float[] pixels = Strokes();
        MarkerImageFrame frame = Frame(pixels) with
        {
            OcrMask = new(64, 64, Enumerable.Repeat(1f, 64 * 64).ToArray()),
            ArtifactMask = new(64, 64, Enumerable.Repeat(1f, 64 * 64).ToArray()),
        };
        Assert.AreEqual(1, ProductionMarkerStrokeGapExclusion.Find(frame, [Marker()], CancellationToken.None).ExcludedMarkerIds.Count);
        CollectionAssert.AreEqual(pixels, Strokes());
    }

    [TestMethod]
    public void ChangedCoordinateSpaceAndMalformedFrameAreRejected()
    {
        MarkerImageFrame frame = Frame(Strokes());
        Assert.ThrowsExactly<ArgumentException>(() => ProductionMarkerStrokeGapExclusion.Find(
            frame with { OriginalToFrame = new(2, 0, 0, 0, 2, 0) }, [Marker()], CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => ProductionMarkerStrokeGapExclusion.Find(
            frame with { SourceImage = MarkerSourceImage.Enhanced }, [Marker()], CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => ProductionMarkerStrokeGapExclusion.Find(
            frame with { ChannelCount = 3 }, [Marker()], CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => ProductionMarkerStrokeGapExclusion.Find(
            frame with { ChannelsFirstPixels = new float[1] }, [Marker()], CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => Find(Strokes(), Marker(x: double.NaN)));
        Assert.ThrowsExactly<ArgumentException>(() => Find(Strokes(), Marker(radius: double.PositiveInfinity)));
        Assert.ThrowsExactly<ArgumentException>(() => Find(Strokes(), Marker(coordinateSpace: "enhanced_pixels")));
    }

    [TestMethod]
    public void CancellationPropagatesBeforeEmptyOrNonemptyBatches()
    {
        var canceled = new CancellationToken(canceled: true);
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionMarkerStrokeGapExclusion.Find(Frame(Blank()), [], canceled));
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionMarkerStrokeGapExclusion.Find(Frame(Strokes()), [Marker()], canceled));
    }

    private static StrokeGapMarkerExclusionBatch Find(float[] pixels, ClassifiedMarker marker) =>
        ProductionMarkerStrokeGapExclusion.Find(Frame(pixels), [marker], CancellationToken.None);

    private static MarkerImageFrame Frame(float[] pixels) => new(
        64, 64, 1, pixels, MarkerSourceImage.Original, MarkerAffineTransform.Identity,
        new MarkerMask(64, 64, new float[64 * 64]), new MarkerMask(64, 64, new float[64 * 64]));

    private static ClassifiedMarker Marker(double x = 32, double radius = 6,
        MarkerShape shape = MarkerShape.TriangleUp, MarkerFill fill = MarkerFill.Open,
        string coordinateSpace = MarkerContract.CoordinateSpace) => new(
        new MarkerCenter("candidate", new(x, 32), radius, 0.01, 0.95, MarkerSourceImage.Original, coordinateSpace),
        shape, fill, "triangle", "Open triangle", 0.01, 0.99, 0.99, Enumerable.Repeat(0.1f, 12));

    private static float[] Blank() => Enumerable.Repeat(1f, 64 * 64).ToArray();

    private static float[] Strokes(int rotation = 0)
    {
        float[] pixels = Blank();
        for (int n = 20; n <= 44; n++)
        foreach (int offset in new[] { -3, 3 })
        {
            int x = rotation == 1 ? 32 + offset : n;
            int y = rotation == 0 ? 32 + offset : rotation == 1 ? n : n + offset;
            pixels[y * 64 + x] = 0;
        }
        return pixels;
    }
}
