// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GraphReader.App.Integration.CalibrationOcr;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Ocr;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationIntervalLabelTests
{
    private static readonly int[] ExpectedCenters = [41, 61, 81];

    [TestMethod]
    [DataRow(1d)]
    [DataRow(0d)]
    public void IntervalLabelsNeverSnapToEdgeTicksAndOnlySupplyEstimatedReviewPositions(double first)
    {
        RustAxisGeometry geometry = Geometry();
        CalibrationOcrResponse response = Response(first);
        string original = JsonSerializer.Serialize(response), originalGeometry = JsonSerializer.Serialize(geometry);
        CalibrationTickProposalResult strict = CalibrationTickProposal.Create(geometry, response);
        Assert.IsFalse(strict.IsComplete);
        Assert.IsFalse(strict.PairedTicks.Any(pair => pair.Axis == "X"));
        CollectionAssert.Contains(strict.Reasons.ToArray(), CalibrationTickProposalReasons.UnpairedLabel);

        CalibrationTickProposalResult review = CalibrationTickProposal.CreateReviewDraft(geometry, response);
        CollectionAssert.AreEqual(ExpectedCenters, review.PairedTicks.Where(pair => pair.Axis == "X").Select(pair => pair.PixelPosition).ToArray());
        Assert.IsTrue(review.PairedTicks.Where(pair => pair.Axis == "X").All(pair => pair.IsEstimated));
        Assert.IsTrue(review.PairedTicks.Where(pair => pair.Axis == "Y").All(pair => !pair.IsEstimated));
        Assert.IsTrue(review.UsesEstimatedPositions);
        if (first == 1)
        {
            Assert.IsTrue(review.IsComplete, string.Join(',', review.Reasons));
            Assert.AreEqual(new CalibrationOcrPoint(41, 65), review.Anchors!.FirstSessionY0);
            Assert.AreEqual(new CalibrationOcrPoint(81, 65), review.Anchors.FinalSessionY0);
        }
        else
        {
            Assert.IsFalse(review.IsComplete);
            Assert.IsNotNull(review.PrintedXReviewDraft);
            Assert.IsTrue(review.PrintedXReviewDraft.UsesEstimatedPositions);
            Assert.AreEqual(0d, review.PrintedXReviewDraft.FirstX);
        }
        Assert.AreEqual(original, JsonSerializer.Serialize(response));
        Assert.AreEqual(originalGeometry, JsonSerializer.Serialize(geometry));
    }

    [TestMethod]
    [DataRow("missing-request-ticks")]
    [DataRow("mismatched-request-ticks")]
    [DataRow("boundary-crossing")]
    [DataRow("wrong-axis")]
    [DataRow("too-few-ticks")]
    public void InvalidIntervalProvenanceCannotPublishAnAxisDraft(string defect)
    {
        RustAxisGeometry geometry = Geometry();
        CalibrationOcrResponse response = Response(1);
        CalibrationOcrProposal[] rows = response.Proposals.ToArray();
        switch (defect)
        {
            case "missing-request-ticks": response = response with { Request = response.Request! with { MeasuredXTicks = null } }; break;
            case "mismatched-request-ticks": response = response with { Request = response.Request! with { MeasuredXTicks = [30, 51, 70, 90] } }; break;
            case "boundary-crossing": rows[0] = rows[0] with { Polygon = [new(27, 75), new(35, 75), new(35, 87), new(27, 87)] }; break;
            case "wrong-axis": rows[3] = rows[3] with { IsIntervalXLabel = true }; break;
            case "too-few-ticks": geometry = geometry with { XTickPositions = [30, 50, 90] }; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }
        CalibrationTickProposalResult review = CalibrationTickProposal.CreateReviewDraft(geometry, response with { Proposals = rows });
        Assert.IsFalse(review.IsComplete);
        Assert.IsNull(review.XReviewDraft);
        Assert.IsNull(review.PrintedXReviewDraft);
        Assert.IsNull(review.YReviewDraft);
    }

    [TestMethod]
    public void TextCorrectionRetainsIntervalPositionProvenanceAndInvalidSequenceStillFails()
    {
        CalibrationOcrResponse response = Response(1);
        CalibrationTickFitEvidence[] evidence = response.Proposals.Select(row => new CalibrationTickFitEvidence(row,
            row.RegionId == "x1" ? "8" : null)).ToArray();
        Assert.IsTrue(evidence[1].EffectiveProposal.IsIntervalXLabel);
        CalibrationTickProposalResult result = CalibrationTickProposal.CreateReviewDraft(Geometry(), response, evidence);
        Assert.IsFalse(result.IsComplete);
        Assert.IsNull(result.XReviewDraft);
        Assert.IsNull(result.PrintedXReviewDraft);
        Assert.IsTrue(result.PairedTicks.Where(pair => pair.Axis == "X").All(pair => pair.IsEstimated));
    }

    [TestMethod]
    public async Task PrivateWirePreservesIntervalProvenanceAndAbsentFlagDefaultsFalse()
    {
        CalibrationOcrResponse response = Response(1);
        using var stream = new MemoryStream();
        await CalibrationOcrWire.WriteResponseAsync(stream, response, CancellationToken.None);
        stream.Position = 0;
        CalibrationOcrResponse actual = await CalibrationOcrWire.ReadResponseAsync(stream, CancellationToken.None);
        Assert.IsTrue(actual.Proposals.Where(row => row.SuggestedAxis == "X").All(row => row.IsIntervalXLabel));
        Assert.IsTrue(actual.Proposals.Where(row => row.SuggestedAxis == "Y").All(row => !row.IsIntervalXLabel));

        using var legacy = new MemoryStream();
        await CalibrationOcrWire.WriteResponseAsync(legacy, response with
        {
            Proposals = response.Proposals.Select(row => row with { IsIntervalXLabel = false }).ToArray(),
        }, CancellationToken.None);
        Assert.IsFalse(Encoding.UTF8.GetString(legacy.ToArray()).Contains("is_interval_x_label", StringComparison.Ordinal));
        legacy.Position = 0;
        actual = await CalibrationOcrWire.ReadResponseAsync(legacy, CancellationToken.None);
        Assert.IsTrue(actual.Proposals.All(row => !row.IsIntervalXLabel));
    }

    [TestMethod]
    public void RecognitionProposalCarriesDetectorProvenanceWithoutChangingLiteralValue()
    {
        const string id = "owned-interval";
        var candidate = new OcrDetectedRegion(id, OcrPolygon.FromRectangle(new(37, 75, 8, 12)), 0, .9,
            Evidence: new(2, .5, .9, 0, false, [ConnectedComponentTextRegionDetector.IntervalXLabelReason]));
        var reading = new OcrRecognition(id, OcrSourceImage.Original, [new("1", .9, OcrSourceImage.Original)], 0);
        MethodInfo method = typeof(CalibrationOcrSession).GetMethod("Proposal", BindingFlags.Static | BindingFlags.NonPublic)!;
        var proposal = (CalibrationOcrProposal)method.Invoke(null, [candidate, reading, new OcrRectangle(30, 10, 60, 55)])!;
        Assert.IsTrue(proposal.IsIntervalXLabel);
        Assert.IsTrue(proposal.RequiresReview);
        Assert.AreEqual("1", proposal.Text);
        Assert.AreEqual(1d, proposal.NumericValue);
        Assert.AreEqual("XTick", proposal.Role);
    }

    private static RustAxisGeometry Geometry() => new(30, 65, 90, 10, .9)
    {
        XTickPositions = [30, 50, 70, 90], YTickPositions = [15, 40, 65],
    };

    private static CalibrationOcrResponse Response(double first)
    {
        var rows = new List<CalibrationOcrProposal>();
        for (int index = 0; index < 3; index++)
        {
            rows.Add(Label("x" + index.ToString(CultureInfo.InvariantCulture), "X", 41 + 20 * index, first + index) with { IsIntervalXLabel = true });
            rows.Add(Label("y" + index.ToString(CultureInfo.InvariantCulture), "Y", 15 + 25 * index, 10 - 5 * index));
        }
        var ordered = rows.OrderBy(row => row.RegionId, StringComparer.Ordinal).ToArray();
        return new(new("owned-interval", "binding", new string('a', 64), new string('b', 64), 128, 110, new(30, 10, 90, 65))
        {
            MeasuredXTicks = [30, 50, 70, 90], MeasuredYTicks = [15, 40, 65],
        }, ordered, null, null, null, null, null);
    }

    private static CalibrationOcrProposal Label(string id, string axis, int position, double value)
    {
        double x = axis == "X" ? position : 17, y = axis == "X" ? 81 : position;
        string text = value.ToString(CultureInfo.InvariantCulture);
        return new(id, [new(x - 4, y - 6), new(x + 4, y - 6), new(x + 4, y + 6), new(x - 4, y + 6)],
            text, .9, value, text, true, axis == "X" ? "XTick" : "YTick", axis, null);
    }
}
