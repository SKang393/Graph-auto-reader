// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using GraphReader.App.Integration.CalibrationOcr;
using GraphReader.App.Services;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationOcrMeasuredTicksTests
{
    private static readonly int[] DuplicateTicks = [20, 20];
    private static readonly int[] ExpectedYTicks = [10, 40, 80];
    [TestMethod]
    public async Task OptionalTicksRoundTripAndEchoCompareByValue()
    {
        var request = Request() with { MeasuredXTicks = [20, 50, 80], MeasuredYTicks = [10, 40, 80] };
        using var stream = new MemoryStream();
        await CalibrationOcrWire.WriteRequestAsync(stream, request, new byte[] { 1 }, CancellationToken.None);
        stream.Position = 0;
        CalibrationOcrWorkItem? item = await CalibrationOcrWire.ReadRequestAsync(stream, CancellationToken.None);
        Assert.IsNotNull(item);
        Assert.IsTrue(request.Matches(item.Request));
        Assert.IsFalse(request.Matches(item.Request with { MeasuredXTicks = [20, 50, 81] }));
        Assert.IsFalse(request.Matches(item.Request with { MeasuredYTicks = [10, 40, 79] }));
        Assert.IsFalse(request.Matches(item.Request with { MeasuredYTicks = null }));
        Assert.IsTrue(item.Request.ValidMeasuredXTicks());
        Assert.IsTrue(item.Request.ValidMeasuredYTicks());
        Assert.IsTrue(Request().ValidMeasuredXTicks());
        Assert.IsTrue(Request().ValidMeasuredYTicks());
        using var legacyStream = new MemoryStream();
        await CalibrationOcrWire.WriteRequestAsync(legacyStream, Request(), new byte[] { 1 }, CancellationToken.None);
        string legacyFrame = System.Text.Encoding.UTF8.GetString(legacyStream.ToArray());
        Assert.IsFalse(legacyFrame.Contains("measured_y_ticks", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InvalidYTicksAreRejectedAndYOnlyGroupingIsAvailable()
    {
        foreach (int[] ticks in new[] { new[] { 20, 20 }, new[] { 50, 20 }, new[] { 9 }, new[] { 81 }, Enumerable.Range(0, 129).ToArray() })
            Assert.IsFalse((Request() with { MeasuredYTicks = ticks }).ValidMeasuredYTicks());
        var yOnly = CalibrationOcrSession.CreateMeasuredTickGrouping(Request() with { MeasuredYTicks = [10, 40, 80] });
        Assert.IsNotNull(yOnly);
        Assert.AreEqual(0, yOnly.XTicks.Count);
        CollectionAssert.AreEqual(ExpectedYTicks, yOnly.YTicks!.ToArray());
        Assert.AreEqual(10d, yOnly.PlotLeft);
        Assert.IsNull(CalibrationOcrSession.CreateMeasuredTickGrouping(Request()));
        Assert.IsNull(CalibrationOcrSession.CreateMeasuredTickGrouping(Request() with { MeasuredYTicks = [10, 40] }));
        var xOnly = CalibrationOcrSession.CreateMeasuredTickGrouping(Request() with { MeasuredXTicks = [20, 50] });
        Assert.IsNotNull(xOnly);
        Assert.IsNull(xOnly.YTicks);
        Assert.IsNull(xOnly.PlotLeft);
    }

    [TestMethod]
    public async Task InvalidYTicksAreSnapshottedBeforeSchedulingAndNeverStartWorker()
    {
        int[] ticks = [20, 20];
        CalibrationOcrRequest request = Request() with { MeasuredYTicks = ticks };
        await using var client = new CalibrationOcrWorkerClient(Path.GetFullPath("missing-worker.exe"), Path.GetFullPath("missing-assets"));
        Task<CalibrationOcrResponse> pending = client.SuggestAsync(request, new byte[] { 1 }, CancellationToken.None);
        ticks[1] = 50;
        CalibrationOcrResponse result = await pending;
        Assert.AreEqual(CalibrationOcrReasonCodes.InvalidInput, result.FailureCode);
        CollectionAssert.AreEqual(DuplicateTicks, result.Request!.MeasuredYTicks!);
        Assert.IsTrue(CalibrationOcrWorkerClient.IsValidResponse(result.Request, result));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, result));
    }

    [TestMethod]
    public void InvalidTicksAreRejected()
    {
        foreach (int[] ticks in new[] { new[] { 20, 20 }, new[] { 50, 20 }, new[] { 9 }, new[] { 91 }, Enumerable.Range(0, 129).ToArray() })
            Assert.IsFalse((Request() with { MeasuredXTicks = ticks }).ValidMeasuredXTicks());
    }

    [TestMethod]
    public async Task InvalidTicksAreSnapshottedBeforeSchedulingAndNeverStartWorker()
    {
        int[] ticks = [20, 20];
        CalibrationOcrRequest request = Request() with { MeasuredXTicks = ticks };
        await using var client = new CalibrationOcrWorkerClient(Path.GetFullPath("missing-worker.exe"), Path.GetFullPath("missing-assets"));
        Task<CalibrationOcrResponse> pending = client.SuggestAsync(request, new byte[] { 1 }, CancellationToken.None);
        ticks[1] = 50;
        CalibrationOcrResponse result = await pending;
        Assert.AreEqual(CalibrationOcrReasonCodes.InvalidInput, result.FailureCode);
        CollectionAssert.AreEqual(DuplicateTicks, result.Request!.MeasuredXTicks!);
        Assert.IsTrue(CalibrationOcrWorkerClient.IsValidResponse(result.Request, result));
        Assert.IsFalse(CalibrationOcrWorkerClient.IsValidResponse(request, result));
    }

    private static CalibrationOcrRequest Request() => new("request", new string('a', 64), new string('b', 64),
        new string('c', 64), 100, 100, new CalibrationOcrPlot(10, 10, 90, 80));
}
