// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Ocr;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationNumericLiteralTests
{
    [TestMethod]
    [DataRow("1 2 3")]
    [DataRow("1\t2")]
    [DataRow("1\n2")]
    [DataRow("1\u00a0234")]
    [DataRow("1 234")]
    public void InternalWhitespaceIsNotAcceptedAsOneCalibrationNumber(string text) =>
        Assert.IsFalse(CalibrationNumericLiteral.IsLiteral(text));

    [TestMethod]
    [DataRow(" 20 ")]
    [DataRow("1,000")]
    [DataRow("1,234.5")]
    [DataRow("1.234,5")]
    [DataRow("-12.345,6%")]
    [DataRow(".75")]
    [DataRow("\u22120.25")]
    public void OuterWhitespaceAndSupportedPunctuationFormatsRemainValid(string text) =>
        Assert.IsTrue(CalibrationNumericLiteral.IsLiteral(text));

    [TestMethod]
    public void ForgedSpacedNumericResponseIsRejectedAtWorkerBoundary()
    {
        CalibrationOcrRequest request = CalibrationOcrClientTests.Request();
        CalibrationOcrResponse response = CalibrationOcrClientTests.Success(request);
        CalibrationOcrProposal proposal = response.Proposals.Single() with
        {
            Text = "1 2 3",
            NumericValue = 123,
            NormalizedText = "123",
            NumericLiteral = true,
        };

        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(
            request, response with { Proposals = [proposal] }));
    }

    [TestMethod]
    public void ForgedSpacedNumericTickCannotEnterCalibrationProposal()
    {
        RustAxisGeometry geometry = new(70, 280, 350, 20, .9)
        {
            XTickPositions = [100, 200, 300],
            YTickPositions = [50, 150, 250],
        };
        CalibrationOcrProposal[] labels =
        [
            Label("x-first", "X", 100, 123),
            Label("x-middle", "X", 200, 246),
            Label("x-final", "X", 300, 369),
            Label("y-maximum", "Y", 50, 10),
            Label("y-middle", "Y", 150, 5),
            Label("y-zero", "Y", 250, 0),
        ];
        CalibrationOcrRequest request = new("owned-request", "binding", new string('a', 64),
            new string('b', 64), 450, 340, new(70, 20, 350, 280));
        var sequence = new CalibrationOcrSequenceCheck(false, 3, 3, 0, []);
        CalibrationOcrResponse response = new(request, labels, sequence, sequence, null, null, null);

        Assert.IsTrue(CalibrationTickProposal.Create(geometry, response).IsComplete);
        labels[0] = labels[0] with { Text = "1 2 3" };
        labels[1] = labels[1] with { Text = "2 4 6" };
        labels[2] = labels[2] with { Text = "3 6 9" };

        CalibrationTickProposalResult result = CalibrationTickProposal.Create(geometry, response);

        Assert.IsFalse(result.IsComplete);
        Assert.IsNull(result.Anchors);
        CollectionAssert.Contains(result.Reasons.ToArray(), CalibrationTickProposalReasons.InvalidNumericLabel);
        CalibrationTickProposalResult draft = CalibrationTickProposal.CreateReviewDraft(geometry, response);
        Assert.IsNull(draft.XReviewDraft);
        Assert.IsNotNull(draft.YReviewDraft);
        Assert.IsFalse(draft.IsComplete);
    }

    private static CalibrationOcrProposal Label(string id, string axis, int pixel, double value)
    {
        double x = axis == "X" ? pixel : 40;
        double y = axis == "Y" ? pixel : 291;
        string text = value.ToString(CultureInfo.InvariantCulture);
        return new(id, [new(x - 6, y - 5), new(x + 6, y - 5), new(x + 6, y + 5), new(x - 6, y + 5)],
            text, .9, value, text, true, axis == "X" ? "XTick" : "YTick", axis, null);
    }
}
