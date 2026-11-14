// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using GraphReader.App.Services;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationOcrMeasuredTicksTests
{
    private static readonly int[] DuplicateTicks = [20, 20];
    [TestMethod]
    public async Task OptionalTicksRoundTripAndEchoCompareByValue()
    {
        var request = Request() with { MeasuredXTicks = [20, 50, 80] };
        using var stream = new MemoryStream();
        await CalibrationOcrWire.WriteRequestAsync(stream, request, new byte[] { 1 }, CancellationToken.None);
        stream.Position = 0;
        CalibrationOcrWorkItem? item = await CalibrationOcrWire.ReadRequestAsync(stream, CancellationToken.None);
        Assert.IsNotNull(item);
        Assert.IsTrue(request.Matches(item.Request));
        Assert.IsFalse(request.Matches(item.Request with { MeasuredXTicks = [20, 50, 81] }));
        Assert.IsTrue(item.Request.ValidMeasuredXTicks());
        Assert.IsTrue(Request().ValidMeasuredXTicks());
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
