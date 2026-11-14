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
    [DataRow(false)]
    [DataRow(true)]
    public void UnlabelledOuterAndMinorTicksNeverCreateValuesOrMoveObservedAnchors(bool reviewDraft)
    {
        RustAxisGeometry geometry = Geometry() with
        {
            XTickPositions = [80, 100, 150, 200, 250, 300, 340],
            YTickPositions = [30, 50, 100, 150, 200, 250, 270],
        };
        CalibrationOcrResponse response = Response(Labels());
        CalibrationTickProposalResult result = reviewDraft
            ? CalibrationTickProposal.CreateReviewDraft(geometry, response)
            : CalibrationTickProposal.Create(geometry, response);
        Assert.IsTrue(result.IsComplete, string.Join(",", result.Reasons));
        Assert.AreEqual(new CalibrationOcrPoint(100, 250), result.Anchors!.FirstSessionY0);
        Assert.AreEqual(new CalibrationOcrPoint(100, 50), result.Anchors.FirstSessionYMaximum);
        Assert.AreEqual(new CalibrationOcrPoint(300, 250), result.Anchors.FinalSessionY0);
        Assert.AreEqual(0d, result.PairedTicks.Single(pair => pair.Axis == "Y" && pair.PixelPosition == 250).Value);
        Assert.HasCount(6, result.PairedTicks);
        Assert.IsFalse(result.PairedTicks.Any(pair => pair.Axis == "X"
            ? pair.PixelPosition is 80 or 150 or 250 or 340
            : pair.PixelPosition is 30 or 100 or 200 or 270));
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReviewDraftRetainsValidXWhenYHasNoObservedZero(bool estimatedX)
    {
        CalibrationOcrProposal[] labels = Labels().Select(row => row.SuggestedAxis == "Y"
            ? Value(row, row.NumericValue!.Value + 2) : row).ToArray();
        RustAxisGeometry geometry = estimatedX ? Geometry() with { XTickPositions = [] } : Geometry();

        CalibrationTickProposalResult draft = CalibrationTickProposal.CreateReviewDraft(geometry, Response(labels));

        Assert.IsFalse(draft.IsComplete);
        Assert.IsNull(draft.Anchors);
        Assert.IsNull(draft.FirstSession);
        Assert.IsNull(draft.YMaximum);
        Assert.AreEqual(new CalibrationXReviewDraft(100, 300, 1, 7, estimatedX), draft.XReviewDraft);
        Assert.IsNull(draft.YReviewDraft);
        CollectionAssert.Contains(draft.Reasons.ToArray(), CalibrationTickProposalReasons.ObservedZeroRequired);
        Assert.HasCount(6, draft.PairedTicks);
        CalibrationTickProposalResult measured = CalibrationTickProposal.Create(geometry, Response(labels));
        Assert.IsNull(measured.XReviewDraft, "Measured-only Create keeps its strict whole-proposal result.");
        Assert.IsNull(measured.YReviewDraft);
    }

    [TestMethod]
    public void ReviewDraftRetainsValidYWhenXIsNonmonotonic()
    {
        CalibrationOcrProposal[] labels = Labels();
        labels[1] = Value(labels[1], 0);
        CalibrationTickProposalResult draft = CalibrationTickProposal.CreateReviewDraft(Geometry(), Response(labels));
        Assert.IsFalse(draft.IsComplete);
        Assert.IsNull(draft.XReviewDraft);
        Assert.AreEqual(new CalibrationYReviewDraft(250, 50, 10, false), draft.YReviewDraft);
        CollectionAssert.Contains(draft.Reasons.ToArray(), CalibrationTickProposalReasons.Nonmonotonic);
    }

    [TestMethod]
    [DataRow("invalid-numeric")]
    [DataRow("duplicate-position")]
    [DataRow("nonlinear")]
    [DataRow("wrong-side")]
    [DataRow("invalid-range")]
    public void PartialAxisCannotHideAnyInvalidRowOrFit(string defect)
    {
        CalibrationOcrProposal[] labels = Labels();
        RustAxisGeometry geometry = Geometry();
        switch (defect)
        {
            case "invalid-numeric":
                labels = [.. labels, labels[0] with { RegionId = "invalid", NumericLiteral = false }];
                break;
            case "duplicate-position":
                labels = [.. labels, labels[0] with { RegionId = "duplicate" }];
                break;
            case "nonlinear": labels[2] = Value(labels[2], 12); break;
            case "wrong-side":
                labels = [.. labels, labels[0] with { RegionId = "inside", Polygon =
                    [new(95, 100), new(105, 100), new(105, 110), new(95, 110)] }];
                break;
            case "invalid-range": labels = Labels(first: 0); break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        CalibrationTickProposalResult draft = CalibrationTickProposal.CreateReviewDraft(geometry, Response(labels));

        Assert.IsFalse(draft.IsComplete);
        Assert.IsNull(draft.XReviewDraft);
        Assert.IsNotNull(draft.YReviewDraft);
        Assert.IsTrue(draft.Reasons.Count > 0);
    }

    [TestMethod]
    [DataRow("duplicate-id")]
    [DataRow("wrong-axis-identity")]
    [DataRow("stale-plot")]
    [DataRow("invalid-measured-geometry")]
    [DataRow("failed-response")]
    public void GlobalEvidenceFailuresPublishNeitherPartialAxis(string defect)
    {
        CalibrationOcrProposal[] labels = Labels();
        RustAxisGeometry geometry = Geometry();
        CalibrationOcrResponse response = Response(labels);
        switch (defect)
        {
            case "duplicate-id": labels[3] = labels[3] with { RegionId = labels[0].RegionId }; break;
            case "wrong-axis-identity": labels[0] = labels[0] with { SuggestedAxis = "Y" }; break;
            case "stale-plot": response = response with { Request = response.Request! with { Plot = new(71, 20, 350, 280) } }; break;
            case "invalid-measured-geometry": geometry = geometry with { XTickPositions = [200, 100, 300] }; break;
            case "failed-response": response = response with { FailureCode = CalibrationOcrReasonCodes.InvalidInput }; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        CalibrationTickProposalResult draft = CalibrationTickProposal.CreateReviewDraft(geometry, response);

        Assert.IsFalse(draft.IsComplete);
        Assert.IsNull(draft.XReviewDraft);
        Assert.IsNull(draft.YReviewDraft);
    }

    [TestMethod]
    public void ReviewDraftEstimatesOnlyMissingAxesAndPrefersMeasuredResult()
    {
        CalibrationOcrResponse response = Response(Labels());
        RustAxisGeometry geometry = Geometry() with { XTickPositions = [], YTickPositions = [] };
        Assert.IsFalse(CalibrationTickProposal.Create(geometry, response).IsComplete);
        CalibrationTickProposalResult draft = CalibrationTickProposal.CreateReviewDraft(geometry, response);
        Assert.IsTrue(draft.IsComplete, string.Join(",", draft.Reasons));
        Assert.IsTrue(draft.UsesEstimatedPositions);
        Assert.IsTrue(draft.PairedTicks.All(row => row.IsEstimated));
        Assert.AreEqual(new CalibrationOcrPoint(100, 250), draft.Anchors!.FirstSessionY0);
        geometry = Geometry() with { XTickPositions = [] };
        draft = CalibrationTickProposal.CreateReviewDraft(geometry, response);
        Assert.IsTrue(draft.IsComplete, string.Join(",", draft.Reasons));
        Assert.IsTrue(draft.PairedTicks.Where(row => row.Axis == "X").All(row => row.IsEstimated));
        Assert.IsTrue(draft.PairedTicks.Where(row => row.Axis == "Y").All(row => !row.IsEstimated));
        draft = CalibrationTickProposal.CreateReviewDraft(Geometry(), response);
        Assert.IsTrue(draft.IsComplete);
        Assert.IsFalse(draft.UsesEstimatedPositions);
    }

    [TestMethod]
    public void ReviewDraftRejectsMissingZeroNonlinearAndWrongRole()
    {
        RustAxisGeometry geometry = Geometry() with { XTickPositions = [], YTickPositions = [] };
        CalibrationOcrProposal[] labels = Labels();
        labels[5] = Value(labels[5], 1);
        Assert.IsFalse(CalibrationTickProposal.CreateReviewDraft(geometry, Response(labels)).IsComplete);
        labels = Labels(); labels[2] = Value(labels[2], 12);
        Assert.IsFalse(CalibrationTickProposal.CreateReviewDraft(geometry, Response(labels)).IsComplete);
        labels = Labels(); labels[0] = labels[0] with { SuggestedAxis = "Y" };
        Assert.IsFalse(CalibrationTickProposal.CreateReviewDraft(geometry, Response(labels)).IsComplete);
    }

    [TestMethod]
    public void ReviewDraftRejectsOverlapDuplicateConflictingMeasuredTicksAndStaleGeometry()
    {
        RustAxisGeometry geometry = Geometry() with { XTickPositions = [], YTickPositions = [] };
        CalibrationOcrProposal[] labels = Labels();
        labels[1] = labels[1] with { Polygon = labels[0].Polygon };
        Assert.IsFalse(CalibrationTickProposal.CreateReviewDraft(geometry, Response(labels)).IsComplete);
        labels = Labels(); labels[1] = labels[1] with { RegionId = labels[0].RegionId };
        Assert.IsFalse(CalibrationTickProposal.CreateReviewDraft(geometry, Response(labels)).IsComplete);
        geometry = Geometry() with { XTickPositions = [100, 200, 340], YTickPositions = [] };
        Assert.IsFalse(CalibrationTickProposal.CreateReviewDraft(geometry, Response(Labels())).IsComplete);
        CalibrationOcrResponse stale = Response(Labels());
        stale = stale with { Request = stale.Request! with { Plot = new(71, 20, 350, 280) } };
        Assert.IsFalse(CalibrationTickProposal.CreateReviewDraft(geometry, stale).IsComplete);
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
