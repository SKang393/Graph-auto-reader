// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class AxisGeometryPairContactTests
{
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
