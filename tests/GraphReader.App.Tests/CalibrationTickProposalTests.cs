// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Ocr;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationTickProposalTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(29)]
    public void CompleteProposalUsesMeasuredTicksAndObservedZeroWithoutExtrapolation(int first)
    {
        CalibrationOcrProposal[] labels = Labels(first);
        CalibrationTickProposalResult result = CalibrationTickProposal.Create(Geometry(), Response(labels));
        Assert.IsTrue(result.IsComplete, string.Join(",", result.Reasons));
        Assert.AreEqual(first, result.FirstSession);
        Assert.AreEqual(first + 6d, result.FinalSession);
        Assert.AreEqual(10d, result.YMaximum);
        Assert.AreEqual(new CalibrationOcrPoint(100, 250), result.Anchors!.FirstSessionY0);
        Assert.AreEqual(new CalibrationOcrPoint(100, 50), result.Anchors.FirstSessionYMaximum);
        Assert.AreEqual(new CalibrationOcrPoint(300, 250), result.Anchors.FinalSessionY0);
        Assert.HasCount(6, result.PairedTicks);
        Assert.HasCount(0, result.Reasons);
        Assert.AreEqual(labels[0].RegionId, result.PairedTicks[0].Label.RegionId);
        Assert.AreNotEqual(Geometry().Top, result.Anchors.FirstSessionYMaximum.Y);
        Assert.AreNotEqual(Geometry().Bottom, result.Anchors.FirstSessionY0.Y);
    }

    [TestMethod]
    public void OffsetGlyphsKeepTheirEvidenceButUseTheMeasuredTickPositions()
    {
        CalibrationOcrProposal[] labels = Labels().Select(row => row with
        {
            Polygon = row.Polygon.Select(point => row.SuggestedAxis == "X"
                ? point with { X = point.X + 7 } : point with { Y = point.Y + 7 }).ToArray(),
        }).ToArray();
        CalibrationTickProposalResult result = CalibrationTickProposal.Create(Geometry(), Response(labels));
        Assert.IsTrue(result.IsComplete, string.Join(",", result.Reasons));
        Assert.AreEqual(100, result.PairedTicks[0].PixelPosition);
        Assert.AreEqual(107d, result.PairedTicks[0].Label.Polygon.Average(point => point.X));
        Assert.AreEqual(new CalibrationOcrPoint(100, 250), result.Anchors!.FirstSessionY0);
    }

    [TestMethod]
    public void StalePlotGeometryIsRejectedBeforePairing()
    {
        CalibrationOcrResponse response = Response(Labels());
        response = response with { Request = response.Request! with { Plot = new(71, 20, 350, 280) } };
        CalibrationTickProposalResult result = CalibrationTickProposal.Create(Geometry(), response);
        Assert.IsFalse(result.IsComplete);
        Assert.IsNull(result.Anchors);
        CollectionAssert.Contains(result.Reasons.ToArray(), CalibrationTickProposalReasons.InvalidEvidence);
    }

    [TestMethod]
    public void RawGlyphCenterWarningDoesNotVetoConsistentMeasuredTickPairs()
    {
        RustAxisGeometry geometry = Geometry() with { XTickPositions = [100, 130, 160] };
        CalibrationOcrProposal[] labels = Labels();
        for (int index = 0; index < 3; index++)
        {
            int offset = index == 1 ? -9 : 9;
            double center = geometry.XTickPositions[index] + offset;
            labels[index] = labels[index] with
            {
                Polygon = [new(center - 6, 286), new(center + 6, 286), new(center + 6, 296), new(center - 6, 296)],
            };
        }
        TickResolutionResult raw = MonotonicTickResolver.Resolve(labels.Take(3).Select(row => new TickCandidate(row.RegionId,
            row.Polygon.Average(point => point.X), row.NumericValue!.Value, row.Confidence)).ToArray(), TickAxisDirection.IncreasingWithPixels);
        Assert.IsTrue(raw.NeedsReview);
        CalibrationOcrResponse response = Response(labels) with
        {
            XSequence = new(raw.NeedsReview, 3, raw.ResolvedTicks.Count, raw.RejectedTicks.Count, raw.Reasons),
        };
        CalibrationTickProposalResult result = CalibrationTickProposal.Create(geometry, response);
        Assert.IsTrue(result.IsComplete, string.Join(",", result.Reasons));
        Assert.AreEqual(new CalibrationOcrPoint(160, 250), result.Anchors!.FinalSessionY0);
        Assert.IsTrue(response.XSequence.NeedsReview, "The original warning remains available to the label UI.");
    }

    [TestMethod]
    public void MissingObservedZeroNeverCreatesAZeroAnchor()
    {
        CalibrationOcrProposal[] labels = Labels().Select(row => row.SuggestedAxis == "Y" ? Value(row, row.NumericValue!.Value + 2) : row).ToArray();
        AssertRejected(Geometry(), labels, CalibrationTickProposalReasons.ObservedZeroRequired);
    }

    [TestMethod]
    public void AnUnlabelledMeasuredEndpointCannotBeExtrapolated()
    {
        RustAxisGeometry geometry = Geometry() with { XTickPositions = [100, 200, 300, 340] };
        AssertRejected(geometry, Labels(), CalibrationTickProposalReasons.MissingEndpoint);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WrongAxisOrWrongSideLabelsRemainVisibleFailures(bool wrongSide)
    {
        CalibrationOcrProposal[] labels = Labels();
        labels[0] = wrongSide
            ? labels[0] with { Polygon = [new(95, 100), new(105, 100), new(105, 110), new(95, 110)] }
            : labels[0] with { SuggestedAxis = "Y" };
        AssertRejected(Geometry(), labels, CalibrationTickProposalReasons.WrongAxis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DuplicateMatchesAndIdsNeverSelectTheMoreConfidentReading(bool duplicateId)
    {
        CalibrationOcrProposal[] labels = Labels();
        CalibrationOcrProposal extra = duplicateId
            ? labels[1] with { RegionId = labels[0].RegionId, Confidence = .999 }
            : labels[0] with { RegionId = "duplicate", Confidence = .999 };
        AssertRejected(Geometry(), [.. labels, extra], CalibrationTickProposalReasons.DuplicateAssociation);
    }

    [TestMethod]
    public void EquidistantLabelIsAmbiguousRatherThanChosenByConfidence()
    {
        CalibrationOcrProposal[] labels = Labels();
        labels[1] = labels[1] with { Polygon = [new(190, 286), new(310, 286), new(310, 296), new(190, 296)] };
        AssertRejected(Geometry(), labels, CalibrationTickProposalReasons.AmbiguousAssociation);
    }

    [TestMethod]
    public void NonmonotonicLabelsCannotBeReducedToABestSubset()
    {
        CalibrationOcrProposal[] labels = Labels();
        labels[1] = Value(labels[1], 0);
        AssertRejected(Geometry(), labels, CalibrationTickProposalReasons.Nonmonotonic);
    }

    [TestMethod]
    public void NonlinearLabelSpacingCannotHideARejectedFitOutlier()
    {
        CalibrationOcrProposal[] labels = Labels();
        labels[2] = Value(labels[2], 12);
        AssertRejected(Geometry(), labels, CalibrationTickProposalReasons.InconsistentSpacing);
    }

    [TestMethod]
    public void FewerThanThreePairedValuesOnEitherAxisIsIncomplete()
    {
        AssertRejected(Geometry(), Labels().Where(row => row.RegionId != "y-middle").ToArray(), CalibrationTickProposalReasons.InsufficientPairs);
    }

    private static void AssertRejected(RustAxisGeometry geometry, CalibrationOcrProposal[] labels, string reason)
    {
        CalibrationTickProposalResult result = CalibrationTickProposal.Create(geometry, Response(labels));
        Assert.IsFalse(result.IsComplete);
        Assert.IsNull(result.Anchors);
        Assert.IsNull(result.FirstSession);
        Assert.IsNull(result.FinalSession);
        Assert.IsNull(result.YMaximum);
        CollectionAssert.Contains(result.Reasons.ToArray(), reason);
    }

    private static RustAxisGeometry Geometry() => new(70, 280, 350, 20, .9)
    {
        XTickPositions = [100, 200, 300], YTickPositions = [50, 150, 250],
    };

    private static CalibrationOcrProposal[] Labels(int first = 1) =>
    [
        Label("x-first", "X", 100, first), Label("x-middle", "X", 200, first + 3), Label("x-final", "X", 300, first + 6),
        Label("y-maximum", "Y", 50, 10), Label("y-middle", "Y", 150, 5), Label("y-zero", "Y", 250, 0),
    ];

    private static CalibrationOcrProposal Label(string id, string axis, int pixel, double value)
    {
        double x = axis == "X" ? pixel : 40, y = axis == "Y" ? pixel : 291;
        string text = value.ToString(CultureInfo.InvariantCulture);
        return new(id, [new(x - 6, y - 5), new(x + 6, y - 5), new(x + 6, y + 5), new(x - 6, y + 5)],
            text, .9, value, text, true, axis == "X" ? "XTick" : "YTick", axis, null);
    }

    private static CalibrationOcrProposal Value(CalibrationOcrProposal label, double value) => label with
    {
        Text = value.ToString(CultureInfo.InvariantCulture), NumericValue = value,
        NormalizedText = value.ToString(CultureInfo.InvariantCulture),
    };

    private static CalibrationOcrResponse Response(IReadOnlyList<CalibrationOcrProposal> labels) =>
        new(new("owned-request", "binding", new string('a', 64), new string('b', 64), 450, 340, new(70, 20, 350, 280)),
            labels, new(false, 3, 3, 0, []), new(false, 3, 3, 0, []), null, null, null);
}
