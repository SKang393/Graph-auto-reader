// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class AxisGeometryPairContactTests
{
    private static readonly string[] LowerAxisIds = ["lower-y-axis"];
    private static readonly string[] ConnectedAxisIds = ["y-bottom", "y-overlap", "y-top"];

    [TestMethod]
    public async Task DetachedCollinearInkDoesNotEnlargePlotOrRemoveDottedDivider()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .CleanAxes()
            .Line(100, 10, 100, 20, id: "detached-caption-stroke")
            .Line(750, 300, 735, 300, id: "detached-right-stroke")
            .DottedDivider(400, pattern: LinePatternHint.Unknown)
            .Build();

        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);

        Assert.AreEqual(50d, result.PlotPolygon.TopLeft.Y, 0.01d);
        Assert.AreEqual(700d, result.PlotPolygon.BottomRight.X, 0.01d);
        Assert.IsFalse(result.YAxis.SupportingCandidateIds.Contains("detached-caption-stroke"));
        Assert.IsFalse(result.XAxis.SupportingCandidateIds.Contains("detached-right-stroke"));
        Assert.AreEqual(1, result.PhaseDividers.Count);
        Assert.AreEqual(DividerStyle.Dotted, result.PhaseDividers[0].Style);
    }

    [TestMethod]
    public async Task StackedCollinearAxesDoNotJoinAcrossThePanelGap()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .Line(100, 350, 700, 350, id: "lower-x-axis")
            .Line(100, 350, 100, 210, id: "lower-y-axis")
            .Line(100, 50, 100, 150, id: "upper-y-axis")
            .Build();

        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);

        Assert.AreEqual(350d, result.PlotPolygon.BottomLeft.Y, 0.01d);
        Assert.AreEqual(210d, result.PlotPolygon.TopLeft.Y, 0.01d);
        CollectionAssert.AreEquivalent(LowerAxisIds, result.YAxis.SupportingCandidateIds.ToArray());
    }

    [TestMethod]
    public async Task OverlappingEvidenceBridgesShortAxisFragmentsInEitherDirection()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .Line(100, 300, 400, 300, id: "x-left")
            .Line(700, 300, 402, 300, id: "x-right")
            .Line(100, 300, 100, 200, id: "y-bottom")
            .Line(100, 202, 100, 150, id: "y-overlap")
            .Line(100, 50, 100, 148, id: "y-top")
            .Line(100, 10, 100, 20, id: "detached-caption-stroke")
            .Build();

        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);

        Assert.AreEqual(50d, result.PlotPolygon.TopLeft.Y, 0.01d);
        Assert.AreEqual(700d, result.PlotPolygon.BottomRight.X, 0.01d);
        CollectionAssert.AreEquivalent(
            ConnectedAxisIds, result.YAxis.SupportingCandidateIds.ToArray());
        Assert.AreEqual(2, result.XAxis.SupportingCandidateIds.Count);
    }

    [TestMethod]
    public async Task DisconnectedAxisSegmentsCannotProduceConfidentGeometry()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .Line(300, 320, 750, 320, id: "disconnected-horizontal")
            .Line(100, 250, 100, 20, id: "disconnected-vertical")
            .Build();

        try
        {
            AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);

            Assert.IsTrue(
                result.Uncertainty.NeedsReview,
                $"Disconnected segments produced confident geometry at " +
                $"({result.PlotPolygon.BottomLeft.X:F3}, {result.PlotPolygon.BottomLeft.Y:F3}) " +
                $"with confidence {result.Confidence:F6} and pair margin " +
                $"{result.Uncertainty.BestAlternativeScoreMargin:F6}.");
        }
        catch (AxisGeometryDetectionException failure)
        {
            Assert.AreEqual("AXIS_GEOMETRY_NOT_FOUND", failure.Code);
        }
    }

    [TestMethod]
    public async Task CoherentAxesWinOrDisconnectedCompetitionIsReportedAsAmbiguous()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder()
            .Line(180, 300, 600, 300, id: "true-x-axis")
            .Line(180, 300, 180, 80, id: "true-y-axis")
            .Line(350, 340, 760, 340, id: "disconnected-horizontal")
            .Line(100, 260, 100, 30, id: "disconnected-vertical")
            .Build();

        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);
        bool coherentPairSelected =
            result.XAxis.SupportingCandidateIds.Contains("true-x-axis") &&
            result.YAxis.SupportingCandidateIds.Contains("true-y-axis");
        bool ambiguityReported =
            result.Uncertainty.NeedsReview &&
            result.Uncertainty.Reasons.Contains("axis_pair_ambiguous");

        Assert.IsTrue(
            coherentPairSelected || ambiguityReported,
            $"Disconnected competition selected an unreviewed pair at " +
            $"({result.PlotPolygon.BottomLeft.X:F3}, {result.PlotPolygon.BottomLeft.Y:F3}) " +
            $"with x support [{string.Join(",", result.XAxis.SupportingCandidateIds)}], " +
            $"y support [{string.Join(",", result.YAxis.SupportingCandidateIds)}], and pair margin " +
            $"{result.Uncertainty.BestAlternativeScoreMargin:F6}.");
    }

    [TestMethod]
    public async Task FittedFamilySpanCannotBridgeAnUnobservedCornerGap()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder(width: 240, height: 180)
            .Line(20, 140, 80, 140, id: "horizontal-left-fragment")
            .Line(120, 140, 220, 140, id: "horizontal-right-fragment")
            .Line(100, 140, 100, 20, id: "contacting-vertical")
            .Build();

        try
        {
            AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(request);

            Assert.IsTrue(
                result.Uncertainty.NeedsReview,
                $"A fitted family bridged a 40-pixel unobserved corner gap and produced " +
                $"confidence {result.Confidence:F6} with pair margin " +
                $"{result.Uncertainty.BestAlternativeScoreMargin:F6}.");
        }
        catch (AxisGeometryDetectionException failure)
        {
            Assert.AreEqual("AXIS_GEOMETRY_NOT_FOUND", failure.Code);
        }
    }
}
