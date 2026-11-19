// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
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

    [TestMethod]
    [DataRow(DividerStyle.Dotted, 0)]
    [DataRow(DividerStyle.Dashed, 1)]
    public void CompleteDashOnMeasuredDividerCannotBecomeAPlotMarker(DividerStyle style, int tilt)
    {
        float[] pixels = Blank();
        for (int y = 8; y < 57; y++)
        {
            if (Math.Abs(y - 32) % 20 > 6 && Math.Abs(y - 32) % 20 < 14) continue;
            int x = 32 + (int)Math.Round(tilt * (y - 32) / 6d);
            for (int dx = -1; dx <= 1; dx++) pixels[y * 64 + x + dx] = 0.4f;
        }
        PhaseDividerGeometry divider = new("line", new(new(32 - 4 * tilt, 8), new(32 + 4 * tilt, 56)),
            style, 0.9, 1, 0.6, ["measured"]);
        var marker = Marker(shape: MarkerShape.Cross, fill: MarkerFill.Unknown);
        var result = ProductionMarkerStrokeGapExclusion.Find(Frame(pixels), [marker], CancellationToken.None, [divider]);
        Assert.AreEqual("marker_excluded_by_original_pixel_divider_dash:candidate", result.Warnings.SingleOrDefault());

        // A lateral arm supplies independent ink for a real symbol even at the same center.
        for (int x = 26; x <= 38; x++) pixels[32 * 64 + x] = 0;
        Assert.IsEmpty(ProductionMarkerStrokeGapExclusion.Find(Frame(pixels), [marker], CancellationToken.None, [divider]).ExcludedMarkerIds);
    }

    [TestMethod]
    public void IsolatedInkNeedsAnIntersectingBrokenDividerAndBothStrokeEnds()
    {
        float[] pixels = Blank();
        for (int y = 25; y <= 39; y++) pixels[y * 64 + 32] = 0;
        var marker = Marker(shape: MarkerShape.Cross, fill: MarkerFill.Filled);
        PhaseDividerGeometry divider = new("line", new(new(32, 8), new(32, 56)), DividerStyle.Dashed, 0.9, 1, 0.6, []);
        foreach (PhaseDividerGeometry other in new[]
        {
            divider with { Style = DividerStyle.Solid },
            divider with { Line = new(new(36, 8), new(36, 56)) },
            divider with { Line = new(new(8, 32), new(56, 32)) },
            divider with { Line = new(new(32, 2), new(32, 16)) },
        })
            Assert.IsEmpty(ProductionMarkerStrokeGapExclusion.Find(Frame(pixels), [marker], CancellationToken.None, [other]).ExcludedMarkerIds);
        Assert.IsEmpty(Find(pixels, marker).ExcludedMarkerIds);
        for (int y = 8; y <= 56; y++) pixels[y * 64 + 32] = 0;
        Assert.IsEmpty(ProductionMarkerStrokeGapExclusion.Find(Frame(pixels), [marker], CancellationToken.None, [divider]).ExcludedMarkerIds);
    }

    [TestMethod]
    public void ShortCrossbarOnLongDividerStrokeProtectsAnInkCenteredSymbol()
    {
        float[] pixels = Blank();
        for (int y = 22; y <= 42; y++) pixels[y * 64 + 32] = 0;
        for (int x = 30; x <= 34; x++) pixels[32 * 64 + x] = 0;
        var divider = new PhaseDividerGeometry("line", new(new(32, 8), new(32, 56)), DividerStyle.Dotted,
            0.9, 1, 0.6, ["measured"]);
        var result = ProductionMarkerStrokeGapExclusion.Find(Frame(pixels),
            [Marker(shape: MarkerShape.Cross, fill: MarkerFill.Unknown)], CancellationToken.None, [divider]);
        Assert.IsEmpty(result.ExcludedMarkerIds);
    }

    [TestMethod]
    public void TwoDisconnectedStrokesCannotConfirmOneDividerDash()
    {
        float[] pixels = Blank();
        for (int y = 24; y <= 40; y++) pixels[y * 64 + (y <= 32 ? 32 : 35)] = 0;
        var divider = new PhaseDividerGeometry("line", new(new(32, 8), new(32, 56)), DividerStyle.Dashed,
            0.9, 1, 0.6, ["measured"]);
        var result = ProductionMarkerStrokeGapExclusion.Find(Frame(pixels),
            [Marker(shape: MarkerShape.Cross, fill: MarkerFill.Unknown)], CancellationToken.None, [divider]);
        Assert.IsEmpty(result.ExcludedMarkerIds);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void RepeatedRasterizedStrokeHasSameWidthAsItsTwoFlankingDots(int tilted)
    {
        var (pixels, axis) = DashDotPattern(tilted);
        var marker = Marker(radius: 4.5, shape: MarkerShape.Cross, fill: MarkerFill.Filled);
        var result = ProductionMarkerStrokeGapExclusion.Find(Frame(pixels), [marker], CancellationToken.None, [axis]);
        Assert.AreEqual("marker_excluded_by_original_pixel_divider_dash:candidate", result.Warnings.SingleOrDefault());
    }

    [TestMethod]
    [DataRow(20, 33, true)]
    [DataRow(23, 31, true)]
    [DataRow(28, 31, true)]
    [DataRow(42, 33, true)]
    [DataRow(20, 35, false)]
    [DataRow(21, 33, false)]
    public void OnlyConnectedTerminalInkCanTaperOffTheMeasuredLine(int row, int x, bool excluded)
    {
        var (pixels, axis) = DashDotPattern();
        Array.Fill(pixels, 1f, row * 64 + 31, 3);
        pixels[row * 64 + x] = 0.4f;
        var marker = Marker(radius: 4.5, shape: MarkerShape.Cross, fill: MarkerFill.Filled);
        var result = ProductionMarkerStrokeGapExclusion.Find(Frame(pixels), [marker], CancellationToken.None, [axis]);
        Assert.AreEqual(excluded, result.ExcludedMarkerIds.Contains("candidate"));
    }

    [TestMethod]
    [DataRow("one-sided")]
    [DataRow("unequal-gaps")]
    [DataRow("unequal-lengths")]
    [DataRow("offset-dot")]
    [DataRow("wider-dot")]
    [DataRow("split-dot")]
    [DataRow("short-divider")]
    [DataRow("solid-divider")]
    public void RepeatedStrokeNeedsIndependentDividerAndTwoAlignedSimilarNeighbors(string defect)
    {
        var (pixels, axis) = DashDotPattern();
        switch (defect)
        {
            case "one-sided": Array.Fill(pixels, 1f, 20 * 64, 4 * 64); break;
            case "unequal-gaps":
                Array.Fill(pixels, 1f, 20 * 64, 4 * 64);
                for (int row = 16; row <= 19; row++) Array.Fill(pixels, 0f, row * 64 + 31, 3);
                break;
            case "unequal-lengths":
                for (int row = 16; row <= 19; row++) Array.Fill(pixels, 0f, row * 64 + 31, 3);
                break;
            case "offset-dot":
                for (int row = 20; row <= 23; row++)
                {
                    Array.Fill(pixels, 1f, row * 64 + 31, 3);
                    Array.Fill(pixels, 0f, row * 64 + 34, 3);
                }
                break;
            case "wider-dot":
                for (int row = 20; row <= 23; row++) Array.Fill(pixels, 0f, row * 64 + 30, 5);
                break;
            case "split-dot": pixels[21 * 64 + 36] = 0; break;
            case "short-divider": axis = axis with { Line = new(new(32, 24), new(32, 48)) }; break;
            case "solid-divider": axis = axis with { Style = DividerStyle.Solid }; break;
        }
        var marker = Marker(radius: 4.5, shape: MarkerShape.Cross, fill: MarkerFill.Filled);
        Assert.IsEmpty(ProductionMarkerStrokeGapExclusion.Find(Frame(pixels), [marker], CancellationToken.None, [axis]).ExcludedMarkerIds);
    }

    [TestMethod]
    [DataRow(MarkerShape.Square, MarkerFill.Filled, 32)]
    [DataRow(MarkerShape.Square, MarkerFill.Open, 32)]
    [DataRow(MarkerShape.Square, MarkerFill.Filled, 38)]
    [DataRow(MarkerShape.Square, MarkerFill.Open, 38)]
    [DataRow(MarkerShape.Cross, MarkerFill.Unknown, 32)]
    public void TwoFlankingDotsCannotEraseGenuineMarkerInk(MarkerShape shape, MarkerFill fill, int x)
    {
        var (pixels, axis) = DashDotPattern();
        for (int dy = -3; dy <= 3; dy++)
        for (int dx = -3; dx <= 3; dx++)
        {
            bool ink = shape == MarkerShape.Cross ? dx == 0 || dy == 0 :
                fill == MarkerFill.Filled || Math.Abs(dx) == 3 || Math.Abs(dy) == 3;
            if (ink) pixels[(32 + dy) * 64 + x + dx] = 0;
        }
        var marker = Marker(x: x, radius: 4.5, shape: shape, fill: fill);
        Assert.IsEmpty(ProductionMarkerStrokeGapExclusion.Find(Frame(pixels), [marker], CancellationToken.None, [axis]).ExcludedMarkerIds);
    }

    private static (float[] Pixels, PhaseDividerGeometry Axis) DashDotPattern(int tilted = 0)
    {
        float[] pixels = Blank();
        for (int row = 20; row <= 45; row++)
        {
            if (row is (> 23 and < 28) or (> 37 and < 42)) continue;
            int x = 32 + (int)Math.Round(tilted * (row - 32) / 8d);
            Array.Fill(pixels, 0.4f, row * 64 + x - 1, row == 30 ? 4 : 3);
        }
        var axis = new PhaseDividerGeometry("line", new(new(32 - 3 * tilted, 8), new(32 + 3 * tilted, 56)),
            DividerStyle.Dashed, 0.9, 1, 0.6, ["measured"]);
        return (pixels, axis);
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
