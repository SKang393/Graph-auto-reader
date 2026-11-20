// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.Text.Json;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Ocr;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationTickProposalTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WideEndpointLiteralsSurviveActualRoleFilteringAndMeasuredAssociation(bool right)
    {
        // Supplied owned geometry/strings isolate role filtering, not OCR accuracy.
        var geometry = new RustAxisGeometry(100, 300, 300, 80, .9)
            { XTickPositions = [100, 200, 300], YTickPositions = [80, 190, 300] };
        var plot = new OcrRectangle(100, 80, 200, 220);
        CalibrationOcrProposal Literal(string id, OcrRectangle box, string text, double value)
        {
            var region = new OcrDetectedRegion(id, OcrPolygon.FromRectangle(box), 0, .9);
            OcrTextRole provisional = GraphTextRoleClassifier.Classify(
                region with { Context = new OcrRegionContext(NumericExpected: true) }, "", plot).Role;
            string axis = provisional switch { OcrTextRole.XTick => "X", OcrTextRole.YTick => "Y", _ => "" };
            string role = GraphTextRoleClassifier.Classify(region, text, plot).Role.ToString();
            return new(id, region.Polygon.Points.Select(point => new CalibrationOcrPoint(point.X, point.Y)).ToArray(),
                text, .9, value, text, true, role, axis, null);
        }
        CalibrationOcrProposal[] rows =
        [
            Literal("x-first", new(right ? 80 : 68, 310, 40, 16), "1000", 1000),
            Literal("x-middle", new(180, 310, 40, 16), "1001", 1001),
            Literal("x-final", new(right ? 292 : 280, 310, 40, 16), "1002", 1002),
            Literal("y-maximum", new(68, 75, 12, 10), "10", 10),
            Literal("y-middle", new(68, 185, 12, 10), "5", 5),
            Literal("y-zero", new(68, 295, 12, 10), "0", 0),
        ];
        var request = new CalibrationOcrRequest("owned-endpoint", "binding", new string('a', 64), new string('b', 64),
            400, 400, new(100, 80, 300, 300));
        CalibrationOcrProposal[] explicitlyTagged = rows.Select(row => row with
        {
            Role = row.RegionId.StartsWith("x-", StringComparison.Ordinal) ? "XTick" : "YTick",
            SuggestedAxis = row.RegionId.StartsWith("x-", StringComparison.Ordinal) ? "X" : "Y",
        }).ToArray();
        CalibrationTickProposalResult reference = CalibrationTickProposal.Create(geometry,
            new(request, explicitlyTagged, null, null, null, null, null));
        Assert.IsTrue(reference.IsComplete, string.Join(",", reference.Reasons));

        // The session reads only regions classified as X/Y before recognition.
        CalibrationOcrProposal[] selected = rows.Where(row => row.SuggestedAxis is "X" or "Y").ToArray();
        var response = new CalibrationOcrResponse(request, selected, null, null, null, null, null);
        string raw = JsonSerializer.Serialize(response);
        CalibrationTickProposalResult actual = CalibrationTickProposal.Create(geometry, response);
        Assert.IsTrue(actual.IsComplete, $"Selected {selected.Length} of 6 owned literal rows: {string.Join(",", actual.Reasons)}");
        Assert.HasCount(6, actual.PairedTicks);
        Assert.AreEqual(reference.Anchors, actual.Anchors);
        Assert.IsFalse(actual.UsesEstimatedPositions);
        Assert.AreEqual(raw, JsonSerializer.Serialize(response));
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(-4d)]
    [DataRow(0.5d)]
    [DataRow(1.5d)]
    public void PrintedXReviewRetainsValidatedLiteralEndpointsWithoutChangingSessionFailure(double first)
    {
        CalibrationOcrProposal[] labels = PrintedLabels(first);
        CalibrationOcrResponse response = Response(labels);
        string raw = JsonSerializer.Serialize(response);
        CalibrationTickProposalResult strict = CalibrationTickProposal.Create(Geometry(), response);
        CalibrationTickProposalResult review = CalibrationTickProposal.CreateReviewDraft(Geometry(), response);

        Assert.IsFalse(strict.IsComplete);
        Assert.IsNull(strict.PrintedXReviewDraft);
        Assert.IsFalse(review.IsComplete);
        Assert.IsNull(review.Anchors);
        Assert.IsNull(review.FirstSession);
        Assert.IsNull(review.FinalSession);
        Assert.IsNull(review.YMaximum);
        Assert.IsNull(review.XReviewDraft);
        CollectionAssert.AreEqual(new[] { CalibrationTickProposalReasons.InvalidRange }, review.Reasons.ToArray());
        Assert.AreEqual(JsonSerializer.Serialize(strict.PairedTicks), JsonSerializer.Serialize(review.PairedTicks));
        Assert.HasCount(6, review.PairedTicks);
        CalibrationPrintedXReviewDraft printed = review.PrintedXReviewDraft!;
        Assert.IsNotNull(printed);
        Assert.AreEqual(first, printed.FirstX);
        Assert.AreEqual(first + 6, printed.FinalX);
        Assert.AreEqual(10d, printed.YMaximum);
        Assert.AreEqual(new CalibrationTickAnchors(new(100, 250), new(100, 50), new(300, 250)), printed.Anchors);
        Assert.IsFalse(printed.UsesEstimatedPositions);
        Assert.AreEqual(raw, JsonSerializer.Serialize(response));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(29)]
    public void PositiveIntegerSessionReviewAlsoOffersLiteralCoordinates(int first)
    {
        CalibrationOcrResponse response = Response(Labels(first));
        CalibrationTickProposalResult strict = CalibrationTickProposal.Create(Geometry(), response);
        CalibrationTickProposalResult review = CalibrationTickProposal.CreateReviewDraft(Geometry(), response);
        Assert.IsTrue(strict.IsComplete);
        Assert.IsTrue(review.IsComplete);
        Assert.AreEqual(first, review.FirstSession);
        Assert.AreEqual(strict.Anchors, review.Anchors);
        Assert.IsNull(strict.PrintedXReviewDraft);
        Assert.IsNotNull(review.PrintedXReviewDraft);
        Assert.AreEqual(review.Anchors, review.PrintedXReviewDraft.Anchors);
        Assert.AreEqual((double)first, review.PrintedXReviewDraft.FirstX);
        Assert.AreEqual(review.FinalSession!.Value, review.PrintedXReviewDraft.FinalX);
    }

    [TestMethod]
    [DataRow("X")]
    [DataRow("Y")]
    [DataRow("both")]
    public void PrintedXReviewRetainsEstimatedCoordinateProvenance(string missingAxis)
    {
        RustAxisGeometry geometry = Geometry() with
        {
            XTickPositions = missingAxis is "X" or "both" ? [] : Geometry().XTickPositions,
            YTickPositions = missingAxis is "Y" or "both" ? [] : Geometry().YTickPositions,
        };
        CalibrationTickProposalResult review = CalibrationTickProposal.CreateReviewDraft(geometry, Response(PrintedLabels(0)));
        CalibrationPrintedXReviewDraft printed = review.PrintedXReviewDraft!;
        Assert.IsNotNull(printed);
        Assert.IsFalse(review.IsComplete);
        Assert.IsNull(review.FirstSession);
        Assert.IsTrue(printed.UsesEstimatedPositions);
        Assert.IsTrue(review.UsesEstimatedPositions);
        Assert.AreEqual(new CalibrationTickAnchors(new(100, 250), new(100, 50), new(300, 250)), printed.Anchors);
        Assert.IsTrue(review.PairedTicks.All(pair => pair.IsEstimated == (missingAxis == "both" || pair.Axis == missingAxis)));
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("outlier")]
    [DataRow("duplicate-position")]
    [DataRow("duplicate-id")]
    [DataRow("missing-zero")]
    [DataRow("insufficient")]
    [DataRow("nonmonotonic")]
    [DataRow("zero-confidence")]
    [DataRow("stale-plot")]
    public void PrintedXReviewCannotHideAnyIncludedEvidenceOrFitFailure(string defect)
    {
        CalibrationOcrProposal[] labels = PrintedLabels(0);
        switch (defect)
        {
            case "malformed": labels[1] = labels[1] with { NumericLiteral = false }; break;
            case "outlier": labels[2] = Value(labels[2], 12); break;
            case "duplicate-position": labels = [.. labels, labels[0] with { RegionId = "duplicate" }]; break;
            case "duplicate-id": labels[3] = labels[3] with { RegionId = labels[0].RegionId }; break;
            case "missing-zero": labels = labels.Select(row => row.SuggestedAxis == "Y" ? Value(row, row.NumericValue!.Value + 2) : row).ToArray(); break;
            case "insufficient": labels = labels.Where(row => row.RegionId != "x-middle").ToArray(); break;
            case "nonmonotonic": labels[1] = Value(labels[1], -1); break;
            case "zero-confidence": labels[0] = labels[0] with { Confidence = 0 }; break;
            case "stale-plot": break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }
        CalibrationOcrResponse response = Response(labels);
        if (defect == "stale-plot") response = response with { Request = response.Request! with { Plot = new(71, 20, 350, 280) } };
        CalibrationTickProposalResult review = CalibrationTickProposal.CreateReviewDraft(Geometry(), response);
        Assert.IsFalse(review.IsComplete);
        Assert.IsNull(review.PrintedXReviewDraft);
        Assert.IsNull(review.Anchors);
        Assert.IsTrue(review.Reasons.Count > 0);
    }

    [TestMethod]
    public void PrintedXReviewAndExplicitEnteredSessionRemainSeparateCachedEvidencePaths()
    {
        CalibrationOcrProposal[] labels = PrintedLabels(0);
        CalibrationOcrResponse response = Response(labels);
        string raw = JsonSerializer.Serialize(response);
        CalibrationTickFitEvidence[] evidence = labels.Select(row => new CalibrationTickFitEvidence(row, null)).ToArray();
        CalibrationTickProposalResult printed = CalibrationTickProposal.CreateReviewDraft(Geometry(), response, evidence);
        Assert.IsNotNull(printed.PrintedXReviewDraft);
        Assert.IsFalse(printed.IsComplete);

        CalibrationTickProposalResult entered = CalibrationTickProposal.CreateReviewDraftForFirstSession(Geometry(), response, evidence, 1);
        Assert.IsTrue(entered.IsComplete);
        Assert.AreEqual(1, entered.FirstSession);
        Assert.IsTrue(entered.UsesUserEnteredSessionPosition);
        Assert.IsNull(entered.PrintedXReviewDraft);
        CalibrationTickProposalResult invalidEntered = CalibrationTickProposal.CreateReviewDraftForFirstSession(Geometry(), response, evidence, 0);
        Assert.IsFalse(invalidEntered.IsComplete);
        Assert.IsNull(invalidEntered.PrintedXReviewDraft);
        Assert.AreEqual(raw, JsonSerializer.Serialize(response));
    }

    [TestMethod]
    [DataRow(480)]
    [DataRow(1000)]
    public void SixLiteralLabelsRetainCompleteCalibrationAcrossPlotWidths(int plotWidth)
    {
        var geometry = new RustAxisGeometry(100, 380, 100 + plotWidth, 80, .9)
            { XTickPositions = [140, 340, 540], YTickPositions = [80, 230, 380] };
        var plot = new OcrRectangle(100, 80, plotWidth, 300);
        CalibrationOcrProposal Literal(string id, int x, int y, string text, double value)
        {
            var region = new OcrDetectedRegion(id, OcrPolygon.FromRectangle(new(x, y, 12, 10)), 0, .9);
            OcrTextRole role = GraphTextRoleClassifier.Classify(region, text, plot).Role;
            OcrTextRole beforeRecognition = GraphTextRoleClassifier.Classify(
                region with { Context = new OcrRegionContext(NumericExpected: true) }, "", plot).Role;
            string axis = beforeRecognition switch { OcrTextRole.XTick => "X", OcrTextRole.YTick => "Y", _ => "" };
            return new(id, region.Polygon.Points.Select(point => new CalibrationOcrPoint(point.X, point.Y)).ToArray(),
                text, .9, value, text, true, role.ToString(), axis, null);
        }
        CalibrationOcrProposal[] labels =
        [
            Literal("x-first", 134, 398, "1", 1), Literal("x-middle", 334, 398, "2", 2), Literal("x-final", 534, 398, "3", 3),
            Literal("y-maximum", 68, 75, "10", 10), Literal("y-middle", 68, 225, "5", 5), Literal("y-zero", 68, 376, "0", 0),
        ];
        var response = new CalibrationOcrResponse(new("owned-width", "binding", new string('a', 64), new string('b', 64),
            1200, 480, new(100, 80, 100 + plotWidth, 380)), labels, null, null, null, null, null);

        CalibrationTickProposalResult result = CalibrationTickProposal.Create(geometry, response);

        Assert.IsTrue(result.IsComplete, string.Join(",", result.Reasons));
        Assert.AreEqual(3, result.PairedTicks.Count(pair => pair.Axis == "X"));
        Assert.AreEqual(3, result.PairedTicks.Count(pair => pair.Axis == "Y"));
        Assert.AreEqual(new CalibrationTickAnchors(new(140, 380), new(140, 80), new(540, 380)), result.Anchors);
        Assert.AreEqual(0d, result.PairedTicks.Single(pair => pair.Label.RegionId == "y-zero").Value);
        Assert.AreEqual(381d, labels[5].Polygon.Average(point => point.Y), "Glyph alignment must not move the observed tick anchor.");
    }

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
    [DataRow("X", -1, false)]
    [DataRow("X", 1, false)]
    [DataRow("Y", -1, false)]
    [DataRow("Y", 1, false)]
    [DataRow("X", -1, true)]
    [DataRow("X", 1, true)]
    [DataRow("Y", -1, true)]
    [DataRow("Y", 1, true)]
    public void DirectionalAssociationOppositeMinorTickRetainsObservedPairs(string axis, int direction, bool reviewDraft)
    {
        CalibrationOcrProposal[] labels = Labels();
        int row = axis == "X" ? 0 : 4;
        int pixel = axis == "X" ? 100 : 150;
        labels[row] = OffsetLabel(labels[row], axis, direction * 7);
        var response = Response(labels);
        string original = JsonSerializer.Serialize(response);
        CalibrationTickProposalResult expected = CalibrationTickProposal.Create(Geometry(), response);
        Assert.IsTrue(expected.IsComplete);
        RustAxisGeometry geometry = WithAdditionalTick(Geometry(), axis, pixel - direction * 10);

        CalibrationTickProposalResult actual = reviewDraft
            ? CalibrationTickProposal.CreateReviewDraft(geometry, response)
            : CalibrationTickProposal.Create(geometry, response);

        Assert.IsTrue(actual.IsComplete, string.Join(",", actual.Reasons));
        Assert.AreEqual(expected.Anchors, actual.Anchors);
        Assert.AreEqual(expected.FirstSession, actual.FirstSession);
        Assert.AreEqual(expected.FinalSession, actual.FinalSession);
        Assert.AreEqual(expected.YMaximum, actual.YMaximum);
        Assert.AreEqual(JsonSerializer.Serialize(expected.PairedTicks), JsonSerializer.Serialize(actual.PairedTicks));
        Assert.AreEqual(original, JsonSerializer.Serialize(response));
    }

    [TestMethod]
    [DataRow("X", -1)]
    [DataRow("X", 1)]
    [DataRow("Y", -1)]
    [DataRow("Y", 1)]
    public void DirectionalAssociationExactHalfCellRemainsAmbiguous(string axis, int direction)
    {
        CalibrationOcrProposal[] labels = Labels();
        int row = axis == "X" ? 0 : 4;
        int pixel = axis == "X" ? 100 : 150;
        labels[row] = OffsetLabel(labels[row], axis, direction * 5);
        AssertRejected(WithAdditionalTick(Geometry(), axis, pixel + direction * 10), labels,
            CalibrationTickProposalReasons.AmbiguousAssociation);
    }

    [TestMethod]
    [DataRow("X", -10d)]
    [DataRow("X", 10d)]
    [DataRow("Y", -10d)]
    [DataRow("Y", 10d)]
    [DataRow("X", -10.01d)]
    [DataRow("X", 10.01d)]
    [DataRow("Y", -10.01d)]
    [DataRow("Y", 10.01d)]
    public void DirectionalAssociationFontBoundIsUnchanged(string axis, double offset)
    {
        CalibrationOcrProposal[] labels = Labels();
        labels[axis == "X" ? 0 : 4] = OffsetLabel(labels[axis == "X" ? 0 : 4], axis, offset);
        if (Math.Abs(offset) > 10)
        {
            AssertRejected(Geometry(), labels, CalibrationTickProposalReasons.UnpairedLabel);
            return;
        }
        CalibrationTickProposalResult actual = CalibrationTickProposal.Create(Geometry(), Response(labels));
        Assert.IsTrue(actual.IsComplete, string.Join(",", actual.Reasons));
        Assert.AreEqual(CalibrationTickProposal.Create(Geometry(), Response(Labels())).Anchors, actual.Anchors);
    }

    [TestMethod]
    [DataRow("X", -1)]
    [DataRow("X", 1)]
    [DataRow("Y", -1)]
    [DataRow("Y", 1)]
    public void DirectionalAssociationDoesNotSkipTheActualNearestMinorTick(string axis, int direction)
    {
        CalibrationOcrProposal[] labels = Labels();
        int row = axis == "X" ? 0 : 4;
        int pixel = axis == "X" ? 100 : 150;
        labels[row] = OffsetLabel(labels[row], axis, direction * 6);
        int minor = pixel + direction * 10;
        CalibrationTickProposalResult actual = CalibrationTickProposal.Create(
            WithAdditionalTick(Geometry(), axis, minor), Response(labels));
        CalibrationPairedTick paired = actual.PairedTicks.Single(pair => pair.Label.RegionId == labels[row].RegionId);
        Assert.AreEqual(minor, paired.PixelPosition);
        Assert.AreEqual(labels[row].NumericValue, paired.Value);
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
    public void ExplicitHumanCorrectionSuppliesValidationSupportWithoutChangingOcrConfidence(bool estimated)
    {
        CalibrationOcrProposal[] labels = Labels();
        labels[4] = labels[4] with { Text = "", NumericValue = null, NormalizedText = null, NumericLiteral = false,
            Confidence = 0, Role = "Other", FailureCode = CalibrationOcrReasonCodes.RecognitionFailed };
        CalibrationOcrResponse response = Response(labels);
        CalibrationTickFitEvidence[] evidence = labels.Select((row, index) => new CalibrationTickFitEvidence(row, index == 4 ? "5" : null)).ToArray();
        RustAxisGeometry geometry = estimated ? Geometry() with { XTickPositions = [], YTickPositions = [] } : Geometry();

        CalibrationTickProposalResult draft = CalibrationTickProposal.CreateReviewDraft(geometry, response, evidence);

        Assert.IsTrue(draft.IsComplete, string.Join(",", draft.Reasons));
        CalibrationPairedTick corrected = draft.PairedTicks.Single(pair => pair.Label.RegionId == labels[4].RegionId);
        Assert.IsTrue(corrected.IsUserCorrected);
        Assert.AreEqual(0d, corrected.Label.Confidence, "The human support weight is not an OCR confidence.");
        Assert.AreEqual(5d, corrected.Value);
        Assert.AreSame(labels[4], response.Proposals[4]);
        Assert.AreEqual("", labels[4].Text);
        Assert.AreEqual(0d, labels[4].Confidence);
        Assert.AreEqual(CalibrationOcrReasonCodes.RecognitionFailed, labels[4].FailureCode);
        Assert.IsFalse(CalibrationTickProposal.CreateReviewDraft(geometry, response).IsComplete,
            "The untouched failed OCR receipt still cannot establish calibration.");
    }

    [TestMethod]
    public void UncorrectedZeroConfidenceAndDetachedCorrectionEvidenceRemainRejected()
    {
        CalibrationOcrProposal[] labels = Labels();
        labels[4] = labels[4] with { Confidence = 0 };
        AssertRejected(Geometry(), labels, CalibrationTickProposalReasons.InvalidNumericLabel);
        CalibrationOcrResponse response = Response(labels);
        CalibrationTickFitEvidence[] evidence = labels.Select(row => new CalibrationTickFitEvidence(row, null)).ToArray();
        evidence[4] = new(labels[4] with { }, "5");

        CalibrationTickProposalResult detached = CalibrationTickProposal.CreateReviewDraft(Geometry(), response, evidence);

        Assert.IsFalse(detached.IsComplete);
        CollectionAssert.Contains(detached.Reasons.ToArray(), CalibrationTickProposalReasons.InvalidEvidence);
        Assert.AreEqual(0d, labels[4].Confidence);
    }

    [TestMethod]
    [DataRow("8", CalibrationTickProposalReasons.InconsistentSpacing)]
    [DataRow("0", CalibrationTickProposalReasons.InsufficientPairs)]
    public void HumanCorrectionCannotHideNumericConflicts(string value, string expectedReason)
    {
        CalibrationOcrProposal[] labels = Labels();
        labels[4] = labels[4] with { Confidence = 0 };
        CalibrationTickFitEvidence[] evidence = labels.Select((row, index) => new CalibrationTickFitEvidence(row, index == 4 ? value : null)).ToArray();

        CalibrationTickProposalResult draft = CalibrationTickProposal.CreateReviewDraft(Geometry(), Response(labels), evidence);

        Assert.IsFalse(draft.IsComplete);
        Assert.IsNull(draft.YReviewDraft);
        CollectionAssert.Contains(draft.Reasons.ToArray(), expectedReason);
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
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void OtherAxisFailureDoesNotHideObservedZeroOrInvalidXRange(bool reviewDraft, bool missingYZero)
    {
        CalibrationOcrProposal[] labels = missingYZero
            ? Labels().Select(row => row.SuggestedAxis == "Y" ? Value(row, row.NumericValue!.Value + 2) : row).ToArray()
            : Labels(first: 0);
        int badRow = missingYZero ? 0 : 3;
        labels[badRow] = labels[badRow] with { NumericLiteral = false };

        CalibrationTickProposalResult result = reviewDraft
            ? CalibrationTickProposal.CreateReviewDraft(Geometry(), Response(labels))
            : CalibrationTickProposal.Create(Geometry(), Response(labels));

        Assert.IsFalse(result.IsComplete);
        Assert.IsNull(result.Anchors);
        Assert.IsNull(result.XReviewDraft);
        Assert.IsNull(result.YReviewDraft);
        CollectionAssert.Contains(result.Reasons.ToArray(), CalibrationTickProposalReasons.InvalidNumericLabel);
        CollectionAssert.Contains(result.Reasons.ToArray(), missingYZero
            ? CalibrationTickProposalReasons.ObservedZeroRequired : CalibrationTickProposalReasons.InvalidRange);
        Assert.HasCount(3, result.PairedTicks.Where(pair => pair.Axis == (missingYZero ? "Y" : "X")).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InsufficientAxisDoesNotClaimAnEndpointRangeFailure(bool reviewDraft)
    {
        CalibrationOcrProposal[] labels = Labels(first: 0).Where(row => row.RegionId != "y-zero").ToArray();

        CalibrationTickProposalResult result = reviewDraft
            ? CalibrationTickProposal.CreateReviewDraft(Geometry(), Response(labels))
            : CalibrationTickProposal.Create(Geometry(), Response(labels));

        Assert.IsFalse(result.IsComplete);
        Assert.IsNull(result.Anchors);
        Assert.IsNull(result.XReviewDraft);
        Assert.IsNull(result.YReviewDraft);
        CollectionAssert.Contains(result.Reasons.ToArray(), CalibrationTickProposalReasons.InsufficientPairs);
        CollectionAssert.Contains(result.Reasons.ToArray(), CalibrationTickProposalReasons.InvalidRange);
        CollectionAssert.DoesNotContain(result.Reasons.ToArray(), CalibrationTickProposalReasons.ObservedZeroRequired);
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

    [TestMethod]
    public void ConsistentButAmbiguousCommaScaleRequiresAnUnambiguousNonzeroLabel()
    {
        var labels = Labels();
        labels[3] = Value(labels[3], 2500) with { Text = "2,500" };
        labels[4] = Value(labels[4], 1250) with { Text = "1,250" };

        AssertRejected(Geometry(), labels, CalibrationTickProposalReasons.AmbiguousNumericScale);
        var draft = CalibrationTickProposal.CreateReviewDraft(Geometry(), Response(labels));
        Assert.IsNull(draft.YReviewDraft);
        Assert.IsNotNull(draft.XReviewDraft);
        Assert.IsNull(draft.PrintedXReviewDraft);

        labels[4] = Value(labels[4], 1250);
        Assert.IsTrue(CalibrationTickProposal.CreateReviewDraft(Geometry(), Response(labels)).IsComplete);
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

    private static CalibrationOcrProposal[] PrintedLabels(double first) => Labels()
        .Select(row => row.SuggestedAxis == "X" ? Value(row, row.NumericValue!.Value + first - 1) : row).ToArray();

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

    private static CalibrationOcrProposal OffsetLabel(CalibrationOcrProposal label, string axis, double offset) => label with
    {
        Polygon = label.Polygon.Select(point => axis == "X" ? point with { X = point.X + offset }
            : point with { Y = point.Y + offset }).ToArray(),
    };

    private static RustAxisGeometry WithAdditionalTick(RustAxisGeometry geometry, string axis, int tick) => axis == "X"
        ? geometry with { XTickPositions = geometry.XTickPositions.Append(tick).Order().ToArray() }
        : geometry with { YTickPositions = geometry.YTickPositions.Append(tick).Order().ToArray() };

    private static CalibrationOcrResponse Response(IReadOnlyList<CalibrationOcrProposal> labels) =>
        new(new("owned-request", "binding", new string('a', 64), new string('b', 64), 450, 340, new(70, 20, 350, 280)),
            labels, new(false, 3, 3, 0, []), new(false, 3, 3, 0, []), null, null, null);
}
