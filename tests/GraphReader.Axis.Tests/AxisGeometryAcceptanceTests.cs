// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class AxisGeometryAcceptanceTests
{
    private static readonly string[] ExpectedAlignedDuplicateTickIds = ["tick-lsd", "tick-hough"];
    private static readonly string[] ExpectedStandaloneDuplicateTickIds = ["standalone-lsd", "standalone-hough"];

    [TestMethod]
    public void GeometryResultCannotEmitMarkerObjects()
    {
        string[] propertyNames = typeof(AxisGeometryResult)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.IsFalse(propertyNames.Any(name =>
            name.Contains("marker", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task CleanAxesRecoverPlotAndSeparateTicksFromGeometry()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .CleanAxes()
            .XTick(100)
            .XTick(250)
            .XTick(400)
            .XTick(550)
            .XTick(700)
            .YTick(50)
            .YTick(175)
            .YTick(300)
            .Build();

        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);

        Assert.AreEqual(AxisGeometryCoordinateSpaces.OriginalPixels, result.CoordinateSpace);
        AssertPointWithin(result.PlotPolygon.BottomLeft, 100, 300, 0.01);
        AssertPointWithin(result.PlotPolygon.BottomRight, 700, 300, 0.01);
        AssertPointWithin(result.PlotPolygon.TopLeft, 100, 50, 0.01);
        Assert.AreEqual(8, result.Ticks.Count);
        Assert.AreEqual(0, result.PhaseDividers.Count);
        Assert.IsTrue(result.XAxis.SupportingCandidateIds.Contains("x-axis"));
        Assert.IsTrue(result.YAxis.SupportingCandidateIds.Contains("y-axis"));
    }

    [TestMethod]
    public async Task DottedDividerAlignedWithTickIsNotAnAxisOrTick()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .CleanAxes()
            .XTick(400)
            .DottedDivider(400)
            .Build();

        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);

        Assert.AreEqual(1, result.Ticks.Count(t => t.Axis == TickAxis.XAxis));
        PhaseDividerGeometry divider = AssertSingle(result.PhaseDividers);
        Assert.AreEqual(DividerStyle.Dotted, divider.Style);
        Assert.AreEqual(400d, divider.Line.Midpoint.X, 0.01);
        Assert.IsFalse(divider.SupportingCandidateIds.Contains("x-tick-400"));
    }

    [TestMethod]
    public async Task UnknownPatternSegmentsInferDividerWithoutAbsorbingAlignedTick()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .CleanAxes()
            .XTick(400)
            .DottedDivider(400, pattern: LinePatternHint.Unknown)
            .Build();

        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);

        AxisTickGeometry tick = AssertSingle(result.Ticks.Where(item =>
            item.Axis == TickAxis.XAxis && item.TickId == "x-tick-400").ToArray());
        PhaseDividerGeometry divider = AssertSingle(result.PhaseDividers);
        Assert.AreEqual(DividerStyle.Dotted, divider.Style);
        Assert.AreEqual(400d, divider.Line.Midpoint.X, 0.01);
        Assert.IsFalse(divider.SupportingCandidateIds.Contains("x-tick-400"));
        Assert.IsTrue(tick.SupportingCandidateIds.Contains("x-tick-400"));
    }

    [TestMethod]
    public async Task UnknownDividerExcludesDuplicateNativeTickSegmentsAndConsolidatesPhysicalTick()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .CleanAxes()
            .Line(
                400,
                295,
                400,
                305,
                LinePatternHint.Unknown,
                id: "tick-lsd",
                source: LineCandidateSource.OpenCvLsd)
            .Line(
                400.2,
                295.2,
                400.2,
                304.8,
                LinePatternHint.Unknown,
                id: "tick-hough",
                source: LineCandidateSource.OpenCvHough)
            .DottedDivider(400, pattern: LinePatternHint.Unknown)
            .Build();

        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);

        AxisTickGeometry tick = AssertSingle(result.Ticks.Where(item =>
            item.Axis == TickAxis.XAxis && Math.Abs(item.Center.X - 400) <= 1).ToArray());
        PhaseDividerGeometry divider = AssertSingle(result.PhaseDividers);
        CollectionAssert.IsSubsetOf(
            ExpectedAlignedDuplicateTickIds,
            tick.SupportingCandidateIds.ToArray());
        Assert.IsFalse(divider.SupportingCandidateIds.Contains("tick-lsd"));
        Assert.IsFalse(divider.SupportingCandidateIds.Contains("tick-hough"));
    }

    [TestMethod]
    public async Task OverlappingLsdAndHoughTickCandidatesProduceOnePhysicalTick()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .CleanAxes()
            .Line(
                250,
                295,
                250,
                305,
                LinePatternHint.Unknown,
                id: "standalone-lsd",
                source: LineCandidateSource.OpenCvLsd)
            .Line(
                250.25,
                295.1,
                250.25,
                304.9,
                LinePatternHint.Unknown,
                id: "standalone-hough",
                source: LineCandidateSource.OpenCvHough)
            .Build();

        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);

        AxisTickGeometry tick = AssertSingle(result.Ticks.Where(item =>
            item.Axis == TickAxis.XAxis && Math.Abs(item.Center.X - 250) <= 1).ToArray());
        CollectionAssert.IsSubsetOf(
            ExpectedStandaloneDuplicateTickIds,
            tick.SupportingCandidateIds.ToArray());
    }

    [TestMethod]
    public async Task OppositeSlantedCrossingLinesDoNotMergeIntoFalseAxis()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .Line(100, 300, 400, 300, id: "x-left")
            .Line(402, 300, 700, 300, id: "x-right")
            .Line(100, 300, 100, 175, id: "y-bottom")
            .Line(100, 173, 100, 50, id: "y-top")
            .Line(100, 206.25, 700, 153.75, id: "cross-up")
            .Line(100, 153.75, 700, 206.25, id: "cross-down")
            .Build();

        try
        {
            AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);
            bool crossersMerged = result.XAxis.SupportingCandidateIds.Contains("cross-up") &&
                result.XAxis.SupportingCandidateIds.Contains("cross-down");
            Assert.IsFalse(crossersMerged, "Opposite signed slopes must not be merged into one axis family.");

            bool coherentAxesSelected = result.XAxis.SupportingCandidateIds.Contains("x-left") &&
                result.XAxis.SupportingCandidateIds.Contains("x-right") &&
                result.YAxis.SupportingCandidateIds.Contains("y-bottom") &&
                result.YAxis.SupportingCandidateIds.Contains("y-top");
            bool structuredAmbiguity = result.Uncertainty.NeedsReview &&
                result.Uncertainty.Reasons.Contains("axis_pair_ambiguous");
            Assert.IsTrue(
                coherentAxesSelected || structuredAmbiguity,
                "The detector must select coherent supported axes or surface structured ambiguity.");
        }
        catch (AxisGeometryDetectionException failure)
        {
            Assert.AreEqual("AXIS_GEOMETRY_NOT_FOUND", failure.Code);
            Assert.IsTrue(failure.Recoverable);
            Assert.AreEqual("select_manual_calibration", failure.SuggestedAction);
        }
    }

    [TestMethod]
    public async Task EmptyRefinedLineFamilyReturnsStructuredDetectionFailure()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .Line(100, 300, 110, 300, id: "fragment-left")
            .Line(115, 304, 125, 304, id: "fragment-right")
            .Line(100, 300, 100, 50, id: "vertical-evidence")
            .Build(new AxisGeometryOptions
            {
                MergeAngleToleranceDegrees = 4,
                MergeDistancePixels = 3,
            });

        AxisGeometryDetectionException failure = await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(
            async () => await new AxisGeometryDetector().DetectAsync(request));

        Assert.AreEqual("AXIS_GEOMETRY_NOT_FOUND", failure.Code);
        Assert.AreEqual("Errors.AxisGeometryNotFound", failure.UserMessageKey);
        Assert.IsTrue(failure.Recoverable);
        Assert.AreEqual("select_manual_calibration", failure.SuggestedAction);
    }

    [TestMethod]
    public async Task FullHeightGridlinesAndFrameDoNotReplaceAxesOrDividers()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .CleanAxes()
            .VerticalGrid(250)
            .VerticalGrid(400)
            .VerticalGrid(550)
            .HorizontalGrid(100)
            .HorizontalGrid(175)
            .HorizontalGrid(250)
            .Line(35, 25, 765, 25, id: "frame-top")
            .Line(765, 25, 765, 365, id: "frame-right")
            .Line(765, 365, 35, 365, id: "frame-bottom")
            .Line(35, 365, 35, 25, id: "frame-left")
            .Build();

        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);

        AssertPointWithin(result.PlotPolygon.BottomLeft, 100, 300, 1);
        Assert.AreEqual(AxisGeometryCoordinateSpaces.OriginalPixels, result.CoordinateSpace);
        Assert.AreEqual(0, result.PhaseDividers.Count);
        Assert.IsTrue(result.Diagnostics.GridOrFrameExclusionCount >= 4);
        Assert.IsTrue(result.Uncertainty.NeedsReview);
        CollectionAssert.Contains(
            result.Uncertainty.Reasons.ToArray(),
            "grid_or_phase_divider_ambiguous");

        Assert.AreEqual(3, result.AmbiguousGridOrDividers.Count);
        double[] expectedXs = [250d, 400d, 550d];
        for (int index = 0; index < expectedXs.Length; index++)
        {
            AmbiguousGridOrDividerGeometry ambiguous = result.AmbiguousGridOrDividers[index];
            Assert.AreEqual(expectedXs[index], ambiguous.Line.Start.X, 0.01);
            Assert.AreEqual(expectedXs[index], ambiguous.Line.End.X, 0.01);
            Assert.AreEqual(50, Math.Min(ambiguous.Line.Start.Y, ambiguous.Line.End.Y), 0.01);
            Assert.AreEqual(300, Math.Max(ambiguous.Line.Start.Y, ambiguous.Line.End.Y), 0.01);
            CollectionAssert.Contains(
                ambiguous.SupportingCandidateIds.ToArray(),
                $"grid-v-{expectedXs[index]:0.###}");
            Assert.IsTrue(ambiguous.GeometryConfidence > 0);
        }
    }

    [TestMethod]
    public async Task SlantedAndPartialAxisSegmentsFitWithinTwoPixels()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .Line(101, 300, 370, 304, id: "x-left")
            .Line(372, 304, 700, 309, id: "x-right")
            .Line(101, 300, 103, 180, id: "y-bottom")
            .Line(103, 178, 105, 50, id: "y-top")
            .Build(new AxisGeometryOptions
            {
                MergeDistancePixels = 5,
                MergeAngleToleranceDegrees = 3,
            });

        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);

        AssertPointWithin(result.PlotPolygon.BottomLeft, 101, 300, 2);
        AssertPointWithin(result.PlotPolygon.BottomRight, 700, 309, 2);
        AssertPointWithin(result.PlotPolygon.TopLeft, 105, 50, 2);
        Assert.IsTrue(result.XAxis.SupportingCandidateIds.Count >= 2);
        Assert.IsTrue(result.YAxis.SupportingCandidateIds.Count >= 2);
    }

    [TestMethod]
    public async Task NonOriginalCoordinateSpaceAndCancellationAreRejected()
    {
        AxisFixtureBuilder fixture = new AxisFixtureBuilder().CleanAxes();
        AxisGeometryRequest derivative = fixture.Build() with { CoordinateSpace = "enhanced_pixels" };

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await new AxisGeometryDetector().DetectAsync(derivative));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await new AxisGeometryDetector().DetectAsync(fixture.Build(), cancellation.Token));
    }

    private static T AssertSingle<T>(IReadOnlyList<T> items)
    {
        Assert.AreEqual(1, items.Count);
        return items[0];
    }

    private static void AssertPointWithin(
        PixelPoint actual,
        double expectedX,
        double expectedY,
        double tolerance)
    {
        Assert.AreEqual(expectedX, actual.X, tolerance);
        Assert.AreEqual(expectedY, actual.Y, tolerance);
    }
}
