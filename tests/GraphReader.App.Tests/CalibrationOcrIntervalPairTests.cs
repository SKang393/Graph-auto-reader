// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.CalibrationOcr;
using GraphReader.App.Services;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationOcrIntervalPairTests
{
    [TestMethod]
    [DataRow("42 57", "42", "57", true)]
    [DataRow("4257", "42", "57", false)]
    [DataRow("42 57", "42", "51", false)]
    [DataRow("42 57", "57", "42", false)]
    [DataRow("1 234", "1", "234", false)]
    [DataRow("5 .2", "5", "2", false)]
    public void ASeparatedReadRequiresWhitespaceAndBothOriginalCropReadings(string whole, string left, string right, bool corroborated)
    {
        var first = Row("first", left, 26, 38);
        var second = Row("second", right, 42, 55);
        Assert.AreEqual(corroborated, CalibrationOcrSession.CorroboratesIntervalPair(whole, first, second));
        Assert.IsFalse(CalibrationOcrSession.CorroboratesIntervalPair(whole, first, second with { FailureCode = "no_read" }));
    }

    [TestMethod]
    public void WorkerReceiptRequiresBothChildrenAndTheirOriginalParentInNeighboringMeasuredCells()
    {
        var request = CalibrationOcrClientTests.Request() with { Plot = new(8, 5, 155, 60), MeasuredXTicks = [20, 40, 60, 80] };
        var parent = new CalibrationOcrParentReading("parent", "42 57", .8, Rect(26, 55));
        var left = Row("left", "42", 26, 38) with { ParentReading = parent };
        var right = Row("right", "57", 42, 55) with { ParentReading = parent };
        var original = CalibrationOcrClientTests.Success(request);
        bool Valid(params CalibrationOcrProposal[] rows) => CalibrationOcrWorkerClient.IsValidResponse(request, original with { Proposals = rows });
        Assert.IsTrue(Valid(left, right));
        Assert.IsFalse(Valid(left));
        Assert.IsFalse(Valid(left, right with { Polygon = Rect(50, 65) }));
        Assert.IsFalse(Valid(left, right with { ParentReading = parent with { Text = "42 51" } }));
        Assert.IsFalse(Valid(left with { ParentReading = parent with { Polygon = Rect(20, 55) } }, right));
        Assert.IsTrue(Valid(left with { ParentReading = null }));
    }

    private static CalibrationOcrProposal Row(string id, string text, int left, int right) =>
        new(id, Rect(left, right), text, .9, double.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            text, true, "XTick", "X", null) { IsIntervalXLabel = true };

    private static CalibrationOcrPoint[] Rect(int left, int right) => [new(left, 75), new(right, 75), new(right, 83), new(left, 83)];
}
